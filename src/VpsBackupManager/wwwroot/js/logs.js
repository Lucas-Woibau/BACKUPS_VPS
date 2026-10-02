import { api, h, clear, $, initPage, fmtDate, errorToast } from './core.js';

if (await initPage()) {
  const app = $('#app');
  const level = h('select', {}, [['', 'Todos'], ['error', 'Erros'], ['warning', 'Avisos'], ['info', 'Info']].map(([v, l]) => h('option', { value: v, text: l })));
  const eventsBox = h('div', {});
  const logBox = h('div', {});
  level.addEventListener('change', loadEvents);

  clear(app,
    h('div', { class: 'page-head' }, h('div', {}, h('h1', { text: 'Logs' }),
      h('div', { class: 'muted' }, 'No servidor: ', h('code', { text: 'docker compose logs -f app' }), ' ou arquivos em ', h('code', { text: 'data/logs/' })))),
    h('div', { class: 'card' }, h('div', { class: 'row' }, h('h2', { text: 'Eventos de backup' }), h('span', { class: 'spacer' }), level,
      h('button', { type: 'button', class: 'small', text: 'Atualizar', onclick: loadEvents })), eventsBox),
    h('div', { class: 'card' }, h('div', { class: 'row' }, h('h2', { text: 'Log da aplicação (últimas 300 linhas)' }), h('span', { class: 'spacer' }),
      h('button', { type: 'button', class: 'small', text: 'Atualizar', onclick: loadLog })), logBox));

  async function loadEvents() {
    try {
      const qs = level.value ? `?level=${level.value}&limit=300` : '?limit=300';
      const events = await api('GET', `/api/events${qs}`);
      clear(eventsBox, events.length === 0 ? h('div', { class: 'empty', text: 'Sem eventos.' }) :
        events.map((e) => h('div', { class: 'log-line' }, h('span', { class: 'muted', text: fmtDate(e.createdAt, true) + ' ' }),
          h('span', { class: `badge ${e.level === 'info' ? 'info' : e.level}`, text: e.level }), ' ', e.message)));
    } catch (err) { errorToast(err); }
  }

  async function loadLog() {
    try {
      const lines = await api('GET', '/api/logs?lines=300');
      clear(logBox, lines.length === 0 ? h('div', { class: 'empty', text: 'Log vazio.' }) :
        lines.slice().reverse().map((l) => {
          const lvl = (l['@l'] || 'Information');
          const cls = lvl === 'Error' || lvl === 'Fatal' ? 'error' : lvl === 'Warning' ? 'warning' : 'info';
          const msg = l['@m'] || renderTemplate(l['@mt'], l) || JSON.stringify(l);
          return h('div', { class: 'log-line' }, h('span', { class: 'muted', text: fmtDate(l['@t'], true) + ' ' }),
            h('span', { class: `badge ${cls}`, text: lvl }), ' ', msg, l['@x'] ? h('div', { class: 'muted', text: l['@x'] }) : null);
        }));
    } catch (err) { errorToast(err); }
  }

  function renderTemplate(tpl, props) {
    if (!tpl) return '';
    return tpl.replace(/\{([A-Za-z0-9_]+)(:[^}]*)?\}/g, (_, k) => (props[k] !== undefined ? String(props[k]) : `{${k}}`));
  }

  loadEvents();
  loadLog();
}
