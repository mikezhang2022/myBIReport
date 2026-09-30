const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'Admin', 'permissions.js'), 'utf8');

async function firstPermissionRequest(origin, override) {
  const requests = [];
  const denied = { hidden: true };
  const window = { location: { origin }, PROCESS_BI_API_BASE: override };
  const document = {
    querySelectorAll: () => [],
    querySelector: selector => selector === '#permission-denied' ? denied : null
  };
  const fetch = async url => {
    requests.push(url);
    return { ok: true, json: async () => ({ role: 'report-user' }) };
  };
  vm.runInNewContext(source, { window, document, fetch, console });
  await new Promise(resolve => setImmediate(resolve));
  return requests[0];
}

test('permissions API follows the LAN browser origin and its port', async () => {
  assert.equal(
    await firstPermissionRequest('http://192.168.1.80:8080'),
    'http://192.168.1.80:8080/api/auth/me'
  );
});

test('permissions API respects an explicitly configured origin', async () => {
  assert.equal(
    await firstPermissionRequest('http://192.168.1.80:8080', 'https://bi.example.test'),
    'https://bi.example.test/api/auth/me'
  );
});
