const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const {JSDOM} = require('jsdom');
const path = require('node:path');
const admin = fs.readFileSync(path.join(__dirname,'../Admin/site.js'),'utf8');
const frontend = fs.readFileSync(path.join(__dirname,'../Frontend/site.js'),'utf8');

test('multiline condition renders a textarea and submits all lines unchanged', async () => {
  const dom = new JSDOM('<div id="export-standard"></div><table id="standard-table"></table><div id="standard-report"></div><div id="standard-ai-card"></div><div id="standard-message"></div><div id="standard-query"></div>');
  let submitted;
  const context = vm.createContext({document:dom.window.document,URLSearchParams,showView:()=>{},safe:x=>String(x),apiUrl:x=>x,fetch:async url=>{submitted=url;return {ok:false,json:async()=>({message:'test response'})};}});
  vm.runInContext(frontend.slice(frontend.indexOf('function openStandard('),frontend.indexOf('async function analyzeStandardResult(')),context);
  const definition={id:'multi',name:'test',category:'test',conditions:['sn'],filters:[{name:'sn',label:'SN',controlType:'textarea',allowEmpty:false}]};
  context.openStandard(definition);
  const field=dom.window.document.querySelector('textarea[data-standard-param="sn"]');
  assert.ok(field);
  field.value='SN001\nSN002\nSN003';
  await context.queryStandard(definition);
  assert.equal(new URL(submitted,'http://localhost').searchParams.get('sn'),field.value);
  submitted=undefined;
  field.value=' \n\n ';
  await context.queryStandard(definition);
  assert.equal(submitted,undefined);
  assert.equal(dom.window.document.querySelector('#standard-message').textContent,'SN不能为空。');
});

test('multiline control configuration survives rerender and SQL preview uses textarea', () => {
  const dom=new JSDOM('<div id="condition-options"><input type="checkbox" checked value="sn"></div><div id="filter-config"></div><div id="sql-debug"></div><div id="sql-debug-fields"></div><div id="sql-preview-message"></div><div id="sql-preview-result"></div>');
  const context=vm.createContext({document:dom.window.document});
  vm.runInContext("let filterConfigs=[];let sqlParameters=['sn'];const $=s=>document.querySelector(s);const esc=v=>String(v??'');"+admin.slice(admin.indexOf('function refreshFilterConfigs()'),admin.indexOf('function setupFilterConfig()'))+admin.slice(admin.indexOf('function renderSqlDebugPanel()'),admin.indexOf('async function previewSql()')),context);
  vm.runInContext('refreshFilterConfigs()',context);
  const select=dom.window.document.querySelector('[data-filter-type]');
  select.value='textarea';select.onchange();
  assert.equal(dom.window.document.querySelector('[data-filter-type]').value,'textarea');
  assert.ok(dom.window.document.querySelector('textarea[data-preview-parameter="sn"]'));
  assert.equal(vm.runInContext('filterConfigs[0].controlType',context),'textarea');
});
