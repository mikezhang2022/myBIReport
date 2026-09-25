(() => {
  const apiBase = window.PROCESS_BI_API_BASE ?? `${window.location.protocol}//${window.location.hostname}:5095`;
  const originalFetch = window.fetch.bind(window);
  window.fetch = (input, init = {}) => originalFetch(input, { ...init, credentials: 'include' });
  const roles = { 'system-admin': '系统管理员', 'report-admin': '报表管理员', 'report-user': '报表使用者' };
  const escapeHtml = value => String(value ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');

  function showGate(mode, message = '') {
    let gate = document.querySelector('#auth-gate');
    if (!gate) {
      document.body.insertAdjacentHTML('beforeend', `<div id="auth-gate"><form id="auth-form"><div class="auth-card"><div class="auth-mark">R</div><h1 id="auth-title"></h1><p id="auth-description"></p><label>用户名<input name="username" autocomplete="username" required minlength="3" maxlength="64"></label><label>密码<input name="password" type="password" autocomplete="current-password" required minlength="2"></label><p class="auth-hint" hidden>首次创建管理员时，密码至少 2 个字符。</p><p id="auth-message" role="status"></p><button class="auth-submit" type="submit">登录</button></div></form></div>`);
      gate = document.querySelector('#auth-gate');
      document.querySelector('#auth-form').addEventListener('submit', submit);
    }
    const setup = mode === 'setup';
    document.querySelector('#auth-title').textContent = setup ? '创建管理员账号' : '登录报表后台';
    document.querySelector('#auth-description').textContent = setup ? '首次使用，请创建系统管理员。之后可由管理员创建其他账号并分配角色。' : '请使用管理员分配的账号登录。';
    const password = document.querySelector('[name="password"]');
    password.autocomplete = setup ? 'new-password' : 'current-password';
    password.minLength = 2;
    document.querySelector('.auth-hint').hidden = !setup;
    document.querySelector('#auth-message').textContent = message;
    document.querySelector('#auth-form').dataset.mode = mode;
    document.querySelector('.auth-submit').textContent = setup ? '创建并登录' : '登录';
    gate.hidden = false;
  }

  async function submit(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const values = new FormData(form);
    const setup = form.dataset.mode === 'setup';
    const button = form.querySelector('.auth-submit');
    button.disabled = true;
    button.textContent = '处理中…';
    try {
      const response = await originalFetch(`${apiBase}/api/auth/${setup ? 'setup' : 'login'}`, {
        method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: values.get('username'), displayName: values.get('username'), password: values.get('password') })
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.message || (response.status === 401 ? '用户名或密码不正确。' : '操作失败，请检查输入后重试。'));
      location.reload();
    } catch (error) {
      document.querySelector('#auth-message').textContent = error.message;
      button.disabled = false;
      button.textContent = setup ? '创建并登录' : '登录';
    }
  }

  async function start() {
    try {
      const statusResponse = await originalFetch(`${apiBase}/api/auth/status`, { credentials: 'include' });
      if (!statusResponse.ok) throw new Error('无法连接报表 API。');
      const status = await statusResponse.json();
      if (status.setupRequired) { showGate('setup'); return; }
      const response = await originalFetch(`${apiBase}/api/auth/me`, { credentials: 'include' });
      if (response.status === 401) { showGate('login'); return; }
      if (!response.ok) throw new Error('读取登录状态失败。');
      const user = await response.json();
      window.processBiUser = user;
      const account = document.createElement('div');
      account.className = 'auth-account';
      account.innerHTML = `<span>${escapeHtml(user.displayName)} · ${escapeHtml(roles[user.role] || user.role)}</span><button type="button">退出</button>`;
      account.querySelector('button').addEventListener('click', async () => {
        await originalFetch(`${apiBase}/api/auth/logout`, { method: 'POST', credentials: 'include' });
        location.reload();
      });
      document.querySelector('#api-status').before(account);
    } catch (error) { showGate('login', error.message); }
  }

  const styles = document.createElement('style');
  styles.textContent = `#auth-gate{position:fixed;inset:0;z-index:10000;display:grid;place-items:center;padding:20px;background:rgba(15,23,42,.72);backdrop-filter:blur(5px)}#auth-gate[hidden]{display:none}.auth-card{width:min(100%,430px);padding:30px;background:#fff;border:1px solid #e4e7ec;border-radius:16px;box-shadow:0 24px 70px rgba(15,23,42,.25);color:#1d2939}.auth-mark{display:grid;place-items:center;width:42px;height:42px;border-radius:12px;background:#2563eb;color:#fff;font-weight:800}.auth-card h1{margin:18px 0 6px}.auth-card>p{color:#667085;line-height:1.6}.auth-card label{display:grid;gap:7px;margin:14px 0;font-weight:600}.auth-card input{width:100%;box-sizing:border-box;padding:11px 12px;border:1px solid #d0d5dd;border-radius:8px;font:inherit}.auth-hint{font-size:13px}.auth-submit{width:100%;padding:12px;border:0;border-radius:8px;background:#2563eb;color:white;font:inherit;font-weight:700;cursor:pointer}.auth-submit:disabled{opacity:.65}.auth-account{display:inline-flex;vertical-align:middle;align-items:center;gap:10px;margin-left:12px;padding:6px 9px 6px 12px;border:1px solid #e4e7ec;border-radius:999px;background:#fff;color:#344054;font-size:13px}.auth-account button{border:0;border-radius:999px;padding:6px 10px;background:#f2f4f7;color:#344054;cursor:pointer}#auth-message{min-height:20px;color:#b42318;font-size:14px}`;
  document.head.append(styles);
  start();
})();
