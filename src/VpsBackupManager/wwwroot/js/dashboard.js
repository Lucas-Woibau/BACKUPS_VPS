import {
  api, h, clear, $, initPage, fmtDate, fmtAgo, fmtBytes, statusBadge, errorToast, toast, withBusy, TRIGGER_LABEL,
} from './core.js';
import { openRunDialog } from './runs.js';

if (await initPage()) {
  const app = $('#app');
  let timer = null;

  async function render() {
    let d, runs;
    try {
      [d, runs] = await Promise.all([api('GET', '/api/dashboard'), api('GET', '/api/runs?limit=10')]);
    } catch (err) { errorToast(err); return; }

    const statusMap = {
      online: ['ok', 'Online'], atencao: ['warn', 'Atenção'], degradado: ['err', 'Degradado'],
    };
    const [dot, label] = statusMap[d.status] || ['', d.status];

    const runBtn = h('button', {
      class: 'primary', type: 'button', text: d.running ? 'Backup em execução…' : 'Executar backup agora', disabled: !!d.running,
      onclick: (e) => withBusy(e.target, async () => {
        try {
          const r = await api('POST', '/api/backups/run', { targetType: 'all' });
          toast('Backup iniciado.', 'success');
          openRunDialog(r.runId);
          render();
        } catch (err) { errorToast(err); }
      }),
    });

    const alerts = [];
    if (d.failing && d.failing.length) {
      alerts.push(h('div', { class: 'alert error' },
        h('strong', { text: 'Bancos falhando desde o último sucesso: ' }),
        d.failing.map((f, i) => [i ? ', ' : '', `${f.connectionName}/${f.databaseName} (${f.failures}x)`])));
    }
    if (!d.lastSuccess && d.connections > 0) alerts.push(h('div', { class: 'alert warning', text: 'Ainda não existe nenhum backup bem-sucedido.' }));
    if (d.lastSuccess && d.lastSuccess.ageHours > 48) {
      alerts.push(h('div', { class: 'alert warning', text: `Último backup bem-sucedido há ${Math.round(d.lastSuccess.ageHours)} horas.` }));
    }
    if (!d.schedulerHealthy) alerts.push(h('div', { class: 'alert error', text: 'Scheduler não está respondendo. Verifique os logs.' }));
    if (!d.encryptionEnabled && d.connections > 0) {
      alerts.push(h('div', { class: 'alert warning' },
        h('strong', { text: 'Backups sem criptografia: ' }),
        'os dumps vão em texto claro para o Google Drive. Ative a criptografia age em Configurações (a chave privada fica fora da VPS).'));
    }
    if (d.drive.ok === false) alerts.push(h('div', { class: 'alert error', text: `Google Drive: ${d.drive.message}` }));

    const disk = d.disk || {};
    const stat = (labelText, value, sub) => h('div', { class: 'card stat' },
      h('div', { class: 'label', text: labelText }), h('div', { class: 'value' }, value), sub ? h('div', { class: 'sub' }, sub) : null);

    clear(app,
      h('div', { class: 'page-head' },
        h('div', {}, h('h1', { text: 'Dashboard' }), h('div', { class: 'muted', text: `Timezone: ${d.timezone}` })),
        runBtn),
      alerts,
      h('div', { class: 'grid' },
        stat('Sistema', h('span', {}, h('span', { class: `dot ${dot}` }), label), d.running ? 'Backup em execução agora' : null),
        stat('Último backup bem-sucedido',
          d.lastSuccess ? fmtDate(d.lastSuccess.finishedAt) : '—',
          d.lastSuccess ? `${d.lastSuccess.connectionName}/${d.lastSuccess.databaseName} · ${fmtAgo(d.lastSuccess.finishedAt)}` : 'Nenhum ainda'),
        stat('Última tentativa',
          d.lastAttempt ? h('span', {}, statusBadge(d.lastAttempt.status), ' ', fmtDate(d.lastAttempt.startedAt)) : '—',
          d.lastAttempt?.message),
        stat('Próximo backup', d.nextRun ? fmtDate(d.nextRun.at) : '—', d.nextRun ? d.nextRun.schedule : 'Nenhuma agenda ativa'),
        stat('Bancos protegidos', String(d.protectedDatabases), `${d.connections} conexão(ões) ativa(s)`),
        stat('Backups nas últimas 24h', String(d.last24h.total),
          `${d.last24h.success} sucesso · ${d.last24h.warning} aviso · ${d.last24h.error} erro`),
        stat('Disco da VPS', disk.freeBytes !== undefined ? `${fmtBytes(disk.freeBytes)} livres` : '—',
          disk.totalBytes ? `${disk.usedPercent.toFixed(0)}% usado de ${fmtBytes(disk.totalBytes)}` : disk.error),
        stat('Google Drive',
          h('span', {}, h('span', { class: `dot ${d.drive.ok === true ? 'ok' : d.drive.ok === false ? 'err' : ''}` }),
            d.drive.ok === true ? 'Conectado' : d.drive.ok === false ? 'Com erro' : 'Não testado'),
          `Última sincronização: ${d.drive.lastUpload ? fmtDate(d.drive.lastUpload) : '—'} · ${d.drive.destination}`),
      ),
      h('div', { class: 'card' },
        h('h2', { text: 'Últimas execuções' }),
        runs.length === 0 ? h('div', { class: 'empty', text: 'Nenhuma execução ainda.' }) :
          h('div', { class: 'table-wrap' }, h('table', {},
            h('thead', {}, h('tr', {}, ['Início', 'Origem', 'Status', 'Resultado', 'Mensagem'].map((t) => h('th', { text: t })))),
            h('tbody', {}, runs.map((r) => h('tr', { class: 'clickable', onclick: () => openRunDialog(r.id) },
              h('td', { text: fmtDate(r.startedAt) }),
              h('td', { text: TRIGGER_LABEL[r.trigger] || r.trigger }),
              h('td', {}, statusBadge(r.status)),
              h('td', { text: `${r.succeeded + r.warnings}/${r.total} ok` }),
              h('td', { class: 'small', text: r.message || '' }))))))),
    );

    clearTimeout(timer);
    timer = setTimeout(render, d.running ? 4000 : 30000);
  }

  render();
}
