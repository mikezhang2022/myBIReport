#!/usr/bin/env node
'use strict';

// Optional Node startup for ProductionProcessBi.
//
// This is a dependency-free reverse-proxy / static host intended for LAN
// deployments. It does NOT replace the ASP.NET Core backend: it only serves the
// two browser apps (Frontend at "/", Admin at "/admin/") and proxies "/api" to
// the real API process. The .NET process must already be running (default
// http://127.0.0.1:5095) before this server is started.
//
// Run from the project root or from a `dotnet publish` output directory:
//   node node-server.js
//   HOST=0.0.0.0 PORT=5173 API_ORIGIN=http://127.0.0.1:5095 node node-server.js
//
// Only Node built-ins are used; there is no `npm install` step.

const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { URL } = require('node:url');

// Headers that must not be forwarded across a proxy hop.
const HOP_HEADERS = new Set([
  'connection',
  'content-length',
  'transfer-encoding',
  'keep-alive',
  'host',
  'upgrade',
  'proxy-authenticate',
  'proxy-authorization',
  'te',
  'trailer',
]);

// Files that must never be served, even though they live next to the browser
// assets. These are development/tooling/source files, not browser resources.
const DENY_BASENAMES = new Set([
  'node-server.js',
  'server.py',
  'start-frontend.ps1',
  'start-admin.ps1',
  'readme.md',
]);

// Extensions that are never browser assets and must not be exposed.
const DENY_EXTENSIONS = new Set(['.py', '.ps1']);

const MIME_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.ico': 'image/x-icon',
  '.webp': 'image/webp',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.ttf': 'font/ttf',
  '.txt': 'text/plain; charset=utf-8',
  '.map': 'application/json; charset=utf-8',
};

// Resolve a browser-app root, preferring the script directory (which is also the
// publish output root) and falling back to the current working directory.
function findRoot(name) {
  const candidates = [
    path.join(__dirname, name),
    path.join(process.cwd(), name),
  ];
  for (const candidate of candidates) {
    if (fs.existsSync(candidate) && fs.statSync(candidate).isDirectory()) {
      return candidate;
    }
  }
  // Fall back to the script-directory location so the startup error is clear.
  return candidates[0];
}

// Validate that the upstream API origin is exactly an HTTP(S) origin (scheme +
// host + optional port, nothing else). Anything else could let a request escape
// to an unexpected target.
function validateApiOrigin(apiOrigin) {
  let parsed;
  try {
    parsed = new URL(apiOrigin);
  } catch {
    throw new Error(
      `API_ORIGIN must be an http(s) origin such as http://127.0.0.1:5095 (got ${apiOrigin})`
    );
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    throw new Error(`API_ORIGIN must use http or https (got ${apiOrigin})`);
  }
  if (!parsed.hostname) {
    throw new Error(`API_ORIGIN must include a host (got ${apiOrigin})`);
  }
  if (parsed.username || parsed.password) {
    throw new Error(`API_ORIGIN must not include credentials (got ${apiOrigin})`);
  }
  if (parsed.pathname !== '/' || parsed.search || parsed.hash) {
    throw new Error(
      `API_ORIGIN must be an origin only, without path/query/fragment (got ${apiOrigin})`
    );
  }
  return parsed;
}

function isDeniedFile(resolvedPath) {
  const base = path.basename(resolvedPath).toLowerCase();
  if (DENY_BASENAMES.has(base)) return true;
  // Block any README-style file regardless of extension.
  if (base.startsWith('readme')) return true;
  const ext = path.extname(resolvedPath).toLowerCase();
  if (DENY_EXTENSIONS.has(ext)) return true;
  return false;
}

// Return the resolved, in-root absolute path for a relative request path, or
// null if it escapes the root (path traversal), targets a denied file, or is
// malformed percent-encoding that cannot be decoded (e.g. "/%ZZ"). The latter
// must not throw: an uncaught exception here would terminate the whole public
// Node process.
function safeFilePath(root, relativePath) {
  let decoded;
  try {
    decoded = decodeURIComponent(relativePath);
  } catch {
    return null; // malformed percent-encoding
  }
  const resolved = path.normalize(path.join(root, decoded));
  const rootWithSep = root.endsWith(path.sep) ? root : root + path.sep;
  if (resolved !== root && !resolved.startsWith(rootWithSep)) {
    return null; // traversal escape
  }
  if (isDeniedFile(resolved)) return null;
  return resolved;
}

function mimeType(filePath) {
  return MIME_TYPES[path.extname(filePath).toLowerCase()] || 'application/octet-stream';
}

function sendError(res, status, message) {
  const body = `${status} ${message}\n`;
  res.writeHead(status, {
    'Content-Type': 'text/plain; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
  });
  res.end(body);
}

function serveStatic(res, filePath) {
  fs.stat(filePath, (err, stats) => {
    if (err || !stats.isFile()) {
      sendError(res, 404, 'Not Found');
      return;
    }
    const headers = {
      'Content-Type': mimeType(filePath),
      'Content-Length': stats.size,
    };
    // Keep HTML/JS fresh on LAN clients so they don't cache a stale build.
    if (/\.(html|js)$/i.test(filePath)) {
      headers['Cache-Control'] = 'no-cache';
    }
    res.writeHead(200, headers);
    fs.createReadStream(filePath).pipe(res);
  });
}

// Proxy an API request to the upstream ASP.NET origin, preserving method, body,
// status, headers, and cookies so login and role-based report authorization work
// from a LAN client.
function proxyApi(req, res, apiOrigin) {
  // Only origin-form request targets are permitted. An absolute-form target
  // such as "http://evil.example/api/auth/status" or a protocol-relative target
  // such as "//evil.example/api/auth/status" would otherwise let a client pick
  // the upstream host/port/scheme, turning this proxy into an SSRF / open
  // proxy. Reject them outright before any upstream connection is opened.
  if (/^(?:[a-zA-Z][a-zA-Z0-9+.\-]*:|\/\/)/.test(req.url)) {
    sendError(res, 400, 'Bad Request');
    return;
  }

  // Parse only the path + query with a neutral base. The destination origin is
  // ALWAYS the configured, validated apiOrigin — never anything derived from the
  // client request line. This is what closes the SSRF hole: an absolute or
  // protocol-relative target is rejected above, so the host/port below can only
  // come from apiOrigin.
  const parsed = new URL(req.url, 'http://localhost');
  const pathname = parsed.pathname;
  // The only acceptable paths are "/api" and "/api/...". Reject anything else.
  if (pathname !== '/api' && !pathname.startsWith('/api/')) {
    sendError(res, 404, 'API route not found');
    return;
  }

  const target = new URL(pathname + parsed.search, apiOrigin);

  const bodyChunks = [];
  req.on('data', (chunk) => bodyChunks.push(chunk));
  req.on('end', () => {
    const body = Buffer.concat(bodyChunks);
    const forwardHeaders = {};
    for (const [key, value] of Object.entries(req.headers)) {
      if (!HOP_HEADERS.has(key.toLowerCase())) {
        forwardHeaders[key] = value;
      }
    }

    const options = {
      method: req.method,
      hostname: target.hostname,
      port: target.port || (target.protocol === 'https:' ? 443 : 80),
      path: target.pathname + target.search,
      headers: forwardHeaders,
    };

    const client = target.protocol === 'https:' ? require('node:https') : http;
    const upstream = client.request(options, (upstreamRes) => {
      const responseHeaders = {};
      const setCookies = [];
      for (const [key, value] of Object.entries(upstreamRes.headers)) {
        const lower = key.toLowerCase();
        if (HOP_HEADERS.has(lower)) continue;
        if (lower === 'set-cookie') {
          if (Array.isArray(value)) setCookies.push(...value);
          else setCookies.push(value);
          continue;
        }
        responseHeaders[key] = value;
      }
      if (setCookies.length) responseHeaders['Set-Cookie'] = setCookies;

      const outChunks = [];
      upstreamRes.on('data', (chunk) => outChunks.push(chunk));
      upstreamRes.on('end', () => {
        const out = Buffer.concat(outChunks);
        responseHeaders['Content-Length'] = out.length;
        res.writeHead(upstreamRes.statusCode || 502, responseHeaders);
        res.end(out);
      });
    });

    upstream.on('error', () => {
      sendError(res, 502, 'Cannot reach API upstream');
    });

    if (body.length) upstream.write(body);
    upstream.end();
  });
}

function createServer(options = {}) {
  const frontendRoot = options.frontendRoot || findRoot('Frontend');
  const adminRoot = options.adminRoot || findRoot('Admin');
  const apiOrigin = validateApiOrigin(options.apiOrigin || process.env.API_ORIGIN || 'http://127.0.0.1:5095');

  return http.createServer((req, res) => {
    const parsedUrl = new URL(req.url, 'http://localhost');
    const pathname = parsedUrl.pathname;

    // API routes are proxied first.
    if (pathname === '/api' || pathname.startsWith('/api/')) {
      proxyApi(req, res, apiOrigin);
      return;
    }

    // Admin app lives under /admin; everything else is the Frontend app.
    let root = frontendRoot;
    let relative = pathname;
    if (pathname === '/admin' || pathname.startsWith('/admin/')) {
      root = adminRoot;
      relative = pathname === '/admin' ? '/' : pathname.slice('/admin'.length);
    }

    if (relative === '/' || relative === '') {
      relative = '/index.html';
    }

    const filePath = safeFilePath(root, relative);
    if (!filePath) {
      sendError(res, 403, 'Forbidden');
      return;
    }
    serveStatic(res, filePath);
  });
}

// Allow the test harness to import the factory without starting a listener.
module.exports = { createServer, validateApiOrigin, safeFilePath, isDeniedFile };

if (require.main === module) {
  const host = process.env.HOST || '0.0.0.0';
  const port = parseInt(process.env.PORT || '5173', 10);
  const apiOrigin = process.env.API_ORIGIN || 'http://127.0.0.1:5095';

  let server;
  try {
    server = createServer({ apiOrigin });
  } catch (err) {
    console.error(`node-server.js: ${err.message}`);
    process.exit(1);
  }

  server.listen(port, host, () => {
    console.log(`ProductionProcessBi frontend: http://${host}:${port}/`);
    console.log(`ProductionProcessBi admin:     http://${host}:${port}/admin/`);
    console.log(`BI API upstream:               ${apiOrigin}`);
  });

  server.on('error', (err) => {
    console.error(`node-server.js: failed to listen on ${host}:${port}: ${err.message}`);
    process.exit(1);
  });
}
