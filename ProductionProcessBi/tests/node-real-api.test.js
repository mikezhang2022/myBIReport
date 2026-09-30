'use strict';

// Disposable integration test: drive the REAL ASP.NET Core API through the Node
// reverse proxy (node-server.js), not the in-process mock used by
// node-server.test.js.
//
// What it does, end to end:
//   1. Pick two free loopback ports (one for .NET, one for Node).
//   2. Create a throwaway content root with its own `data/` directory so the
//      project's real `data/` is never touched.
//   3. Start the real API on loopback only (ASPNETCORE_URLS + ASPNETCORE_CONTENTROOT).
//   4. Start the Node proxy in-process against that API.
//   5. Through Node: set up a temporary admin, log in, and create the fixture
//      report `整机查询部件信息`, then GET /api/report-definitions and assert the
//      fixture is visible for the authorized (admin) user.
//   6. Tear down: stop Node, kill the .NET process tree, remove the temp dir.
//
// It is skipped automatically when `dotnet` is unavailable or SKIP_INTEGRATION is
// set. If the real backend cannot be started for an environment reason, the test
// fails with the exact reason rather than passing silently.

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const net = require('node:net');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

const { createServer } = require('../node-server.js');

const PROJECT_ROOT = path.join(__dirname, '..');
const DOTNET = process.env.DOTNET ?? 'dotnet';
const DLL = path.join(PROJECT_ROOT, 'bin', 'Release', 'net8.0', 'ProductionProcessBi.dll');

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

function httpRequest(authority, urlPath, { method = 'GET', headers = {}, body = null } = {}) {
  const { hostname, port } = authority;
  return new Promise((resolve, reject) => {
    const req = http.request(
      { host: hostname, port, path: urlPath, method, headers },
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

function killTree(child) {
  if (!child || child.exitCode !== null) return;
  const pid = child.pid;
  if (process.platform === 'win32') {
    spawn('taskkill', ['/pid', String(pid), '/T', '/F'], { stdio: 'ignore' });
  } else {
    try {
      process.kill(-pid, 'SIGKILL');
    } catch {
      try { child.kill('SIGKILL'); } catch { /* already gone */ }
    }
  }
}

test('real ASP.NET API reachable through the Node proxy serves the fixture report', { timeout: 240000 }, async (t) => {
  // --- Preconditions -------------------------------------------------------
  let dotnetVersion = null;
  try {
    dotnetVersion = await new Promise((resolve) => {
      const p = spawn(DOTNET, ['--version'], { stdio: ['ignore', 'pipe', 'ignore'] });
      let out = '';
      p.stdout.on('data', (d) => (out += d));
      p.on('error', () => resolve(null));
      p.on('close', () => resolve(out.trim() || null));
    });
  } catch {
    dotnetVersion = null;
  }

  if (!dotnetVersion) {
    t.skip('dotnet executable not available; real-API integration not executed');
    return;
  }
  if (process.env.SKIP_INTEGRATION) {
    t.skip('SKIP_INTEGRATION set; real-API integration not executed');
    return;
  }
  if (!fs.existsSync(DLL)) {
    // Build if the precompiled output is missing.
    await new Promise((resolve, reject) =>
      spawn(DOTNET, ['build', '-c', 'Release', '--nologo'], { cwd: PROJECT_ROOT, stdio: 'ignore' })
        .on('error', reject)
        .on('close', (code) => (code === 0 ? resolve() : reject(new Error(`dotnet build failed (${code})`))))
    );
  }
  if (!fs.existsSync(DLL)) {
    t.skip('could not build the ASP.NET app; real-API integration not executed');
    return;
  }

  // --- Temporary isolated content root (own data/) -------------------------
  // The real API mounts Frontend/Admin via PhysicalFileProvider at startup,
  // which requires those directories to exist even though this test talks to
  // the API through the Node proxy and never needs the .NET static assets.
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'ppbi-real-'));
  fs.mkdirSync(path.join(tempRoot, 'data'), { recursive: true });
  fs.mkdirSync(path.join(tempRoot, 'data', 'auth-keys'), { recursive: true });
  fs.mkdirSync(path.join(tempRoot, 'Frontend'), { recursive: true });
  fs.mkdirSync(path.join(tempRoot, 'Admin'), { recursive: true });

  const apiPort = await freePort();
  const nodePort = await freePort();

  let dotnetProc;
  let server;
  let failed = null;

  try {
    // --- Start the real API on loopback only -------------------------------
    dotnetProc = spawn(
      DOTNET,
      [DLL],
      {
        cwd: PROJECT_ROOT,
        detached: process.platform !== 'win32',
        env: {
          ...process.env,
          ASPNETCORE_CONTENTROOT: tempRoot,
          ASPNETCORE_URLS: `http://127.0.0.1:${apiPort}`,
        },
      }
    );
    let dotnetStderr = '';
    dotnetProc.stderr.on('data', (d) => (dotnetStderr += d));

    const apiAuthority = { hostname: '127.0.0.1', port: apiPort };

    // Wait until the API is ready (setupRequired should be true on an empty data dir).
    const deadline = Date.now() + 180000;
    let ready = false;
    while (Date.now() < deadline) {
      try {
        const status = await httpRequest(apiAuthority, '/api/auth/status');
        if (status.status === 200) {
          const payload = JSON.parse(status.body);
          assert.equal(payload.setupRequired, true, 'fresh temp data dir should require setup');
          ready = true;
          break;
        }
      } catch {
        // Not up yet; keep polling.
      }
      await new Promise((r) => setTimeout(r, 1000));
    }
    if (!ready) {
      throw new Error(`real API did not become ready on 127.0.0.1:${apiPort}. dotnet stderr:\n${dotnetStderr}`);
    }

    // --- Start the Node proxy in-process against the real API --------------
    server = createServer({ apiOrigin: `http://127.0.0.1:${apiPort}` });
    await new Promise((resolve) => server.listen(nodePort, '127.0.0.1', resolve));
    const nodeAuthority = { hostname: '127.0.0.1', port: nodePort };

    // --- Through Node: set up a temporary admin ----------------------------
    const adminUser = `it_${Date.now().toString(36)}`;
    const adminPass = 'it-pass-01';
    const setupRes = await httpRequest(nodeAuthority, '/api/auth/setup', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: adminUser, displayName: 'IT Temp Admin', password: adminPass }),
    });
    assert.equal(setupRes.status, 200, `setup should succeed (got ${setupRes.status}: ${setupRes.body})`);

    // --- Through Node: log in (exercises the login path via the proxy) -----
    const loginRes = await httpRequest(nodeAuthority, '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: adminUser, password: adminPass }),
    });
    assert.equal(loginRes.status, 200, `login should succeed (got ${loginRes.status}: ${loginRes.body})`);
    const sessionCookie = loginRes.setCookies.find((c) => c.startsWith('ProductionProcessBi.Session='));
    assert.ok(sessionCookie, `login should set the session cookie (got: ${JSON.stringify(loginRes.setCookies)})`);
    const cookieHeader = sessionCookie.split(';')[0];

    // --- Through Node: create the fixture report 整机查询部件信息 ----------
    const fixtureId = 'product-parts-info';
    const fixtureName = '整机查询部件信息';
    const createRes = await httpRequest(nodeAuthority, '/api/report-definitions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Cookie: cookieHeader },
      body: JSON.stringify({
        id: fixtureId,
        category: '集成测试',
        name: fixtureName,
        queryType: 'product-trace',
        conditions: [],
        displayFields: ['sn', 'modelCode'],
        enabled: true,
        sqlText: 'SELECT 1 AS x',
      }),
    });
    assert.equal(createRes.status, 201, `fixture report creation should succeed (got ${createRes.status}: ${createRes.body})`);

    // --- Through Node: fetch report-definitions as the authorized user -----
    const listRes = await httpRequest(nodeAuthority, '/api/report-definitions', {
      headers: { Cookie: cookieHeader },
    });
    assert.equal(listRes.status, 200, `report list should be reachable (got ${listRes.status}: ${listRes.body})`);
    const reports = JSON.parse(listRes.body);
    const names = reports.map((r) => r.name);
    assert.ok(
      names.includes(fixtureName),
      `fixture report "${fixtureName}" should be visible to the authorized user; saw: ${JSON.stringify(names)}`
    );
  } catch (err) {
    failed = err;
  } finally {
    // --- Tear down: stop Node, kill .NET, remove ONLY the temp dir --------
    if (server) await new Promise((resolve) => server.close(resolve));
    if (dotnetProc) killTree(dotnetProc);
    try {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    } catch {
      // Best-effort cleanup; ignore removal races.
    }
  }

  if (failed) throw failed;
});
