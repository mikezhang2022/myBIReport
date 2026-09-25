(() => {
  const api = `${window.location.protocol}//${window.location.hostname}:5095/api`;
  const roleNames = { 'system-admin': '系统管理员', 'report-admin': '报表管理员', 'report-user': '报表使用者' };
  const esc = value => String(value ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
  const roleOptions = selected => Object.entries(roleNames).map(([value, label]) => `<option value="${value}" ${selected === value ? 'selected' : ''}>${label}</option>`).join('');
  let reportChoices = [];

  async function request(path, options = {}) {
    const response = await fetch(`${api}${path}`, { credentials: 'include', ...options, headers: { ...(options.headers || {}), ...(options.body ? { 'Content-Type': 'application/json' } : {}) } });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.message || `请求失败（${response.status}）`);
    return body;
  }

  function installUserPage() {
    const nav = document.querySelector('.sidebar nav');
    const main = document.querySelector('main');
    if (!nav || !main || document.querySelector('#users-page')) return;
    nav.insertAdjacentHTML('beforeend', '<button class="nav-item" id="users-nav" type="button"><span>♙</span>用户与权限</button>');
    main.insertAdjacentHTML('beforeend', `<section id="users-page" class="page" hidden><div class="page-heading permission-heading"><div><span class="eyebrow">ACCESS CONTROL</span><h1>用户与权限</h1><p>创建账号、选择角色，并按报表分类授权。</p></div><div class="role-guide"><span>系统管理员</span><span>报表管理员</span><span>报表使用者</span></div></div><div class="permission-layout"><section class="editor-card permission-create"><div class="card-heading"><div><h2>新增账号</h2><p>账号创建后可立即登录；密码仅保存为不可逆摘要。</p></div><span class="step-chip">01</span></div><form id="user-create-form"><label>用户名<input name="username" required minlength="3" maxlength="64" autocomplete="off" placeholder="例如 zhangsan"></label><label>显示名称<input name="displayName" required maxlength="80" placeholder="例如 张三"></label><label>初始密码<input name="password" type="password" required minlength="2" autocomplete="new-password" placeholder="至少 2 个字符"><small>设置简单密码只建议用于本地测试环境。</small></label><label>账号角色<select name="role">${roleOptions('report-user')}</select><small>角色决定可进入的管理页面。</small></label><fieldset class="user-report-picker"><legend>报表访问范围</legend><div class="permission-callout">仅“报表使用者”需要选择报表；管理员默认拥有权限范围内的全部报表。</div><div class="report-tree" data-report-tree="create"></div></fieldset><div class="create-actions"><button class="primary" type="submit">创建账号</button><p id="user-create-message" class="message"></p></div></form></section><aside class="role-reference"><h2>角色说明</h2><div><b>系统管理员</b><span>管理数据源、报表及所有账号。</span></div><div><b>报表管理员</b><span>创建和维护报表，不管理账号与数据源。</span></div><div><b>报表使用者</b><span>仅在报表中心查询已授权的报表。</span></div></aside></div><section class="permission-users"><div class="list-heading"><div><span class="eyebrow">ACCOUNTS</span><h2>已有账号</h2></div><span id="user-count" class="count-chip"></span></div><div id="user-list"><p class="hint">正在读取用户…</p></div></section></section><section id="permission-denied" class="page" hidden><div class="editor-card"><h1>此账号不能进入管理后台</h1><p>当前账号只有报表查询权限。请联系系统管理员。</p></div></section>`);
    const style = document.createElement('style');
    style.textContent = `#users-page{max-width:1240px}.eyebrow{display:block;color:#667085;font-size:11px;font-weight:800;letter-spacing:.12em;margin-bottom:5px}.permission-heading{margin-bottom:20px}.role-guide{display:flex;gap:7px;flex-wrap:wrap;padding-top:7px}.role-guide span,.count-chip,.step-chip{border-radius:999px;background:#eef4ff;color:#175cd3;font-size:12px;font-weight:700;padding:5px 9px}.permission-layout{display:grid;grid-template-columns:minmax(0,1fr) 270px;gap:18px;align-items:start}.permission-create{padding:24px!important}.card-heading,.list-heading{display:flex;justify-content:space-between;gap:16px;align-items:flex-start;margin-bottom:20px}.card-heading h2,.list-heading h2,.role-reference h2{font-size:18px;margin:0}.card-heading p{margin:5px 0 0;color:#667085}.step-chip{font-size:16px;background:#172b4d;color:#fff;padding:7px 10px}.permission-create form{display:grid;grid-template-columns:repeat(2,minmax(200px,1fr));gap:16px}.permission-create label,.permission-user-card label{display:grid;gap:7px;font-weight:700;color:#344054}.permission-create input,.permission-create select,.permission-user-card input,.permission-user-card select{min-width:0;padding:10px 11px;border:1px solid #d0d5dd;border-radius:7px;background:#fff;font:inherit}.permission-create input:focus,.permission-create select:focus,.permission-user-card input:focus,.permission-user-card select:focus{outline:3px solid #dbeafe;border-color:#2563eb}.permission-create small,.permission-user-card small{font-weight:400;color:#667085}.user-report-picker{grid-column:1/-1;min-width:0;border:1px solid #e4e7ec;border-radius:10px;padding:14px 16px}.user-report-picker legend{font-weight:800;padding:0 5px}.permission-callout{margin:2px 0 12px;padding:9px 10px;border-left:3px solid #60a5fa;background:#f8fbff;color:#475467;font-size:13px}.report-tree{display:grid;gap:8px}.report-tree-category{border:1px solid #e4e7ec;border-radius:8px;overflow:hidden}.report-tree-category summary{display:flex;align-items:center;gap:9px;padding:11px 12px;background:#f8fafc;cursor:pointer;font-weight:700;list-style:none}.report-tree-category summary::-webkit-details-marker{display:none}.report-tree-category summary:before{content:'›';color:#667085;transition:transform .15s}.report-tree-category[open] summary:before{transform:rotate(90deg)}.report-tree-category summary input,.report-tree-item input{width:17px;height:17px;margin:0;accent-color:#2563eb}.report-tree-items{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:4px;padding:8px 12px 12px 38px}.report-tree-item{display:flex!important;align-items:center;gap:9px;padding:7px 0;font-weight:400!important;cursor:pointer}.report-tree-empty{color:#667085;padding:10px}.create-actions{grid-column:1/-1;display:flex;align-items:center;gap:12px}.create-actions .message{padding:0;min-height:0}.role-reference{border:1px solid #dbe5f3;border-radius:10px;padding:18px;background:linear-gradient(180deg,#fff,#f7faff)}.role-reference h2{margin-bottom:12px}.role-reference div{display:grid;gap:3px;padding:12px 0;border-top:1px solid #e4e7ec}.role-reference b{font-size:13px;color:#172b4d}.role-reference span{color:#667085;font-size:13px}.permission-users{margin-top:28px}.list-heading{align-items:end;margin-bottom:10px}.count-chip{background:#f2f4f7;color:#475467}.permission-user-card{position:relative;padding:20px;margin:10px 0;border:1px solid #e4e7ec;border-radius:10px;background:#fff;display:grid;grid-template-columns:repeat(4,minmax(140px,1fr));gap:14px;box-shadow:0 1px 2px #1018280a}.permission-user-card:before{content:'账号配置';position:absolute;top:0;left:0;border-radius:10px 0 8px 0;padding:3px 9px;background:#f2f4f7;color:#667085;font-size:11px;font-weight:700}.permission-user-card .user-reports{grid-column:1/-1;margin-top:4px}.permission-user-card .user-actions{grid-column:1/-1;display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding-top:2px}.permission-user-card .user-actions input{max-width:280px}.permission-user-card .user-actions .danger{color:#b42318}.permission-denied{padding:24px}@media(max-width:900px){.permission-layout{grid-template-columns:1fr}.role-reference{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}.role-reference h2{grid-column:1/-1}.role-reference div{border-top:0;padding:0}}@media(max-width:700px){.permission-heading,.card-heading,.list-heading{flex-direction:column}.permission-create form,.permission-user-card,.role-reference{grid-template-columns:1fr}.user-report-picker,.permission-user-card .user-reports,.permission-user-card .user-actions{grid-column:auto}.permission-user-card .user-actions{align-items:stretch;flex-direction:column}.report-tree-items{grid-template-columns:1fr;padding-left:28px}}`;
    document.head.append(style);
    document.querySelector('#users-nav').addEventListener('click', event => showUsers(event.currentTarget));
    document.querySelector('#user-create-form [name="role"]').addEventListener('change', event => {
      document.querySelector('#user-create-form .user-report-picker').hidden = event.target.value !== 'report-user';
    });
    document.querySelector('#user-create-form').addEventListener('submit', createUser);
    document.querySelector('#user-list').addEventListener('submit', saveUser);
    document.querySelector('#user-list').addEventListener('click', deleteUser);
    document.querySelectorAll('.nav-item').forEach(item => item.addEventListener('click', event => setActiveNavigation(event.currentTarget)));
  }

  function setActiveNavigation(activeItem) {
    document.querySelectorAll('.nav-item').forEach(item => item.classList.toggle('active', item === activeItem));
    document.querySelector('#users-page').hidden = activeItem?.id !== 'users-nav';
    const denied = document.querySelector('#permission-denied');
    if (denied) denied.hidden = true;
  }

  async function showUsers(activeItem = document.querySelector('#users-nav')) {
    setActiveNavigation(activeItem);
    document.querySelector('#report-list-page').hidden = true;
    document.querySelector('#editor-page').hidden = true;
    const dataSourcePage = document.querySelector('#data-source-page');
    if (dataSourcePage) dataSourcePage.hidden = true;
    document.querySelector('#breadcrumb').textContent = '用户与权限';
    try {
      const [users, reports] = await Promise.all([request('/users'), request('/report-definitions')]);
      reportChoices = reports.map(report => ({ id: report.id, name: report.name, category: report.category }));
      renderUsers(users);
      renderReportTrees();
    } catch (error) {
      document.querySelector('#user-list').innerHTML = `<p class="message">${esc(error.message)}</p>`;
    }
  }

  function renderReportTree(container, selectedIds, key) {
    const groups = new Map();
    reportChoices.forEach(report => {
      const category = report.category || '未分类';
      if (!groups.has(category)) groups.set(category, []);
      groups.get(category).push(report);
    });
    container.innerHTML = [...groups.entries()].sort(([a], [b]) => a.localeCompare(b, 'zh-CN')).map(([category, reports], index) => {
      const categoryId = `${key}-category-${index}`;
      return `<details class="report-tree-category"><summary><input type="checkbox" data-report-category="${esc(categoryId)}" aria-label="全选${esc(category)}分类"><span>${esc(category)}</span><small>（${reports.length}）</small></summary><div class="report-tree-items">${reports.map(report => `<label class="report-tree-item"><input type="checkbox" name="reportIds" value="${esc(report.id)}" data-report-leaf="${esc(categoryId)}" ${selectedIds.includes(report.id) ? 'checked' : ''}><span>${esc(report.name)}</span></label>`).join('')}</div></details>`;
    }).join('') || '<p class="report-tree-empty">当前没有可授权的报表。</p>';
    container.querySelectorAll('[data-report-category]').forEach(parent => {
      const leaves = [...container.querySelectorAll(`[data-report-leaf="${CSS.escape(parent.dataset.reportCategory)}"]`)];
      const sync = () => {
        const checkedCount = leaves.filter(input => input.checked).length;
        parent.checked = checkedCount === leaves.length && leaves.length > 0;
        parent.indeterminate = checkedCount > 0 && checkedCount < leaves.length;
      };
      parent.addEventListener('change', () => { leaves.forEach(input => { input.checked = parent.checked; }); parent.indeterminate = false; });
      leaves.forEach(input => input.addEventListener('change', sync));
      sync();
    });
  }

  function renderReportTrees() {
    renderReportTree(document.querySelector('#user-create-form [data-report-tree="create"]'), [], 'create');
    document.querySelectorAll('.permission-user-card [data-report-tree]').forEach(tree => {
      renderReportTree(tree, JSON.parse(tree.dataset.selectedReports || '[]'), tree.dataset.reportTree);
    });
  }

  function renderUsers(users) {
    const currentId = window.processBiUser?.id;
    const list = document.querySelector('#user-list');
    document.querySelector('#user-count').textContent = `${users.length} 个账号`;
    list.innerHTML = users.map((user, index) => `<form class="permission-user-card" data-user-id="${esc(user.id)}"><label>用户名<input name="username" value="${esc(user.username)}" required minlength="3" maxlength="64"></label><label>显示名称<input name="displayName" value="${esc(user.displayName)}" required maxlength="80"></label><label>角色<select name="role" ${user.id === currentId ? 'disabled title="不能在当前会话中更改自己的角色"' : ''}>${roleOptions(user.role)}</select></label><label>状态<select name="active" ${user.id === currentId ? 'disabled title="不能停用当前登录账号"' : ''}><option value="true" ${user.active ? 'selected' : ''}>启用</option><option value="false" ${!user.active ? 'selected' : ''}>停用</option></select></label><fieldset class="user-reports user-report-picker"><legend>授权报表</legend><div class="report-tree" data-report-tree="user-${index}" data-selected-reports="${esc(JSON.stringify(user.reportIds || []))}"></div></fieldset><div class="user-actions"><input name="password" type="password" minlength="2" autocomplete="new-password" placeholder="留空表示不修改密码"><button class="secondary" type="submit">保存用户</button><button class="secondary danger" type="button" data-delete-user="${esc(user.id)}" ${user.id === currentId ? 'disabled' : ''}>删除用户</button><small>修改密码时至少 2 个字符；当前账号不能删除。</small></div></form>`).join('') || '<p class="hint">还没有其他用户。</p>';
    renderReportTrees();
    list.querySelectorAll('.permission-user-card [name="role"]').forEach(select => select.addEventListener('change', event => {
      const form = event.target.closest('form');
      form.querySelector('.user-reports').hidden = event.target.value !== 'report-user';
    }));
    list.querySelectorAll('.permission-user-card [name="role"]').forEach(select => { select.closest('form').querySelector('.user-reports').hidden = select.value !== 'report-user'; });
  }

  async function createUser(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const message = document.querySelector('#user-create-message');
    const selected = [...form.querySelectorAll('[name="reportIds"]:checked')].map(input => input.value);
    const body = { username: form.elements.username.value.trim(), displayName: form.elements.displayName.value.trim(), password: form.elements.password.value, role: form.elements.role.value, reportIds: selected, active: true };
    try {
      await request('/users', { method: 'POST', body: JSON.stringify(body) });
      form.reset();
      form.elements.role.value = 'report-user';
      form.querySelector('.user-report-picker').hidden = false;
      message.textContent = '用户已创建。';
      await showUsers();
    } catch (error) { message.textContent = error.message; }
  }

  async function saveUser(event) {
    const form = event.target.closest('.permission-user-card');
    if (!form) return;
    event.preventDefault();
    const selected = [...form.querySelectorAll('[name="reportIds"]:checked')].map(input => input.value);
    const role = form.elements.role.disabled ? form.elements.role.value || window.processBiUser.role : form.elements.role.value;
    const active = form.elements.active.disabled ? true : form.elements.active.value === 'true';
    const body = { username: form.elements.username.value.trim(), displayName: form.elements.displayName.value.trim(), password: form.elements.password.value, role, reportIds: selected, active };
    try { await request(`/users/${encodeURIComponent(form.dataset.userId)}`, { method: 'PUT', body: JSON.stringify(body) }); await showUsers(); }
    catch (error) { alert(error.message); }
  }

  async function deleteUser(event) {
    const button = event.target.closest('[data-delete-user]');
    if (!button || !confirm('确定删除这个用户吗？其登录权限会立即失效。')) return;
    try { await request(`/users/${encodeURIComponent(button.dataset.deleteUser)}`, { method: 'DELETE' }); await showUsers(); }
    catch (error) { alert(error.message); }
  }

  async function start() {
    try {
      const user = window.processBiUser || await request('/auth/me');
      window.processBiUser = user;
      if (user.role === 'system-admin') installUserPage();
      else if (user.role === 'report-admin') document.querySelectorAll('.nav-item')[1]?.setAttribute('hidden', '');
      else {
        document.querySelectorAll('main>.page').forEach(page => page.hidden = true);
        let denied = document.querySelector('#permission-denied');
        if (!denied) {
          document.querySelector('main').insertAdjacentHTML('beforeend', '<section id="permission-denied" class="page"><div class="editor-card permission-denied"><h1>此账号不能进入管理后台</h1><p>当前账号只有报表查询权限。请联系系统管理员。</p></div></section>');
          denied = document.querySelector('#permission-denied');
        }
        denied.hidden = false;
      }
    } catch { /* auth.js displays the login/setup screen */ }
  }
  start();
})();
