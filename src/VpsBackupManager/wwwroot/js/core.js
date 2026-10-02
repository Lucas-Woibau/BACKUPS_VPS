import { HELP } from './help.js';

// Shared helpers for every page. No framework; DOM built with createElement/textContent (no innerHTML
// with data) so values coming from the API can never inject markup (XSS).

export const state = { csrf: null, user: null, timezone: 'America/Sao_Paulo', vpsName: '' };

export class ApiError extends Error {
  constructor(message, status, data) { super(message); this.status = status; this.data = data; }
}

export async function api(method, url, body) {
  const headers = { 'X-Requested-With': 'vbm', 'Accept': 'application/json' };
  if (state.csrf) headers['X-CSRF-Token'] = state.csrf;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(url, {
    method, headers, credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  let data = null;
  const text = await res.text();
  if (text) { try { data = JSON.parse(text); } catch { data = { error: text }; } }
  if (res.status === 401 && !url.startsWith('/api/auth/')) { location.href = '/login.html'; throw new ApiError('Sessão expirada', 401); }
  if (!res.ok) throw new ApiError((data && data.error) || `Erro HTTP ${res.status}`, res.status, data);
  return data;
}

/** h('div', {class:'x', onclick: fn}, 'text', childNode, [more]) */
export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else if (k === 'text') el.textContent = v;
    else if (k === 'dataset') Object.assign(el.dataset, v);
    else if (k in el && typeof v !== 'string') el[k] = v;
    else el.setAttribute(k, v === true ? '' : String(v));
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const c of children.flat(Infinity)) {
    if (c === undefined || c === null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

export function clear(el, ...children) { el.replaceChildren(); append(el, children); return el; }

export const $ = (sel, root = document) => root.querySelector(sel);

// ---------------------------------------------------------------- formatting
export function fmtDate(iso, withSeconds = false) {
  if (!iso) return '—';
  const d = new Date(iso);
  return new Intl.DateTimeFormat('pt-BR', {
    timeZone: state.timezone, day: '2-digit', month: '2-digit', year: 'numeric',
    hour: '2-digit', minute: '2-digit', second: withSeconds ? '2-digit' : undefined,
  }).format(d);
}

export function fmtBytes(n) {
  if (n === null || n === undefined) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0; let v = Number(n);
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v.toLocaleString('pt-BR', { maximumFractionDigits: 1 })} ${u[i]}`;
}

export function fmtDuration(ms) {
  if (ms === null || ms === undefined) return '—';
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}min ${s % 60}s`;
  return `${Math.floor(m / 60)}h ${m % 60}min`;
}

export function fmtAgo(iso) {
  if (!iso) return '';
  const diff = (Date.now() - new Date(iso).getTime()) / 1000;
  const abs = Math.abs(diff);
  const rtf = new Intl.RelativeTimeFormat('pt-BR', { numeric: 'auto' });
  if (abs < 60) return rtf.format(-Math.round(diff), 'second');
  if (abs < 3600) return rtf.format(-Math.round(diff / 60), 'minute');
  if (abs < 86400) return rtf.format(-Math.round(diff / 3600), 'hour');
  return rtf.format(-Math.round(diff / 86400), 'day');
}

const STATUS_LABEL = {
  pending: 'Aguardando', running: 'Executando', success: 'Sucesso', warning: 'Sucesso com aviso', error: 'Erro',
};
export function statusBadge(status) {
  return h('span', { class: `badge ${status || 'neutral'}`, text: STATUS_LABEL[status] || status || '—' });
}

export const DAY_NAMES = ['Dom', 'Seg', 'Ter', 'Qua', 'Qui', 'Sex', 'Sáb'];
export const TRIGGER_LABEL = { manual: 'Manual', schedule: 'Agendado', catchup: 'Recuperação' };

// ---------------------------------------------------------------- UI helpers
export function toast(message, type = 'info', ms = 4500) {
  let box = document.getElementById('toasts');
  if (!box) { box = h('div', { id: 'toasts', role: 'status', 'aria-live': 'polite' }); document.body.append(box); }
  const t = h('div', { class: `toast ${type}`, text: message });
  box.append(t);
  setTimeout(() => t.remove(), ms);
}

export function errorToast(err) { toast(err.message || String(err), 'error', 7000); }

/** Opens a native <dialog>. Returns {dialog, body, foot, close}. */
export function openDialog(title, { wide = false } = {}) {
  const body = h('div', { class: 'dlg-body' });
  const foot = h('div', { class: 'dlg-foot' });
  const dialog = h('dialog', {},
    h('div', { class: 'dlg-head' }, h('h2', { text: title, class: 'm0' }),
      h('button', { class: 'small', type: 'button', 'aria-label': 'Fechar', text: '✕', onclick: () => dialog.close() })),
    body, foot);
  if (wide) dialog.style.width = 'min(1000px, calc(100vw - 24px))';
  dialog.addEventListener('close', () => dialog.remove());
  document.body.append(dialog);
  dialog.showModal();
  return { dialog, body, foot, close: () => dialog.close() };
}

export async function confirmDialog(title, message, confirmLabel = 'Confirmar', danger = false) {
  return new Promise((resolve) => {
    const d = openDialog(title);
    d.body.append(h('p', { text: message }));
    let result = false;
    d.foot.append(
      h('button', { type: 'button', text: 'Cancelar', onclick: () => d.close() }),
      h('button', { type: 'button', class: danger ? 'primary danger' : 'primary', text: confirmLabel, onclick: () => { result = true; d.close(); } }));
    d.dialog.addEventListener('close', () => resolve(result));
  });
}

/** Renders a help entry ({ text, list, example, warn }) as DOM — text only, never HTML. */
export function helpContent(entry) {
  return [
    entry.text ? h('p', { text: entry.text }) : null,
    entry.list ? h('ul', { class: 'help-list' }, entry.list.map((x) => h('li', { text: x }))) : null,
    entry.example ? h('div', { class: 'help-example' }, h('strong', { text: 'Exemplo: ' }), h('code', { text: entry.example })) : null,
    entry.warn ? h('div', { class: 'alert warning', text: entry.warn }) : null,
  ].filter(Boolean);
}

export function openHelp(title, key = title) {
  const entry = HELP[key];
  if (!entry) return;
  const d = openDialog(title);
  d.body.append(...helpContent(entry),
    h('p', { class: 'small muted' }, 'Guia completo: ', h('a', { href: '/help.html', target: '_blank', text: 'página Ajuda' }), '.'));
  d.foot.append(h('button', { type: 'button', class: 'primary', text: 'Entendi', onclick: () => d.close() }));
}

function helpButton(title, key) {
  if (!HELP[key]) return null;
  return h('button', {
    type: 'button', class: 'help-btn', title: 'O que colocar aqui?', 'aria-label': `Ajuda: ${title}`, text: 'i',
    onclick: (e) => { e.preventDefault(); e.stopPropagation(); openHelp(title, key); },
  });
}

export function field(labelText, input, hint, helpKey = labelText) {
  return h('div', { class: 'field' },
    h('div', { class: 'label-row' }, h('label', { text: labelText }), helpButton(labelText, helpKey)),
    input, hint ? h('div', { class: 'hint', text: hint }) : null);
}

export function checkbox(labelText, checked, attrs = {}, helpKey = labelText) {
  const input = h('input', { type: 'checkbox', checked: !!checked, ...attrs });
  return { input, el: h('div', { class: 'check-row' }, h('label', { class: 'check' }, input, labelText), helpButton(labelText, helpKey)) };
}

export async function withBusy(button, fn) {
  const original = button.textContent;
  button.disabled = true;
  button.textContent = 'Aguarde…';
  try { return await fn(); } finally { button.disabled = false; button.textContent = original; }
}

// ---------------------------------------------------------------- page bootstrap
const NAV = [
  ['/', 'Dashboard'], ['/connections.html', 'Bancos'], ['/schedules.html', 'Agendamentos'],
  ['/backups.html', 'Histórico'], ['/settings.html', 'Configurações'], ['/logs.html', 'Logs'], ['/help.html', 'Ajuda'],
];

export async function initPage() {
  const st = await api('GET', '/api/auth/status');
  if (st.setupRequired) { location.href = '/setup.html'; return null; }
  if (!st.authenticated) { location.href = '/login.html'; return null; }
  Object.assign(state, { csrf: st.csrfToken, user: st.username, timezone: st.timezone, vpsName: st.vpsName });

  const path = location.pathname === '/index.html' ? '/' : location.pathname;
  const nav = h('nav', { class: 'nav', 'aria-label': 'Principal' },
    NAV.map(([href, label]) => h('a', { href, text: label, class: href === path ? 'active' : null })));
  const logout = h('button', {
    class: 'small', type: 'button', text: 'Sair',
    onclick: async () => { await api('POST', '/api/auth/logout'); location.href = '/login.html'; },
  });
  document.body.prepend(h('header', { class: 'topbar' },
    h('div', { class: 'brand' }, 'VPS Backup Manager', h('span', { text: st.vpsName })),
    nav, h('div', { class: 'user' }, h('span', { text: st.username }), logout)));
  return state;
}
