const API_BASE = window.PROCESS_BI_API_BASE ?? `${window.location.protocol}//${window.location.hostname}:5095`;
const apiUrl = (path) => `${API_BASE}${path}`;
const get = (url) => fetch(apiUrl(url)).then(r => r.ok ? r.json() : Promise.reject(r.status));
const dateTime = new Intl.DateTimeFormat('zh-CN', { dateStyle:'short', timeStyle:'short', hour12:false });
const text = (value) => value ?? '—';
const input = document.querySelector('#sn-input');
const button = document.querySelector('#query-button');
const message = document.querySelector('#message');
const report = document.querySelector('#report');
const safe = (value) => String(value ?? '—').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
let productTraceResult = null, capacityResult = null, standardResult = null, standardQueryState = null;
function exportCsv(filename, columns, rows) { const quote = value => `"${String(value ?? '').replaceAll('"', '""')}"`; const csv = '\ufeff' + [columns.map(column => quote(column.label)).join(','), ...rows.map(row => columns.map(column => quote(row[column.key])).join(','))].join('\r\n'); const url = URL.createObjectURL(new Blob([csv], {type:'text/csv;charset=utf-8'})); const link = document.createElement('a'); link.href=url; link.download=`${filename}.csv`; link.click(); URL.revokeObjectURL(url); }

function flag(value, cls = '') { return value ? `<span class="flag ${cls}">${value}</span>` : '—'; }
function activeFields(queryType) {
  return reportDefinitions.find(item => item.id === selectedDefinitionId && item.queryType === queryType)?.displayFields ?? [];
}
function render(result) {
  productTraceResult = result;
  const p = result.product;
  const fields = activeFields('product-trace');
  document.querySelector('#product-info').innerHTML = [
    ['sn','SN', p.sn], ['status','状态', `<span class="status ${p.routeEnd ? 'done' : ''}">${p.status}</span>`], ['modelCode','型号', p.modelCode], ['moNumber','工单', p.moNumber], ['projectNo','项目', p.projectNo], ['quantity','数量', p.quantity], ['firstInTime','首次过站', dateTime.format(new Date(p.firstInTime))], ['lastInTime','最后过站', dateTime.format(new Date(p.lastInTime))], ['errorRecords','异常记录', p.errorRecords], ['ngQuantity','NG 数量', p.ngQuantity], ['reworkRecords','返工记录', p.reworkRecords], ['scrapQuantity','报废数量', p.scrapQuantity]
  ].filter(([id]) => fields.length === 0 || fields.includes(id)).map(([, label, value]) => `<div class="info-item"><span>${label}</span><strong>${value}</strong></div>`).join('');
  document.querySelector('#record-count').textContent = `共 ${result.records.length} 条过站记录`;
  document.querySelector('#records-table').innerHTML = `<thead><tr><th>序号</th><th>工序</th><th>工位</th><th>过站时间</th><th>状态</th></tr></thead><tbody>${result.records.map(row => {
    const states = [row.error ? flag('异常','error') : '', row.ngQuantity ? flag(`NG ${row.ngQuantity}`,'error') : '', row.rework ? flag('返工','warn') : '', row.scrap ? flag('报废','error') : '', row.routeEnd ? flag('流程结束','success') : ''].filter(Boolean).join(' ');
    return `<tr><td>${text(row.sequence)}</td><td>${safe(row.process)}</td><td>${safe(row.station)}</td><td>${dateTime.format(new Date(row.inTime))}</td><td>${states || '正常'}</td></tr>`;
  }).join('')}</tbody>`;
  report.hidden = false;
}
async function query() {
  const sn = input.value.trim();
  if (!sn) { message.textContent = '请输入 SN。'; input.focus(); return; }
  button.disabled = true; button.textContent = '查询中…'; message.textContent = '';
  try {
    await loadDefinitions();
    const response = await fetch(apiUrl(`/api/reports/product-trace?sn=${encodeURIComponent(sn)}`));
    const body = await response.json();
    if (!response.ok) throw new Error(body.message || '查询失败。');
    render(body); message.textContent = `已查询 SN：${body.product.sn}`;
  } catch (error) { report.hidden = true; message.textContent = error.message; }
  finally { button.disabled = false; button.textContent = '查询'; }
}
button.addEventListener('click', query);
input.addEventListener('keydown', event => { if (event.key === 'Enter') query(); });

const navigation = document.querySelector('#report-navigation');
let reportDefinitions = [];
let selectedDefinitionId = null;
function escapeHtml(value) { return safe(value); }
function showView(viewId, title, breadcrumb, subtitle) {
  document.querySelectorAll('.report-view').forEach(view => view.hidden = view.id !== viewId);
  document.querySelector('#breadcrumb-text').textContent = breadcrumb;
  document.querySelector('#report-title').textContent = title;
  document.querySelector('#report-subtitle').textContent = subtitle;
}
function openDefinition(definition) {
  selectedDefinitionId = definition.id;
  if (definition.sqlText || definition.queryType === 'standard') { openStandard(definition); renderNavigation(); return; }
  showView(`${definition.queryType}-view`, definition.name, `${definition.category} / ${definition.name}`, definition.queryType === 'capacity' ? '按日期范围查询实际过站与完成情况。' : '输入 SN，查询产品的完整流程记录。');
  if (definition.queryType === 'capacity') queryCapacity();
  renderNavigation();
}
function openStandard(definition) {
  showView('standard-view', definition.name, `${definition.category} / ${definition.name}`, '按配置的查询条件读取数据。');
  document.querySelector('#export-standard').hidden = definition.enableCsvExport === false;
  document.querySelector('#standard-table').classList.toggle('compact-table', definition.reportStyle === 'compact');
  const container = document.querySelector('#standard-query');
  const inferred = [...(definition.sqlText || '').matchAll(/[@:]([A-Za-z_][A-Za-z0-9_]*)/g)].map(match => match[1]);
  const conditions = definition.conditions?.length ? definition.conditions : [...new Set(inferred)];
  const filters = Object.fromEntries((definition.filters || []).map(filter => [filter.name, filter]));
  container.innerHTML = `<fieldset><legend>查询条件</legend><div class="standard-inputs">${conditions.map(name => { const filter=filters[name], type=filter?.controlType || (/date|time|日期|时间/i.test(name)?'date':'text'), label=filter?.label||name; return `<label>${safe(label)}${type==='select'?`<select data-standard-param="${safe(name)}"><option value="">请选择</option></select>`:`<input data-standard-param="${safe(name)}" type="${type==='date'?'date':'search'}" placeholder="请输入 ${safe(label)}">`}</label>`; }).join('')}</div></fieldset><button id="standard-query-button" type="button">查询</button>`;
  (definition.filters||[]).filter(filter=>filter.controlType==='select'&&filter.optionsSql).forEach(async filter=>{const select=container.querySelector(`[data-standard-param="${filter.name}"]`);try{const options=await fetch(apiUrl(`/api/reports/${encodeURIComponent(definition.id)}/filter-options/${encodeURIComponent(filter.name)}`)).then(r=>r.json());select.innerHTML='<option value="">请选择</option>'+options.map(item=>`<option value="${safe(item.value)}">${safe(item.label)}</option>`).join('');}catch{select.innerHTML='<option value="">选项加载失败</option>';}});
  document.querySelector('#standard-query-button').addEventListener('click', () => queryStandard(definition, 1));
}
async function queryStandard(definition, page = 1) {
  const message = document.querySelector('#standard-message'), report = document.querySelector('#standard-report');
  const query = new URLSearchParams();
  document.querySelectorAll('[data-standard-param]').forEach(input => query.set(input.dataset.standardParam, input.value));
  query.set('page', page); query.set('pageSize', 50);
  message.textContent = '查询中…';
  try {
    const response = await fetch(apiUrl(`/api/reports/${encodeURIComponent(definition.id)}/query?${query}`));
    const body = await response.json(); if (!response.ok) throw new Error(body.message || '查询失败。');
    standardResult = body;
    standardQueryState = { definition, page: body.page, hasMore: body.hasMore };
    document.querySelector('#standard-table').innerHTML = `<thead><tr>${body.columns.map(x => `<th>${safe(x)}</th>`).join('')}</tr></thead><tbody>${body.rows.map(row => `<tr>${body.columns.map(x => `<td>${safe(row[x] ?? '—')}</td>`).join('')}</tr>`).join('')}</tbody>`;
    document.querySelector('#standard-count').textContent = `第 ${body.page} 页，每页最多 ${body.pageSize} 条`;
    const pager = document.querySelector('#standard-pagination');
    pager.innerHTML = `<button type="button" data-standard-page="${body.page - 1}" ${body.page <= 1 ? 'disabled' : ''}>上一页</button><span>第 ${body.page} 页</span><button type="button" data-standard-page="${body.page + 1}" ${body.hasMore ? '' : 'disabled'}>下一页</button>`;
    pager.querySelectorAll('[data-standard-page]').forEach(button => button.addEventListener('click', () => queryStandard(definition, Number(button.dataset.standardPage))));
    report.hidden = false; message.textContent = '查询完成。';
    requestAnimationFrame(() => requestAnimationFrame(() => renderDashboard(definition.dashboardWidgets, body)));
  } catch (error) { report.hidden = true; message.textContent = error.message; }
}
function renderDashboard(widgets, result) {
  const rows = result.rows || [], columns = result.columns || [];
  const list = widgets?.length ? widgets : [];
  const values = field => rows.map(row => Number(row[field]) || 0);
  const holder = document.querySelector('#dashboard-widgets');
  holder.innerHTML = list.map(widget => {
    const width = Math.min(12, Math.max(3, Number(widget.width) || 6));
    if (widget.type === 'metric') { const sum = widget.yField ? values(widget.yField).reduce((a,b)=>a+b,0) : rows.length; return `<article class="dashboard-widget" style="--widget-width:${width}"><h3>${safe(widget.title || '指标')}</h3><div class="metric-value">${safe(sum)}</div></article>`; }
    const series = values(widget.yField || columns[1]); const max = Math.max(...series, 1);
    if (['line','bar','area','pie'].includes(widget.type)) return `<article class="dashboard-widget" style="--widget-width:${width}"><h3>${safe(widget.title || '图表')}</h3><div id="chart-${safe(widget.id)}" class="chart-host" style="height:${Math.max(180,Number(widget.height)||300)}px"></div></article>`;
    return '';
  }).join('');
  renderEcharts(list, rows, columns);
}
function renderEcharts(widgets, rows, columns) {
  if (!window.echarts) return;
  widgets.filter(widget => ['line','bar','area','pie'].includes(widget.type)).forEach(widget => {
    const host = document.querySelector(`#chart-${CSS.escape(widget.id)}`); if (!host) return;
    const x = widget.xField || columns[0], y = widget.yField || columns[1];
    const colors={blue:'#366E6B',green:'#5a8f80',orange:'#b7802a',purple:'#6d7f76'},color=colors[widget.color]||colors.blue,labels=rows.map(row=>String(row[x]??'')),values=rows.map(row=>Number(row[y])||0);const pie=widget.type==='pie'?{name:widget.title||y,type:'pie',radius:'62%',data:labels.map((name,i)=>({name,value:values[i]})),label:{show:widget.showLabel===true}}:{name:widget.title||y,type:widget.type==='area'?'line':widget.type,data:values,smooth:widget.type==='line'||widget.type==='area',showSymbol:true,areaStyle:widget.type==='area'?{opacity:.25}:undefined,label:{show:widget.showLabel===true}};const chart = echarts.init(host); chart.setOption({ color:[color], tooltip:{trigger:pie?'item':'axis'}, legend:{show:widget.showLegend!==false,bottom:0}, grid:pie?undefined:{left:55,right:25,top:35,bottom:55}, xAxis:pie?undefined:{type:'category',name:x,data:labels,axisLabel:{rotate:labels.length>6?35:0}}, yAxis:pie?undefined:{type:'value',name:y}, series:[series] }); requestAnimationFrame(()=>chart.resize());
  });
}
function renderNavigation() {
  const groups = reportDefinitions.filter(item => item.enabled).reduce((all, item) => {
    (all[item.category] ??= []).push(item); return all;
  }, {});
  const content = Object.entries(groups).map(([category, items]) => {
    const key = category.replace(/[^\w\u4e00-\u9fff]/g, '');
    return `<button class="category" type="button" data-category="${escapeHtml(key)}" aria-expanded="true"><span>${escapeHtml(category)}</span><span class="chevron">⌄</span></button><div class="report-links" data-category-links="${escapeHtml(key)}">${items.map(item => `<button class="report-link ${item.id === selectedDefinitionId ? 'active' : ''}" type="button" data-definition-id="${escapeHtml(item.id)}">${escapeHtml(item.name)}</button>`).join('')}</div>`;
  }).join('');
  navigation.innerHTML = content || '<p class="navigation-loading">暂无可用报表。</p>';
  navigation.querySelectorAll('.category').forEach(category => category.addEventListener('click', () => {
    const expanded = category.getAttribute('aria-expanded') === 'true';
    category.setAttribute('aria-expanded', String(!expanded));
    navigation.querySelector(`[data-category-links="${category.dataset.category}"]`).hidden = expanded;
  }));
  navigation.querySelectorAll('.report-link').forEach(link => link.addEventListener('click', () => {
    const definition = reportDefinitions.find(item => item.id === link.dataset.definitionId);
    if (definition) openDefinition(definition);
  }));
}
async function loadDefinitions() {
  const currentId = selectedDefinitionId;
  reportDefinitions = await get('/api/report-definitions');
  selectedDefinitionId = reportDefinitions.some(item => item.id === currentId) ? currentId : reportDefinitions[0]?.id ?? null;
  renderNavigation();
}

const startDate = document.querySelector('#start-date');
const endDate = document.querySelector('#end-date');
const capacityButton = document.querySelector('#capacity-query-button');
const capacityMessage = document.querySelector('#capacity-message');
const capacityReport = document.querySelector('#capacity-report');
function renderCapacity(result) {
  capacityResult = result;
  const s = result.summary;
  const fields = activeFields('capacity');
  document.querySelector('#capacity-summary').innerHTML = [
    ['passRecords','过站记录', s.passRecords], ['serials','参与 SN', s.serials], ['completedSerials','完成 SN', s.completedSerials], ['errorRecords','异常记录', s.errorRecords], ['ngQuantity','NG 数量', s.ngQuantity], ['activeDays','有效生产天数', s.activeDays], ['averageCompletedPerActiveDay','日均完成 SN', s.averageCompletedPerActiveDay]
  ].filter(([id]) => fields.length === 0 || fields.includes(id)).map(([, label, value]) => `<div class="info-item"><span>${label}</span><strong>${value}</strong></div>`).join('');
  document.querySelector('#capacity-count').textContent = `${result.startDate} 至 ${result.endDate}，共 ${result.rows.length} 天有生产记录`;
  document.querySelector('#capacity-table').innerHTML = `<thead><tr><th>日期</th><th>过站记录</th><th>参与 SN</th><th>完成 SN</th><th>异常记录</th><th>NG 数量</th></tr></thead><tbody>${result.rows.map(row => `<tr><td>${row.date}</td><td>${row.passRecords}</td><td>${row.serials}</td><td>${row.completedSerials}</td><td>${row.errorRecords}</td><td>${row.ngQuantity}</td></tr>`).join('')}</tbody>`;
  startDate.value = result.startDate;
  endDate.value = result.endDate;
  capacityReport.hidden = false;
}
async function queryCapacity() {
  capacityButton.disabled = true; capacityButton.textContent = '查询中…'; capacityMessage.textContent = '';
  try {
    await loadDefinitions();
    const queryString = new URLSearchParams();
    if (startDate.value) queryString.set('startDate', startDate.value);
    if (endDate.value) queryString.set('endDate', endDate.value);
    const response = await fetch(apiUrl(`/api/reports/capacity?${queryString}`));
    const body = await response.json();
    if (!response.ok) throw new Error(body.message || '查询失败。');
    renderCapacity(body);
  } catch (error) { capacityReport.hidden = true; capacityMessage.textContent = error.message; }
  finally { capacityButton.disabled = false; capacityButton.textContent = '查询'; }
}
capacityButton.addEventListener('click', queryCapacity);
document.querySelector('#enter-report-center').addEventListener('click', () => document.body.classList.add('report-entered'));
document.querySelector('#export-product-trace').addEventListener('click', () => { if (productTraceResult) exportCsv('产品追溯', [{key:'sequence',label:'序号'},{key:'process',label:'工序'},{key:'station',label:'工位'},{key:'inTime',label:'过站时间'},{key:'error',label:'异常'},{key:'ngQuantity',label:'NG数量'},{key:'scrap',label:'报废'},{key:'rework',label:'返工'}], productTraceResult.records); });
document.querySelector('#export-capacity').addEventListener('click', () => { if (capacityResult) exportCsv('产能报表', [{key:'date',label:'日期'},{key:'passRecords',label:'过站记录'},{key:'serials',label:'参与SN'},{key:'completedSerials',label:'完成SN'},{key:'errorRecords',label:'异常记录'},{key:'ngQuantity',label:'NG数量'}], capacityResult.rows); });
document.querySelector('#export-standard').addEventListener('click', () => { if (standardResult) exportCsv('报表查询结果', standardResult.columns.map(key => ({key,label:key})), standardResult.rows); });

const shell = document.querySelector('.shell');
const sidebarToggle = document.querySelector('#sidebar-toggle');
const mainSidebarToggle = document.querySelector('#main-sidebar-toggle');
function setSidebarCollapsed(collapsed) {
  shell.classList.toggle('sidebar-collapsed', collapsed);
  mainSidebarToggle.hidden = !collapsed;
  sidebarToggle.setAttribute('aria-label', collapsed ? '展开左侧报表目录' : '收起左侧报表目录');
}
sidebarToggle.addEventListener('click', () => setSidebarCollapsed(true));
mainSidebarToggle.addEventListener('click', () => setSidebarCollapsed(false));
if (window.matchMedia('(max-width:850px)').matches) setSidebarCollapsed(true);

loadDefinitions().catch(() => { navigation.innerHTML = '<p class="navigation-loading">无法加载报表配置。</p>'; });
