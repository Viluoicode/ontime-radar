// App shell: login gate, role-aware navigation, hash router, light/dark theme.
import { handleRedirect, currentUser, accessToken, login, logout } from './auth.js';
import { icon } from './icons.js';
import { esc, errorState } from './ui.js';
import * as overview from './pages/overview.js';
import * as lookup from './pages/lookup.js';
import * as attention from './pages/attention.js';

// Which pages each role may open (the API enforces the same rules: 403 otherwise).
const ROUTES = {
  overview:  { page: overview,  icon: 'layout-dashboard', label: 'Tổng quan SLA', managerOnly: true },
  lookup:    { page: lookup,    icon: 'search',           label: 'Tra cứu đơn' },
  attention: { page: attention, icon: 'flag',             label: 'Cần xử lý' }
};

const app = document.getElementById('app');
let current = null;

// ---------------- theme: follows Windows/macOS setting, with a manual override ----------------
const THEME_KEY = 'osr.theme';
function savedTheme() { try { return localStorage.getItem(THEME_KEY); } catch { return null; } }
function applyTheme(theme) {
  if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
  else delete document.documentElement.dataset.theme;
}
function isDark() {
  const t = document.documentElement.dataset.theme;
  return t ? t === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
}
function toggleTheme() {
  const next = isDark() ? 'light' : 'dark';
  try { localStorage.setItem(THEME_KEY, next); } catch { /* not persisted */ }
  applyTheme(next);
  updateThemeButton();
  route();   // charts read colors from CSS variables → redraw
}
function updateThemeButton() {
  const btn = document.getElementById('theme-btn');
  if (!btn) return;
  btn.innerHTML = `${icon(isDark() ? 'sun' : 'moon')}<span>${isDark() ? 'Giao diện sáng' : 'Giao diện tối'}</span>`;
}
applyTheme(savedTheme());
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { if (!savedTheme()) { updateThemeButton(); route(); } });

// ---------------- screens ----------------
function loginScreen(message) {
  app.innerHTML = `
    <main class="login">
      <div class="login__card card">
        <div class="brand brand--lg">${icon('truck', { size: 28 })}<span>OnTime Radar</span></div>
        <p>Theo dõi vận đơn đa hãng và tỉ lệ giao đúng hạn.</p>
        ${message ? `<p class="login__msg" role="alert">${icon('info', { size: 16 })} ${esc(message)}</p>` : ''}
        <button class="btn btn--primary btn--lg" id="login-btn" type="button"><span>Đăng nhập</span>${icon('chevron-right', { size: 18 })}</button>
        <p class="muted small">Tài khoản do quản trị viên cấp. Hỗ trợ trình quản lý mật khẩu và mã xác thực 2 lớp.</p>
      </div>
    </main>`;
  document.getElementById('login-btn').addEventListener('click', () => login('#/'));
}

function shell(user) {
  const allowed = Object.entries(ROUTES).filter(([, r]) => !r.managerOnly || user.isManager);
  app.innerHTML = `
    <a class="skip" href="#main">Bỏ qua menu</a>
    <div class="layout">
      <aside class="sidebar" id="sidebar">
        <div class="brand">${icon('truck', { size: 22 })}<span>OnTime Radar</span></div>
        <nav aria-label="Điều hướng chính"><ul class="nav">
          ${allowed.map(([key, r]) => `<li><a class="nav__link" href="#/${key}" data-route="${key}">${icon(r.icon)}<span>${r.label}</span></a></li>`).join('')}
        </ul></nav>
        <div class="sidebar__foot">
          <button class="btn btn--ghost btn--block" id="theme-btn" type="button"></button>
          <div class="user">
            <span class="user__email" title="${esc(user.email)}">${esc(user.email)}</span>
            <span class="muted small">${user.isManager ? 'Quản lý' : 'CSKH'}</span>
          </div>
          <button class="btn btn--ghost btn--block" id="logout-btn" type="button">${icon('log-out')}<span>Đăng xuất</span></button>
        </div>
      </aside>
      <div class="topbar">
        <button class="btn btn--ghost" id="menu-btn" type="button" aria-controls="sidebar" aria-expanded="false">${icon('menu')}<span class="sr-only">Mở menu</span></button>
        <div class="brand">${icon('truck', { size: 20 })}<span>OnTime Radar</span></div>
      </div>
      <main class="content" id="main" tabindex="-1"></main>
    </div>`;

  document.getElementById('logout-btn').addEventListener('click', logout);
  document.getElementById('theme-btn').addEventListener('click', toggleTheme);
  const menuBtn = document.getElementById('menu-btn');
  menuBtn.addEventListener('click', () => {
    const open = document.body.classList.toggle('nav-open');
    menuBtn.setAttribute('aria-expanded', String(open));
  });
  updateThemeButton();
}

// ---------------- router: #/<page>/<param>/<param> ----------------
function route() {
  const user = currentUser();
  if (!user || !document.getElementById('main')) return;
  const [key = '', ...params] = location.hash.replace(/^#\/?/, '').split('/').map(decodeURIComponent);
  const fallback = user.isManager ? 'overview' : 'lookup';
  const r = ROUTES[key];
  if (!r || (r.managerOnly && !user.isManager)) { location.replace(`#/${fallback}`); return; }

  current?.page.destroy();
  current = r;
  document.title = `${r.label} · OnTime Radar`;
  document.querySelectorAll('.nav__link').forEach(a => a.toggleAttribute('aria-current', a.dataset.route === key));
  document.body.classList.remove('nav-open');
  document.getElementById('menu-btn')?.setAttribute('aria-expanded', 'false');
  const main = document.getElementById('main');
  r.page.render(main, params);
  main.focus({ preventScroll: true });
}

// ---------------- start ----------------
async function start() {
  try {
    await handleRedirect();
  } catch (err) {
    loginScreen(`Đăng nhập không thành công: ${err.message}`);
    return;
  }
  if (!(await accessToken())) { loginScreen(); return; }

  const user = currentUser();
  if (!user.isManager && !user.isCskh) {
    app.innerHTML = `<main class="login"><div class="login__card card">
      ${errorState(`Tài khoản ${user.email} chưa được cấp quyền. Hãy liên hệ quản trị viên để được thêm vào nhóm CSKH hoặc Quản lý.`)}
      <button class="btn" id="logout-btn" type="button">${icon('log-out')}<span>Đăng xuất</span></button></div></main>`;
    document.getElementById('logout-btn').addEventListener('click', logout);
    return;
  }
  shell(user);
  window.addEventListener('hashchange', route);
  route();
}

start();
