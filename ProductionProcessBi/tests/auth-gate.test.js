'use strict';

// Regression test for the missing login gate after a 401 on /api/auth/me.
//
// Reproduces the reported LAN symptom with a disposable in-process mock of the ASP.NET
// API and a real DOM (jsdom) that loads the actual Frontend/auth.js + site.js. It asserts
// that an anonymous/expired session:
//   1. always sees a visible login gate (never a permanent loading screen), and
//   2. never leaves the report navigation stuck on "正在载入报表目录…", and
//   3. does NOT trigger /api/report-definitions before authentication (report authorization preserved).
//
// jsdom is an OPTIONAL dev dependency (npm install --no-save jsdom). If it is not present
// the suite is skipped rather than failing the whole `node --test` run.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const os = require('node:os');

let JSDOM = null;
try {
  JSDOM = require('jsdom').JSDOM;
} catch {
  JSDOM = null;
}

const PROJECT_ROOT = path.join(__dirname, '..');
const FRONTEND_ROOT = path.join(PROJECT_ROOT, 'Frontend');

function readFrontend(name) {
  return fs.readFileSync(path.join(FRONTEND_ROOT, name), 'utf8');
}

// Build a self-contained HTML document: inline the three boot scripts and drop the
// external echarts CDN tag (the harness has no network). The page origin is set to the
// mock API so every /api request resolves against it.
function buildHtml() {
  let html = readFrontend('index.html');
  html = html.replace(/<script src="https:\/\/cdn\.jsdelivr\.net[^"]*"><\/script>/, '');
  const apiConfig = readFrontend('api-config.js');
  const auth = readFrontend('auth.js');
  const site = readFrontend('site.js');
  html = html
    .replace('<script src="/api-config.js"></script>', `<script>${apiConfig}</script>`)
    .replace('<script src="/auth.js"></script>', `<script>${auth}</script>`)
    .replace('<script src="/site.js"></script>', `<script>${site}</script>`);
  return html;
}

function startMockApi(handler) {
  const requests = [];
  const server = http.createServer((req, res) => {
    requests.push(req.url);
    handler(req, res);
  });
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      const port = server.address().port;
      resolve({ server, requests, origin: `http://127.0.0.1:${port}` });
    });
  });
}

function sendJson(res, status, body) {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(payload),
    'Connection': 'close',
  });
  res.end(payload);
}

const delay = (ms) => new Promise((r) => setTimeout(r, ms));

function closeApi(api, dom) {
  if (dom && dom.window && typeof dom.window.close === 'function') dom.window.close();
  if (typeof api.server.closeAllConnections === 'function') api.server.closeAllConnections();
  api.server.close();
}

async function boot(handler, settleMs = 800) {
  // startMockApi returns the *actual* origin (port 0 resolves to a real ephemeral
  // port). The JSDOM page origin MUST match that origin so every /api request the
  // loaded Frontend code makes resolves against our in-process mock — otherwise the
  // page just hits a dead placeholder and the test passes on a connection failure
  // rather than on the intended 401 / authenticated flow.
  const api = await startMockApi(handler);
  const html = buildHtml();
  const dom = new JSDOM(html, {
    url: `${api.origin}/`,
    runScripts: 'dangerously',
    beforeParse(window) {
      // jsdom has no fetch; route requests to the real Node fetch (absolute URLs only).
      window.fetch = (input, init) => {
        let url = input;
        if (typeof url === 'string' && url.startsWith('/')) url = window.location.origin + url;
        return globalThis.fetch(url, init);
      };
      window.matchMedia = () => ({ matches: false, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} });
    },
  });
  // Allow the async boot flow (status -> me) to settle. Tests that exercise a
  // deliberately slow response must pass a larger settleMs.
  await delay(settleMs);
  return { dom, api };
}

function gateVisible(window) {
  const gate = window.document.querySelector('#auth-gate');
  return !!gate && !gate.hidden;
}

const suiteOpts = JSDOM ? {} : { skip: 'jsdom not installed (npm install --no-save jsdom)' };

test('anonymous session with 401 on /api/auth/me shows the login gate and clears the loading state', suiteOpts, async () => {
  const { dom, api } = await boot((req, res) => {
    if (req.url === '/api/auth/status') return sendJson(res, 200, { setupRequired: false });
    if (req.url === '/api/auth/me') return sendJson(res, 401, { message: '未登录。' });
    return sendJson(res, 401, { message: '未授权。' });
  });

  const window = dom.window;
  assert.equal(gateVisible(window), true, 'login gate must be visible after a 401');
  const gate = window.document.querySelector('#auth-gate');
  assert.equal(window.document.querySelector('#auth-title').textContent, '登录报表中心');
  const nav = window.document.querySelector('#report-navigation');
  assert.equal(nav.querySelector('.navigation-loading'), null, 'navigation must not stay in the loading state');
  assert.equal(api.requests.includes('/api/auth/status'), true, 'the status probe must have been issued');
  assert.equal(api.requests.includes('/api/auth/me'), true, 'the /me probe must have been issued');
  assert.equal(api.requests.includes('/api/report-definitions'), false,
    'report directory must not be fetched before authentication (authorization preserved)');
  closeApi(api, dom);
});

test('first-run (setupRequired) shows the setup gate', suiteOpts, async () => {
  const { dom, api } = await boot((req, res) => {
    if (req.url === '/api/auth/status') return sendJson(res, 200, { setupRequired: true });
    return sendJson(res, 401, { message: '未登录。' });
  });
  const window = dom.window;
  assert.equal(gateVisible(window), true);
  assert.equal(window.document.querySelector('#auth-title').textContent, '创建管理员账号');
  closeApi(api, dom);
});

test('authenticated session hides the gate, fires auth-ready, and loads the directory', suiteOpts, async () => {
  const user = { id: 'u1', username: 'ops', displayName: '运维', role: 'report-user', reportIds: ['r1'], active: true };
  const { dom, api } = await boot((req, res) => {
    if (req.url === '/api/auth/status') return sendJson(res, 200, { setupRequired: false });
    if (req.url === '/api/auth/me') return sendJson(res, 200, user);
    if (req.url === '/api/report-definitions') return sendJson(res, 200, [{ id: 'r1', category: '质量', name: '追溯', queryType: 'product-trace', enabled: true }]);
    return sendJson(res, 401, {});
  });
  const window = dom.window;
  assert.equal(gateVisible(window), false, 'gate must be hidden when authenticated');
  assert.equal(window.processBiUser && window.processBiUser.username, 'ops');
  assert.equal(api.requests.includes('/api/auth/status'), true, 'the status probe must have been issued');
  assert.equal(api.requests.includes('/api/auth/me'), true, 'the /me probe must have been issued');
  assert.equal(api.requests.includes('/api/report-definitions'), true,
    'directory is fetched only after authentication resolves');
  // The directory loader should have rendered the authorized report into the navigation.
  const nav = window.document.querySelector('#report-navigation');
  assert.equal(nav.querySelector('.navigation-loading'), null);
  closeApi(api, dom);
});

test('failed status ping still surfaces the login gate (never a blank loading screen)', suiteOpts, async () => {
  const { dom, api } = await boot((req, res) => {
    if (req.url === '/api/auth/status') return sendJson(res, 500, {});
    return sendJson(res, 401, {});
  });
  const window = dom.window;
  assert.equal(gateVisible(window), true, 'a backend error must still show the gate, not a permanent loader');
  assert.equal(api.requests.includes('/api/auth/status'), true, 'the status probe must have been issued');
  closeApi(api, dom);
});

// Regression for the slow-success case: the /api/auth/me response is deliberately slow,
// but there is no client-side timeout that would pre-empt it. The session must NOT be
// marked unauthenticated before the real response arrives, so once /me finally returns 200
// the session upgrades to authenticated: the gate stays hidden, processBiUser is set, and
// auth-ready fires so the report directory is fetched. A premature timeout would have
// stranded the directory on a loading/error state instead of loading the reports.
test('authenticated session still resolves when /api/auth/me answers after a slow response', suiteOpts, async () => {
  const user = { id: 'u1', username: 'ops', displayName: '运维', role: 'report-user', reportIds: ['r1'], active: true };
  const { dom, api } = await boot((req, res) => {
    if (req.url === '/api/auth/status') return sendJson(res, 200, { setupRequired: false });
    if (req.url === '/api/auth/me') return setTimeout(() => sendJson(res, 200, user), 5000);
    if (req.url === '/api/report-definitions') return sendJson(res, 200, [{ id: 'r1', category: '质量', name: '追溯', queryType: 'product-trace', enabled: true }]);
    return sendJson(res, 401, {});
  }, 7000);

  const window = dom.window;
  assert.equal(gateVisible(window), false, 'gate must be hidden once the slow /me succeeds');
  assert.equal(window.processBiUser && window.processBiUser.username, 'ops', 'user must be published despite the late answer');
  assert.equal(api.requests.includes('/api/auth/status'), true, 'the status probe must have been issued');
  assert.equal(api.requests.includes('/api/auth/me'), true, 'the slow /me probe must have been issued');
  assert.equal(api.requests.includes('/api/report-definitions'), true,
    'directory is fetched after the late auth-ready fires');
  const nav = window.document.querySelector('#report-navigation');
  assert.equal(nav.querySelector('.navigation-loading'), null, 'navigation must not stay loading after late auth');
  closeApi(api, dom);
});
