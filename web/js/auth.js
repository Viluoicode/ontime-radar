// Login with Cognito managed login: OAuth 2.0 authorization code + PKCE (no client secret in the
// browser). Tokens live in sessionStorage: closing the tab logs the user out.
// The API checks the token and the role itself – the role here only decides what the UI shows.
import { CONFIG } from './config.js';

const KEY = { tokens: 'osr.tokens', verifier: 'osr.pkce', state: 'osr.state', back: 'osr.back' };

const store = {
  get(k) { try { return sessionStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { sessionStorage.setItem(k, v); } catch { /* private mode: login lasts this page only */ } },
  del(k) { try { sessionStorage.removeItem(k); } catch { /* ignore */ } }
};
let memoryTokens = null; // fallback when sessionStorage is unavailable

function b64url(bytes) {
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
function randomString(n = 32) { return b64url(crypto.getRandomValues(new Uint8Array(n))); }
async function sha256(text) {
  return new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text)));
}
function decodeJwt(token) {
  try {
    const part = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const json = decodeURIComponent([...atob(part)].map(c => '%' + c.charCodeAt(0).toString(16).padStart(2, '0')).join(''));
    return JSON.parse(json);
  } catch { return {}; }
}

function loadTokens() {
  const raw = store.get(KEY.tokens);
  return raw ? JSON.parse(raw) : memoryTokens;
}
function saveTokens(t) {
  const prev = loadTokens();
  const tokens = {
    access_token: t.access_token,
    id_token: t.id_token,
    refresh_token: t.refresh_token ?? prev?.refresh_token,   // refresh responses omit it
    expires_at: Date.now() + t.expires_in * 1000
  };
  memoryTokens = tokens;
  store.set(KEY.tokens, JSON.stringify(tokens));
}
export function clearTokens() { memoryTokens = null; store.del(KEY.tokens); }

async function tokenRequest(params) {
  const res = await fetch(`${CONFIG.authDomain}/oauth2/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: CONFIG.clientId, ...params })
  });
  if (!res.ok) throw new Error(`Cognito token endpoint ${res.status}`);
  return res.json();
}

/** Go to the Cognito login page; come back to `returnHash` afterwards. */
export async function login(returnHash = location.hash) {
  const verifier = randomString(48);
  const state = randomString(16);
  store.set(KEY.verifier, verifier);
  store.set(KEY.state, state);
  store.set(KEY.back, returnHash || '#/');
  const params = new URLSearchParams({
    response_type: 'code',
    client_id: CONFIG.clientId,
    redirect_uri: CONFIG.redirectUri,
    scope: 'openid email profile',
    state,
    code_challenge: b64url(await sha256(verifier)),
    code_challenge_method: 'S256'
  });
  location.assign(`${CONFIG.authDomain}/oauth2/authorize?${params}`);
}

/** Called once on page load: finishes the login if Cognito just redirected back with ?code=. */
export async function handleRedirect() {
  const q = new URLSearchParams(location.search);
  if (!q.has('code') && !q.has('error')) return;

  const back = store.get(KEY.back) || '#/';
  const expectedState = store.get(KEY.state);
  const verifier = store.get(KEY.verifier);
  [KEY.state, KEY.verifier, KEY.back].forEach(store.del);
  history.replaceState(null, '', location.pathname + back);   // drop ?code= from the address bar

  if (q.has('error')) throw new Error(q.get('error_description') || q.get('error'));
  if (!expectedState || q.get('state') !== expectedState) throw new Error('Phiên đăng nhập không hợp lệ (state). Hãy đăng nhập lại.');

  saveTokens(await tokenRequest({
    grant_type: 'authorization_code', code: q.get('code'), redirect_uri: CONFIG.redirectUri, code_verifier: verifier
  }));
}

/** A valid access token (refreshed if it expires within a minute), or null when logged out. */
export async function accessToken() {
  const t = loadTokens();
  if (!t) return null;
  if (t.expires_at - 60_000 > Date.now()) return t.access_token;
  if (!t.refresh_token) { clearTokens(); return null; }
  try {
    saveTokens(await tokenRequest({ grant_type: 'refresh_token', refresh_token: t.refresh_token }));
    return loadTokens().access_token;
  } catch {
    clearTokens();
    return null;
  }
}

/** Who is logged in (for display and menu only). */
export function currentUser() {
  const t = loadTokens();
  if (!t) return null;
  const access = decodeJwt(t.access_token);
  const id = decodeJwt(t.id_token ?? '');
  const groups = access['cognito:groups'] ?? [];
  return {
    email: id.email ?? access.username ?? '',
    groups,
    isManager: groups.includes('manager'),
    isCskh: groups.includes('cskh')
  };
}

export function logout() {
  clearTokens();
  const params = new URLSearchParams({ client_id: CONFIG.clientId, logout_uri: CONFIG.redirectUri });
  location.assign(`${CONFIG.authDomain}/logout?${params}`);
}
