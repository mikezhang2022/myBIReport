'use strict';

// Scoped regression test for the proxy SSRF / open-proxy fix in node-server.js.
//
// The vulnerability: `createServer` matched the `/api` route by parsing
// `req.url` with a neutral base, but `proxyApi` then built the upstream target
// from `new URL(req.url, apiOrigin)`. A client-supplied absolute-form target
// (e.g. "http://attacker/api/...") or protocol-relative target
// ("//attacker/api/...") therefore chose the upstream host/port, so the proxy
// could be aimed at an unintended server.
//
// This test proves the fix: a separately started "sentinel" server stands in for
// the attacker's upstream. Malicious absolute-form / protocol-relative request
// targets must NOT reach the sentinel, while a normal `/api` call still reaches
// the configured (legitimate) upstream. No real `data/` is touched and no
// production system is involved.

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const net = require('node:net');
const path = require('node:path');

const { createServer } = require('../node-server.js');

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

// A "sentinel" server that records every request it receives. It represents a
// server the attacker would love the proxy to forward to. If the proxy ever
// connects here, `hits` increments.
function startSentinel() {
  const state = { hits: 0, paths: [] };
  const srv = http.createServer((req, res) => {
    state.hits += 1;
    state.paths.push(req.url);
    res.writeHead(200, {
      'Content-Type': 'application/json; charset=utf-8',
      'Content-Length': 2,
    });
    res.end('{}');
  });
  return new Promise((resolve) => {
    srv.listen(0, '127.0.0.1', () =>
      resolve({ srv, port: srv.address().port, state })
    );
  });
}

// The legitimate configured upstream API.
function startLegitApi() {
  const srv = http.createServer((req, res) => {
    res.writeHead(200, {
      'Content-Type': 'application/json; charset=utf-8',
      'Content-Length': 2,
    });
    res.end('{}');
  });
  return new Promise((resolve) => {
    srv.listen(0, '127.0.0.1', () => resolve({ srv, port: srv.address().port }));
  });
}

function request(port, urlPath, { method = 'GET', headers = {} } = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request(
      { host: '127.0.0.1', port, path: urlPath, method, headers },
      (res) => {
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () =>
          resolve({
            status: res.statusCode,
            body: Buffer.concat(chunks).toString('utf8'),
          })
        );
      }
    );
    req.on('error', reject);
    req.end();
  });
}

let sentinel;
let legit;
let server;
let nodePort;

test.before(async () => {
  sentinel = await startSentinel();
  legit = await startLegitApi();
  server = createServer({
    frontendRoot: FRONTEND_ROOT,
    adminRoot: ADMIN_ROOT,
    apiOrigin: `http://127.0.0.1:${legit.port}`,
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  nodePort = server.address().port;
});

test.after(() => {
  if (server) server.close();
  if (legit && legit.srv) legit.srv.close();
  if (sentinel && sentinel.srv) sentinel.srv.close();
});

test('absolute-form target is rejected and never reaches the sentinel', async () => {
  const before = sentinel.state.hits;
  const res = await request(
    nodePort,
    `http://127.0.0.1:${sentinel.port}/api/auth/status`
  );
  // Rejected outright as a malformed request target.
  assert.equal(res.status, 400, 'absolute-form target must be rejected with 400');
  // The attacker server must never have been contacted.
  assert.equal(sentinel.state.hits, before, 'sentinel must not be hit by absolute-form target');
});

test('protocol-relative target is rejected and never reaches the sentinel', async () => {
  const before = sentinel.state.hits;
  const res = await request(
    nodePort,
    `//127.0.0.1:${sentinel.port}/api/auth/status`
  );
  assert.equal(res.status, 400, 'protocol-relative target must be rejected with 400');
  assert.equal(
    sentinel.state.hits,
    before,
    'sentinel must not be hit by protocol-relative target'
  );
});

test('absolute-form target with https scheme is rejected', async () => {
  const before = sentinel.state.hits;
  const res = await request(
    nodePort,
    `https://127.0.0.1:${sentinel.port}/api/auth/status`
  );
  assert.equal(res.status, 400);
  assert.equal(sentinel.state.hits, before, 'sentinel must not be hit by https absolute-form target');
});

test('normal /api call still reaches the configured upstream, not the sentinel', async () => {
  const sentinelBefore = sentinel.state.hits;
  const res = await request(nodePort, '/api/auth/status');
  assert.equal(res.status, 200);
  // Only the legitimate configured upstream handled it; the sentinel is untouched.
  assert.equal(sentinel.state.hits, sentinelBefore, 'sentinel must remain untouched on normal calls');
});

test('query string is preserved on a normal /api call', async () => {
  const res = await request(nodePort, '/api/report-definitions?kind=all&limit=5');
  assert.equal(res.status, 200);
});
