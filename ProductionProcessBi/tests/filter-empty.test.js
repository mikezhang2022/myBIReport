const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { JSDOM } = require('jsdom');
const path = require('node:path');
const admin = fs.readFileSync(path.join(__dirname, '../Admin/site.js'), 'utf8');
const frontend = fs.readFileSync(path.join(__dirname, '../Frontend/site.js'), 'utf8');

test('each condition saves its allow-empty checkbox across rerenders', () => {
  const dom = new JSDOM('<div id="condition-options"><input type="checkbox" checked value="sn"><input type="checkbox" checked value="date"></div><div id="filter-config"></div>');
  const context = vm.createContext({ document: dom.window.document });
  vm.runInContext("let filterConfigs=[];const $=s=>document.querySelector(s);const esc=v=>String(v??'');" + admin.slice(admin.indexOf('function refreshFilterConfigs()'), admin.indexOf('function setupFilterConfig()')), context);
  vm.runInContext('refreshFilterConfigs()', context);
  const boxes = dom.window.document.querySelectorAll('[data-filter-allow-empty]');
  assert.equal(boxes.length, 2);
  assert.equal(boxes[0].checked, false);
  boxes[0].checked = true;
  boxes[0].onchange();
  vm.runInContext('refreshFilterConfigs()', context);
  assert.equal(dom.window.document.querySelector('[data-filter-allow-empty]').checked, true);
  assert.equal(vm.runInContext('filterConfigs[1].allowEmpty', context), false);
});

for (const value of ['', '   ']) {
  test(`required blank query (${JSON.stringify(value)}) is blocked before fetch`, async () => {
    const dom = new JSDOM('<div id="standard-message"></div><div id="standard-report"></div><input data-standard-param="sn">');
    dom.window.document.querySelector('input').value = value;
    let calls = 0;
    const context = vm.createContext({ document: dom.window.document, URLSearchParams, fetch: async () => { calls++; }, apiUrl: x=>x });
    vm.runInContext(frontend.slice(frontend.indexOf('async function queryStandard('), frontend.indexOf('async function analyzeStandardResult(')), context);
    await context.queryStandard({id:'test',conditions:['sn'],filters:[{name:'sn',label:'序列号',allowEmpty:false}]});
    assert.equal(calls, 0);
    assert.equal(dom.window.document.querySelector('#standard-message').textContent, '序列号不能为空。');
  });
}

test('optional empty query reaches the API', async () => {
  const dom = new JSDOM('<div id="standard-message"></div><div id="standard-report"></div><input data-standard-param="sn">');
  let calls = 0;
  const context = vm.createContext({ document:dom.window.document, URLSearchParams, apiUrl:x=>x, fetch:async () => { calls++;return {ok:false,json:async()=>({message:'API reached'})}; } });
  vm.runInContext(frontend.slice(frontend.indexOf('async function queryStandard('), frontend.indexOf('async function analyzeStandardResult(')), context);
  await context.queryStandard({id:'test',conditions:['sn'],filters:[{name:'sn',allowEmpty:true}]});
  assert.equal(calls,1);
});
test('rendered SQL parameter remains required when conditions is empty', async () => {
  const dom = new JSDOM('<div id="standard-message"></div><div id="standard-report"></div><input data-standard-param="sn">');
  let calls = 0;
  const context = vm.createContext({ document: dom.window.document, URLSearchParams, apiUrl: x => x, fetch: async () => { calls++; } });
  vm.runInContext(frontend.slice(frontend.indexOf('async function queryStandard('), frontend.indexOf('async function analyzeStandardResult(')), context);
  await context.queryStandard({ id: 'test', conditions: [], filters: [] });
  assert.equal(calls, 0);
  assert.equal(dom.window.document.querySelector('#standard-message').textContent, 'sn不能为空。');
  assert.equal(dom.window.document.querySelector('#standard-report').hidden, true);
});
for (const allowEmpty of [false, true]) {
  test(`SQL preview respects allowEmpty=${allowEmpty}`, async () => {
    const dom = new JSDOM('<textarea id="definition-sql">SELECT sn FROM items WHERE sn=@sn</textarea><div id="sql-preview-message"></div><div id="sql-preview-result"></div><input data-preview-parameter="sn">');
    let calls=0, submitted;
    const context=vm.createContext({document:dom.window.document, sqlParameters:['sn'], API:'', $:s=>dom.window.document.querySelector(s), refreshFilterConfigs:()=>{}, formData:()=>({filters:[{name:'sn',label:'序列号',allowEmpty}]}), fetch:async (url,options)=>{calls++;submitted=JSON.parse(options.body);return {ok:false,json:async()=>({message:'API reached'})};}});
    vm.runInContext(admin.slice(admin.indexOf('async function previewSql()'),admin.indexOf("$('#preview-sql').addEventListener")),context);
    await context.previewSql();
    assert.equal(calls,allowEmpty?1:0);
    if(allowEmpty) assert.equal(submitted.filters[0].allowEmpty,true);
    else assert.equal(dom.window.document.querySelector('#sql-preview-message').textContent,'序列号不能为空。');
  });
}
