import {
  api, h, clear, $, initPage, fmtDate, errorToast, toast, openDialog, confirmDialog, field, checkbox, withBusy, DAY_NAMES,
} from './core.js';

if (await initPage()) {
  const app = $('#app');

  function describe(s, connections) {
    const when = s.kind === 'interval'
      ? `A cada ${s.intervalMinutes >= 60 && s.intervalMinutes % 60 === 0 ? `${s.intervalMinutes / 60} h` : `${s.intervalMinutes} min`}`
      : `${s.daysOfWeek.length === 7 ? 'Todos os dias' : s.daysOfWeek.map((d) => DAY_NAMES[d]).join(', ')} às ${s.times.join(', ')}`;
    const conn = connections.find((c) => c.id === s.targetConnectionId);
    const target = s.targetType === 'all' ? 'Todas as conexões'
      : s.targetType === 'connection' ? `Conexão ${conn ? conn.name : '#' + s.targetConnectionId}`
        : `Banco ${s.targetDatabase} (${conn ? conn.name : '#' + s.targetConnectionId})`;
    return { when, target };
  }

  async function render() {
    let list, connections;
    try { [list, connections] = await Promise.all([api('GET', '/api/schedules'), api('GET', '/api/connections')]); } catch (err) { errorToast(err); return; }
    clear(app,
      h('div', { class: 'page-head' },
        h('div', {}, h('h1', { text: 'Agendamentos' }),
          h('div', { class: 'muted', text: 'Horários no timezone configurado. Agendas ficam no banco interno e sobrevivem a reinícios.' })),
        h('button', { type: 'button', class: 'primary', text: 'Nova agenda', onclick: () => openForm(null, connections) })),
      h('div', { class: 'card' },
        list.length === 0 ? h('div', { class: 'empty', text: 'Nenhuma agenda. Crie uma (ex.: todos os dias às 03:00).' }) :
          h('div', { class: 'table-wrap' }, h('table', {},
            h('thead', {}, h('tr', {}, ['Nome', 'Quando', 'Alvo', 'Próxima execução', 'Último disparo', ''].map((t) => h('th', { text: t })))),
            h('tbody', {}, list.map((s) => {
              const { when, target } = describe(s, connections);
              return h('tr', {},
                h('td', {}, h('strong', { text: s.name }), s.enabled ? null : h('span', { class: 'badge neutral', text: ' pausada' })),
                h('td', { text: when }), h('td', { text: target }),
                h('td', { text: s.enabled ? fmtDate(s.nextRunAt) : '—' }),
                h('td', { text: fmtDate(s.lastTriggeredAt) }),
                h('td', { class: 'actions' },
                  h('button', { class: 'small', type: 'button', text: 'Editar', onclick: () => openForm(s, connections) }),
                  h('button', {
                    class: 'small danger', type: 'button', text: 'Excluir',
                    onclick: async () => {
                      if (!await confirmDialog('Excluir agenda', `Excluir "${s.name}"?`, 'Excluir', true)) return;
                      try { await api('DELETE', `/api/schedules/${s.id}`); render(); } catch (err) { errorToast(err); }
                    },
                  })));
            }))))));
  }

  function openForm(existing, connections) {
    const s = existing || {
      name: 'Diário 03:00', enabled: true, kind: 'weekly', daysOfWeek: [0, 1, 2, 3, 4, 5, 6], times: ['03:00'],
      intervalMinutes: 360, targetType: 'all', targetConnectionId: null, targetDatabase: null,
    };
    const d = openDialog(existing ? `Editar ${s.name}` : 'Nova agenda');
    const name = h('input', { type: 'text', value: s.name, maxlength: 80 });
    const enabled = checkbox('Agenda ativa', s.enabled);
    const kind = h('select', {},
      h('option', { value: 'weekly', text: 'Dias da semana + horários', selected: s.kind === 'weekly' }),
      h('option', { value: 'interval', text: 'Intervalo fixo', selected: s.kind === 'interval' }));
    const dayBoxes = DAY_NAMES.map((n, i) => h('input', { type: 'checkbox', checked: s.daysOfWeek.includes(i), value: i }));
    const setDays = (arr) => dayBoxes.forEach((b, i) => { b.checked = arr.includes(i); });
    const presets = h('div', { class: 'row' },
      h('button', { type: 'button', class: 'small', text: 'Todos os dias', onclick: () => setDays([0, 1, 2, 3, 4, 5, 6]) }),
      h('button', { type: 'button', class: 'small', text: 'Dias úteis', onclick: () => setDays([1, 2, 3, 4, 5]) }),
      h('button', { type: 'button', class: 'small', text: 'Seg, Qua, Sex', onclick: () => setDays([1, 3, 5]) }),
      h('button', { type: 'button', class: 'small', text: '1x por semana (Dom)', onclick: () => setDays([0]) }));
    const times = h('input', { type: 'text', value: s.times.join(', '), placeholder: '03:00, 15:30' });
    const interval = h('input', { type: 'number', min: 15, value: s.intervalMinutes || 360 });
    const weeklyBox = h('div', {},
      field('Dias', h('div', {}, h('div', { class: 'days' }, dayBoxes.map((b, i) => h('label', {}, b, DAY_NAMES[i]))), presets)),
      field('Horários (HH:mm, separados por vírgula)', times));
    const intervalBox = h('div', {}, field('Intervalo em minutos (mín. 15)', interval));

    const targetType = h('select', {},
      [['all', 'Todas as conexões'], ['connection', 'Uma conexão'], ['database', 'Um banco específico']]
        .map(([v, l]) => h('option', { value: v, text: l, selected: s.targetType === v })));
    const conn = h('select', {}, connections.map((c) => h('option', { value: c.id, text: c.name, selected: c.id === s.targetConnectionId })));
    const dbName = h('input', { type: 'text', value: s.targetDatabase || '' });
    const connField = field('Conexão', conn);
    const dbField = field('Banco', dbName);
    const preview = h('div', {});

    const sync = () => {
      weeklyBox.classList.toggle('hidden', kind.value !== 'weekly');
      intervalBox.classList.toggle('hidden', kind.value !== 'interval');
      connField.classList.toggle('hidden', targetType.value === 'all');
      dbField.classList.toggle('hidden', targetType.value !== 'database');
    };
    kind.addEventListener('change', sync);
    targetType.addEventListener('change', sync);
    sync();

    const collect = () => ({
      name: name.value.trim(), enabled: enabled.input.checked, kind: kind.value,
      daysOfWeek: dayBoxes.filter((b) => b.checked).map((b) => Number(b.value)),
      times: times.value.split(',').map((t) => t.trim()).filter(Boolean),
      intervalMinutes: kind.value === 'interval' ? Number(interval.value) : null,
      targetType: targetType.value,
      targetConnectionId: targetType.value === 'all' ? null : Number(conn.value),
      targetDatabase: targetType.value === 'database' ? dbName.value.trim() : null,
    });

    d.body.append(field('Nome', name), enabled.el, field('Frequência', kind), weeklyBox, intervalBox,
      field('Alvo', targetType), connField, dbField, preview);
    d.foot.append(
      h('button', {
        type: 'button', text: 'Prévia', onclick: async () => {
          try {
            const c = collect();
            const next = await api('POST', '/api/schedules/preview', { kind: c.kind, daysOfWeek: c.daysOfWeek, times: c.times, intervalMinutes: c.intervalMinutes });
            clear(preview, h('div', { class: 'alert info' }, h('strong', { text: 'Próximas execuções: ' }), next.map((x) => fmtDate(x)).join(' · ')));
          } catch (err) { clear(preview, h('div', { class: 'alert error', text: err.message })); }
        },
      }),
      h('button', { type: 'button', text: 'Cancelar', onclick: () => d.close() }),
      h('button', {
        type: 'button', class: 'primary', text: 'Salvar', onclick: (e) => withBusy(e.target, async () => {
          try {
            if (existing) await api('PUT', `/api/schedules/${existing.id}`, collect());
            else await api('POST', '/api/schedules', collect());
            toast('Agenda salva.', 'success');
            d.close();
            render();
          } catch (err) { clear(preview, h('div', { class: 'alert error', text: err.message })); }
        }),
      }));
  }

  render();
}
