'use strict';

// Scoped checks for node-server.js. These run entirely in Node with a disposable
// in-process mock of the ASP.NET API; they never touch the real `data/` directory
// and do not use Python. The static assertions read the real Frontend/Admin asset
// folders (read-only) so the deny-list behavior is exercised against real files.

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const net = require('node:net');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFile } = require('node:child_process');

const { createServer, validateApiOrigin, safeFilePath } = require('../node-server.js');

const PROJECT_ROOT = path.join(__dirname, '..');
const FRONTEND_ROOT = path.join(PROJECT_ROOT, 'Frontend');
const ADMIN_ROOT = path.join(PROJECT_ROOT, 'Admin');

function freePort() {
  return new Promise((resolve, reject) => {
    const srv = net.createServer();
    srv.listen(0, '127.0.0.1', () => {
      const port = srv.address().port;
      srv.close(() => resolve(port));
    });
    srv.on('error', reject);
  });
}

function parseCookies(header) {
  const out = {};
  if (!header) return out;
  for (const part of header.split(';')) {
    const idx = part.indexOf('=');
    if (idx === -1) continue;
    out[part.slice(0, idx).trim()] = part.slice(idx + 1).trim();
  }
  return out;
}

// A minimal stand-in for the ASP.NET API that performs cookie-based role
// filtering, matching the behavior the proxy must preserve.
function startMockApi() {
  const srv = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const cookies = parseCookies(req.headers.cookie);
    const session = cookies['ppbi'];

    const send = (status, obj, setCookie) => {
      const body = JSON.stringify(obj);
      const headers = {
        'Content-Type': 'application/json; charset=utf-8',
        'Content-Length': Buffer.byteLength(body),
      };
      if (setCookie) headers['Set-Cookie'] = setCookie;
      res.writeHead(status, headers);
      res.end(body);
    };

    if (url.pathname === '/api/auth/status') return send(200, { setupRequired: false });
    if (url.pathname === '/api/auth/login' && req.method === 'POST') {
      let data = '';
      req.on('data', (c) => (data += c));
      req.on('end', () => {
        const body = JSON.parse(data || '{}');
        const received = req.headers['x-test'] || null;
        if (body.username === 'admin') {
          return send(200, { role: 'system-admin', received }, 'ppbi=admin; Path=/; HttpOnly');
        }
        return send(200, { role: 'report-user', received }, 'ppbi=user; Path=/; HttpOnly');
      });
      return;
    }
    if (url.pathname === '/api/report-definitions') {
      if (session === 'admin') return send(200, [{ id: 'r1' }, { id: 'r2' }]);
      if (session === 'user') return send(200, [{ id: 'r1' }]);
      return send(200, []);
    }
    return send(404, { message: 'not found' });
  });
  return new Promise((resolve) => {
    srv.listen(0, '127.0.0.1', () => resolve({ srv, port: srv.address().port }));
  });
}

function request(port, urlPath, { method = 'GET', headers = {}, body = null } = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request(
      { host: '127.0.0.1', port, path: urlPath, method, headers },
      (res) => {
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => {
          resolve({
            status: res.statusCode,
            headers: res.headers,
            setCookies: res.headers['set-cookie'] || [],
            body: Buffer.concat(chunks).toString('utf8'),
          });
        });
      }
    );
    req.on('error', reject);
    if (body) req.write(typeof body === 'string' ? body : JSON.stringify(body));
    req.end();
  });
}

let mock;
let server;
let nodePort;

test.before(async () => {
  mock = await startMockApi();
  server = createServer({
    frontendRoot: FRONTEND_ROOT,
    adminRoot: ADMIN_ROOT,
    apiOrigin: `http://127.0.0.1:${mock.port}`,
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  nodePort = server.address().port;
});

test.after(() => {
  if (server) server.close();
  if (mock && mock.srv) mock.srv.close();
});

// ---------------------------------------------------------------------------
// Static routes
// ---------------------------------------------------------------------------

test('serves the Frontend index at /', async () => {
  const res = await request(nodePort, '/');
  assert.equal(res.status, 200);
  assert.match(res.headers['content-type'], /text\/html/);
  assert.match(res.body, /<html/i);
});

test('serves the Admin index at /admin/', async () => {
  const res = await request(nodePort, '/admin/');
  assert.equal(res.status, 200);
  assert.match(res.headers['content-type'], /text\/html/);
  assert.match(res.body, /<html/i);
});

test('serves the Admin default document at /admin', async () => {
  const res = await request(nodePort, '/admin');
  assert.equal(res.status, 200);
  assert.match(res.headers['content-type'], /text\/html/);
});

test('serves a real Frontend asset with a JS mime type', async () => {
  const res = await request(nodePort, '/site.js');
  assert.equal(res.status, 200);
  assert.match(res.headers['content-type'], /javascript/);
});

test('serves a real Admin asset', async () => {
  const res = await request(nodePort, '/admin/site.js');
  assert.equal(res.status, 200);
  assert.match(res.headers['content-type'], /javascript/);
});

test('returns 404 for a missing asset', async () => {
  const res = await request(nodePort, '/does-not-exist.xyz');
  assert.equal(res.status, 404);
});

// ---------------------------------------------------------------------------
// Traversal / source / config / data blocking
// ---------------------------------------------------------------------------

test('blocks the Node server script itself', async () => {
  const res = await request(nodePort, '/node-server.js');
  assert.equal(res.status, 403);
});

test('blocks Python source inside Frontend', async () => {
  const res = await request(nodePort, '/server.py');
  assert.equal(res.status, 403);
});

test('blocks PowerShell scripts by extension', async () => {
  const res = await request(nodePort, '/start-frontend.ps1');
  assert.equal(res.status, 403);
});

test('blocks README files', async () => {
  const res = await request(nodePort, '/README.md');
  assert.equal(res.status, 403);
});

test('safeFilePath rejects path traversal out of the root', () => {
  assert.equal(safeFilePath(FRONTEND_ROOT, '../Program.cs'), null);
  assert.equal(safeFilePath(FRONTEND_ROOT, '../data/users.json'), null);
  assert.equal(safeFilePath(FRONTEND_ROOT, '/../Admin/permissions.js'), null);
  assert.equal(safeFilePath(ADMIN_ROOT, '..\\..\\data\\users.json'), null);
});

test('safeFilePath allows in-root files', () => {
  assert.ok(safeFilePath(FRONTEND_ROOT, '/index.html').endsWith('index.html'));
  assert.ok(safeFilePath(ADMIN_ROOT, 'site.js').endsWith('site.js'));
});

test('encoded traversal toward data/ is not served', async () => {
  // %2e%2e decodes to ".."; even after URL normalization this must not reach
  // the project-level data/ directory.
  const res = await request(nodePort, '/%2e%2e/data/users.json');
  assert.notEqual(res.status, 200);
});

test('malformed percent-encoding is rejected without crashing the server', async () => {
  // /%ZZ is invalid percent-encoding and previously threw from
  // decodeURIComponent inside safeFilePath, terminating the process. It must now
  // be answered with a client error while the server keeps serving.
  const bad = await request(nodePort, '/%ZZ');
  assert.ok(bad.status >= 400 && bad.status < 500, `expected client error, got ${bad.status}`);

  // A subsequent valid request must still succeed: the server is alive.
  const after = await request(nodePort, '/');
  assert.equal(after.status, 200);
  assert.match(after.body, /<html/i);
});

// ---------------------------------------------------------------------------
// API proxy: methods, bodies, headers, status, cookies, role filtering
// ---------------------------------------------------------------------------

test('proxies GET /api/auth/status with status and body preserved', async () => {
  const res = await request(nodePort, '/api/auth/status');
  assert.equal(res.status, 200);
  assert.match(res.headers['content-type'], /json/);
  assert.deepEqual(JSON.parse(res.body), { setupRequired: false });
});

test('proxies POST body, custom header, and Set-Cookie', async () => {
  const res = await request(nodePort, '/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'x-test': 'yes' },
    body: JSON.stringify({ username: 'admin', password: 'ab' }),
  });
  assert.equal(res.status, 200);
  const parsed = JSON.parse(res.body);
  assert.equal(parsed.role, 'system-admin');
  // The custom header was forwarded to the upstream.
  assert.equal(parsed.received, 'yes');
  // The upstream Set-Cookie reached the browser.
  assert.ok(res.setCookies.some((c) => c.includes('ppbi=admin')));
});

test('role-filtered report list works for admin through the proxy', async () => {
  const login = await request(nodePort, '/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: 'admin', password: 'ab' }),
  });
  const cookie = login.setCookies.find((c) => c.startsWith('ppbi=')).split(';')[0];

  const res = await request(nodePort, '/api/report-definitions', { headers: { Cookie: cookie } });
  assert.equal(res.status, 200);
  const reports = JSON.parse(res.body);
  assert.deepEqual(reports.map((r) => r.id).sort(), ['r1', 'r2']);
});

test('role-filtered report list works for report-user through the proxy', async () => {
  const login = await request(nodePort, '/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: 'user', password: 'ab' }),
  });
  const cookie = login.setCookies.find((c) => c.startsWith('ppbi=')).split(';')[0];

  const res = await request(nodePort, '/api/report-definitions', { headers: { Cookie: cookie } });
  assert.equal(res.status, 200);
  const reports = JSON.parse(res.body);
  // The proxy preserves the cookie, so the upstream returns only assigned reports.
  assert.deepEqual(reports.map((r) => r.id), ['r1']);
});

test('anonymous report list returns empty through the proxy', async () => {
  const res = await request(nodePort, '/api/report-definitions');
  assert.equal(res.status, 200);
  assert.deepEqual(JSON.parse(res.body), []);
});

test('non-API paths are not proxied', async () => {
  const res = await request(nodePort, '/apix/secret');
  assert.equal(res.status, 404);
});

// ---------------------------------------------------------------------------
// Upstream URL validation
// ---------------------------------------------------------------------------

test('rejects a non-origin API_ORIGIN with a path', () => {
  assert.throws(() => validateApiOrigin('http://127.0.0.1:5095/api'));
});

test('rejects a non-http API_ORIGIN', () => {
  assert.throws(() => validateApiOrigin('ftp://127.0.0.1:5095'));
});

test('rejects an API_ORIGIN with credentials', () => {
  assert.throws(() => validateApiOrigin('http://user:pass@127.0.0.1:5095'));
});

test('accepts a plain http origin', () => {
  assert.doesNotThrow(() => validateApiOrigin('http://127.0.0.1:5095'));
});

// ---------------------------------------------------------------------------
// Publish asset presence (runs `dotnet publish` to a temp directory)
// ---------------------------------------------------------------------------

test('node-server.js is included in dotnet publish output, outside static roots', async function () {
  const dotnet = process.env.DOTNET ?? 'dotnet';
  let exeAvailable = true;
  try {
    await new Promise((resolve, reject) =>
      execFile(dotnet, ['--version'], { timeout: 30000 }, (err) => (err ? reject(err) : resolve()))
    );
  } catch {
    exeAvailable = false;
  }
  if (!exeAvailable) {
    this.skip('dotnet executable not available; publish asset presence not verified');
    return;
  }
  if (process.env.SKIP_PUBLISH) {
    this.skip('SKIP_PUBLISH set; publish asset presence not verified');
    return;
  }

  const outDir = path.join(os.tmpdir(), `ppbi-publish-${process.pid}-${Date.now()}`);
  await new Promise((resolve, reject) =>
    execFile(
      dotnet,
      ['publish', '-c', 'Release', '-o', outDir],
      { timeout: 240000, cwd: PROJECT_ROOT },
      (err, stdout, stderr) => {
        if (err) reject(new Error(`dotnet publish failed: ${stderr || stdout || err.message}`));
        else resolve();
      }
    )
  );

  try {
    const serverInRoot = fs.existsSync(path.join(outDir, 'node-server.js'));
    const frontendPresent = fs.existsSync(path.join(outDir, 'Frontend', 'index.html'));
    const adminPresent = fs.existsSync(path.join(outDir, 'Admin', 'index.html'));
    const serverInsideFrontend = fs.existsSync(path.join(outDir, 'Frontend', 'node-server.js'));
    const serverInsideAdmin = fs.existsSync(path.join(outDir, 'Admin', 'node-server.js'));

    assert.ok(serverInRoot, 'node-server.js should be at the publish root');
    assert.ok(frontendPresent, 'Frontend assets should be published');
    assert.ok(adminPresent, 'Admin assets should be published');
    assert.ok(!serverInsideFrontend, 'node-server.js must not be inside the Frontend static root');
    assert.ok(!serverInsideAdmin, 'node-server.js must not be inside the Admin static root');
  } finally {
    fs.rmSync(outDir, { recursive: true, force: true });
  }
});
