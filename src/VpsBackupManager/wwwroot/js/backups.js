import {
  api, h, clear, $, initPage, fmtDate, fmtBytes, fmtDuration, statusBadge, errorToast, toast, openDialog, TRIGGER_LABEL,
} from './core.js';
import { openRunDialog } from './runs.js';

if (await initPage()) {
  const app = $('#app');
  const filters = { status: '', connectionId: '', q: '' };
  let offset = 0;
  const pageSize = 50;
  const connections = await api('GET', '/api/connections');

  const statusSel = h('select', { onchange: (e) => { filters.status = e.target.value; offset = 0; load(); } },
    [['', 'Todos os status'], ['success', 'Sucesso'], ['warning', 'Sucesso com aviso'], ['error', 'Erro'], ['running', 'Executando'], ['pending', 'Aguardando']]
      .map(([v, l]) => h('option', { value: v, text: l })));
  const connSel = h('select', { onchange: (e) => { filters.connectionId = e.target.value; offset = 0; load(); } },
    h('option', { value: '', text: 'Todas as conexões' }), connections.map((c) => h('option', { value: c.id, text: c.name })));
  const search = h('input', { type: 'text', placeholder: 'Buscar banco…' });
  search.addEventListener('change', () => { filters.q = search.value.trim(); offset = 0; load(); });

  const runsBox = h('div', {});
  const tableBox = h('div', {});
  const pager = h('div', { class: 'row' });

  clear(app,
    h('div', { class: 'page-head' }, h('div', {}, h('h1', { text: 'Histórico' }),
      h('div', { class: 'muted', text: 'Todas as execuções e backups individuais, com tamanhos, checksum e destino.' }))),
    h('div', { class: 'card' }, h('h2', { text: 'Execuções recentes' }), runsBox),
    h('div', { class: 'card' },
      h('h2', { text: 'Backups' }),
      h('div', { class: 'row' }, statusSel, connSel, search),
      h('br'), tableBox, pager));

  async function loadRuns() {
    try {
      const runs = await api('GET', '/api/runs?limit=15');
      clear(runsBox, runs.length === 0 ? h('div', { class: 'empty', text: 'Nenhuma execução.' }) :
        h('div', { class: 'table-wrap' }, h('table', {},
          h('thead', {}, h('tr', {}, ['Início', 'Fim', 'Origem', 'Status', 'OK/Total', 'Mensagem'].map((t) => h('th', { text: t })))),
          h('tbody', {}, runs.map((r) => h('tr', { class: 'clickable', onclick: () => openRunDialog(r.id) },
            h('td', { text: fmtDate(r.startedAt) }), h('td', { text: fmtDate(r.finishedAt) }),
            h('td', { text: TRIGGER_LABEL[r.trigger] || r.trigger }), h('td', {}, statusBadge(r.status)),
            h('td', { text: `${r.succeeded + r.warnings}/${r.total}` }), h('td', { class: 'small', text: r.message || '' })))))));
    } catch (err) { errorToast(err); }
  }

  async function load() {
    const qs = new URLSearchParams({ limit: pageSize, offset });
    for (const [k, v] of Object.entries(filters)) if (v) qs.set(k, v);
    let rows;
    try { rows = await api('GET', `/api/backups?${qs}`); } catch (err) { errorToast(err); return; }
    clear(tableBox, rows.length === 0 ? h('div', { class: 'empty', text: 'Nenhum backup encontrado.' }) :
      h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, ['Início', 'Conexão', 'Banco', 'Status', 'Duração', 'Original', 'Final', 'SHA-256', 'Destino / erro'].map((t) => h('th', { text: t })))),
        h('tbody', {}, rows.map((b) => h('tr', { class: 'clickable', onclick: () => openDetail(b.id) },
          h('td', { text: fmtDate(b.startedAt) }), h('td', { text: b.connectionName }), h('td', { class: 'mono', text: b.databaseName }),
          h('td', {}, statusBadge(b.status)), h('td', { text: fmtDuration(b.durationMs) }),
          h('td', { text: fmtBytes(b.rawSize) }), h('td', { text: fmtBytes(b.finalSize) }),
          h('td', { class: 'mono small', text: b.checksumSha256 ? `${b.checksumSha256.slice(0, 12)}…` : '—' }),
          h('td', { class: 'small' }, b.error || b.remotePath || '—',
            b.remoteDeletedAt ? h('div', { class: 'muted', text: `removido pela retenção em ${fmtDate(b.remoteDeletedAt)}` }) : null)))))));
    clear(pager,
      h('button', { type: 'button', class: 'small', text: '← Anteriores', disabled: offset === 0, onclick: () => { offset = Math.max(0, offset - pageSize); load(); } }),
      h('span', { class: 'muted small', text: `Itens ${offset + 1}–${offset + rows.length}` }),
      h('button', { type: 'button', class: 'small', text: 'Próximos →', disabled: rows.length < pageSize, onclick: () => { offset += pageSize; load(); } }));
  }

  async function openDetail(id) {
    const d = openDialog('Detalhes do backup', { wide: true });
    try {
      const { backup: b, events } = await api('GET', `/api/backups/${encodeURIComponent(id)}`);
      const rows = [
        ['Status', statusBadge(b.status)], ['Conexão', b.connectionName], ['Tipo', b.dbType], ['Banco', b.databaseName],
        ['Início', fmtDate(b.startedAt, true)], ['Término', fmtDate(b.finishedAt, true)], ['Duração', fmtDuration(b.durationMs)],
        ['Tamanho original', fmtBytes(b.rawSize)], ['Compactado', fmtBytes(b.compressedSize)], ['Final (enviado)', fmtBytes(b.finalSize)],
        ['Compressão', b.compression || '—'], ['Criptografado (age)', b.encrypted ? 'Sim' : 'Não'],
        ['Arquivo', b.fileName || '—'], ['Destino', b.remotePath || '—'], ['Cópia local', b.localPath || '—'],
        ['SHA-256 (arquivo final)', b.checksumSha256 || '—'], ['SHA-256 (dump bruto)', b.rawSha256 || '—'],
        ['Tentativas de upload', String(b.uploadAttempts)], ['Aviso', b.warning || '—'], ['Erro', b.error || '—'],
        ['Removido do destino', fmtDate(b.remoteDeletedAt)],
      ];
      const copyBtn = b.checksumSha256 ? h('button', {
        type: 'button', class: 'small', text: 'Copiar SHA-256',
        onclick: async () => { await navigator.clipboard.writeText(b.checksumSha256); toast('Copiado.', 'success'); },
      }) : null;
      clear(d.body,
        h('table', {}, h('tbody', {}, rows.map(([k, v]) => h('tr', {}, h('th', { text: k }), h('td', { class: 'mono small' }, v))))),
        h('div', { class: 'row' }, copyBtn, h('button', { type: 'button', class: 'small', text: 'Ver execução completa', onclick: () => { d.close(); openRunDialog(b.runId); } })),
        h('h3', { text: 'Eventos' }),
        events.map((e) => h('div', { class: 'log-line' }, h('span', { class: 'muted', text: fmtDate(e.createdAt, true) + ' ' }),
          h('span', { class: `badge ${e.level === 'info' ? 'info' : e.level}`, text: e.level }), ' ', e.message)));
    } catch (err) { clear(d.body, h('div', { class: 'alert error', text: err.message })); }
  }

  loadRuns();
  load();
}
