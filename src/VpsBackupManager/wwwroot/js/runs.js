import { api, h, clear, openDialog, fmtDate, fmtBytes, fmtDuration, statusBadge, errorToast, TRIGGER_LABEL } from './core.js';

/** Run detail dialog with live refresh while the run is in progress. */
export function openRunDialog(runId) {
  const d = openDialog('Execução de backup', { wide: true });
  let timer = null;
  d.dialog.addEventListener('close', () => clearTimeout(timer));

  async function load() {
    let data;
    try { data = await api('GET', `/api/runs/${encodeURIComponent(runId)}`); } catch (err) { errorToast(err); return; }
    const { run, items, events } = data;
    clear(d.body,
      h('div', { class: 'row' }, statusBadge(run.status),
        h('span', { text: `${TRIGGER_LABEL[run.trigger] || run.trigger} · início ${fmtDate(run.startedAt, true)}` }),
        run.finishedAt ? h('span', { class: 'muted', text: `· fim ${fmtDate(run.finishedAt, true)}` }) : null),
      run.message ? h('p', { text: run.message }) : null,
      h('h3', { text: 'Bancos' }),
      h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, ['Conexão', 'Banco', 'Status', 'Duração', 'Original', 'Final', 'Destino / erro'].map((t) => h('th', { text: t })))),
        h('tbody', {}, items.map((b) => h('tr', {},
          h('td', { text: b.connectionName }), h('td', { text: b.databaseName }), h('td', {}, statusBadge(b.status)),
          h('td', { text: fmtDuration(b.durationMs) }), h('td', { text: fmtBytes(b.rawSize) }), h('td', { text: fmtBytes(b.finalSize) }),
          h('td', { class: 'small' }, b.error ? h('span', { class: 'badge error', text: 'erro' }) : null, ' ',
            b.error || b.remotePath || '', b.warning ? h('div', { class: 'muted', text: b.warning }) : null)))))),
      h('h3', { text: 'Eventos' }),
      events.length === 0 ? h('div', { class: 'muted', text: 'Sem eventos.' }) :
        h('div', {}, events.map((e) => h('div', { class: 'log-line' },
          h('span', { class: 'muted', text: fmtDate(e.createdAt, true) + ' ' }),
          h('span', { class: `badge ${e.level === 'info' ? 'info' : e.level}`, text: e.level }), ' ', e.message))),
    );
    if (run.status === 'running' || run.status === 'pending') timer = setTimeout(load, 3000);
  }
  load();
}
