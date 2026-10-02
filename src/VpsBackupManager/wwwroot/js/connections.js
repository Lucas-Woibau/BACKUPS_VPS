import {
  api, h, clear, $, initPage, fmtDate, statusBadge, errorToast, toast, openDialog, confirmDialog, field, checkbox, withBusy,
} from './core.js';
import { openRunDialog } from './runs.js';

const TYPE_LABEL = { sqlserver: 'SQL Server', mysql: 'MySQL', mariadb: 'MariaDB', postgresql: 'PostgreSQL' };
const TYPE_ORDER = ['sqlserver', 'mysql', 'mariadb', 'postgresql'];
const SSL_LABEL = {
  disabled: 'Desabilitado', preferred: 'Criptografar (aceita certificado próprio)', required: 'Obrigatório (aceita certificado próprio)',
  verify_ca: 'Verificar CA', verify_identity: 'Verificar CA + hostname',
};

if (await initPage()) {
  const app = $('#app');
  const types = (await api('GET', '/api/connections/types'))
    .sort((a, b) => TYPE_ORDER.indexOf(a.type) - TYPE_ORDER.indexOf(b.type));
  const settings = await api('GET', '/api/settings');

  const parseList = (text) => text.split(/[,;\n]/).map((x) => x.trim()).filter(Boolean);

  async function render() {
    let list;
    try { list = await api('GET', '/api/connections'); } catch (err) { errorToast(err); return; }
    clear(app,
      h('div', { class: 'page-head' },
        h('div', {}, h('h1', { text: 'Bancos de dados' }),
          h('div', { class: 'muted', text: 'Cada item é um banco (ou servidor) a copiar. Ex.: "VPS - Banco 1". Adicione quantos precisar.' })),
        h('div', { class: 'row' },
          h('button', { type: 'button', text: 'Descobrir serviços', onclick: openDiscovery }),
          h('button', { type: 'button', class: 'primary', text: '+ Novo banco', onclick: () => openForm(null) }))),
      h('div', { class: 'card' },
        list.length === 0 ? h('div', { class: 'empty', text: 'Nenhum banco cadastrado. Clique em "+ Novo banco".' }) :
          h('div', { class: 'table-wrap' }, h('table', {},
            h('thead', {}, h('tr', {}, ['Nome', 'Tipo', 'IP : porta', 'Banco(s)', 'Pasta no Drive', 'Último teste', ''].map((t) => h('th', { text: t })))),
            h('tbody', {}, list.map(rowFor))))));
  }

  function rowFor(c) {
    const testCell = c.lastTestAt
      ? h('span', {}, statusBadge(c.lastTestOk ? 'success' : 'error'), ' ', h('span', { class: 'small muted', text: fmtDate(c.lastTestAt) }))
      : h('span', { class: 'muted', text: 'nunca' });
    const folder = (c.options && c.options.drive_folder) || (settings.useVpsSubfolder ? settings.vpsFolderName : '(raiz)');
    return h('tr', {},
      h('td', {}, h('strong', { text: c.name }), c.enabled ? null : h('span', { class: 'badge neutral', text: ' desativado' })),
      h('td', { text: TYPE_LABEL[c.dbType] || c.dbType }),
      h('td', { class: 'mono', text: `${c.host}:${c.port}` }),
      h('td', { class: 'mono', text: c.backupAll ? `todos (${c.protectedCount})` : c.selectedDatabases.join(', ') }),
      h('td', { class: 'mono small', text: `${settings.remoteBasePath}/${folder}` }),
      h('td', {}, testCell),
      h('td', { class: 'actions' },
        h('button', { class: 'small', type: 'button', text: 'Testar', onclick: (e) => withBusy(e.target, () => testSaved(c)) }),
        h('button', { class: 'small', type: 'button', text: 'Backup agora', onclick: () => runBackup({ targetType: 'connection', connectionId: c.id }) }),
        h('button', { class: 'small', type: 'button', text: 'Editar', onclick: () => openForm(c) }),
        h('button', {
          class: 'small danger', type: 'button', text: 'Excluir',
          onclick: async () => {
            if (!await confirmDialog('Excluir banco', `Excluir "${c.name}"? O histórico é mantido; arquivos no Google Drive não são apagados.`, 'Excluir', true)) return;
            try { await api('DELETE', `/api/connections/${c.id}`); toast('Excluído.', 'success'); render(); } catch (err) { errorToast(err); }
          },
        })));
  }

  async function testSaved(c) {
    try {
      const r = await api('POST', `/api/connections/${c.id}/test`);
      toast(r.ok ? `${r.message} ${r.serverVersion || ''}` : r.message, r.ok ? 'success' : 'error', 8000);
      render();
    } catch (err) { errorToast(err); }
  }

  async function runBackup(body) {
    try {
      const r = await api('POST', '/api/backups/run', body);
      toast('Backup iniciado.', 'success');
      openRunDialog(r.runId);
    } catch (err) { errorToast(err); }
  }

  async function openDiscovery() {
    const d = openDialog('Descoberta de serviços', { wide: true });
    d.body.append(h('p', { class: 'muted', text: 'Verificando portas padrão no host e containers (se habilitado)…' }));
    try {
      const r = await api('GET', '/api/discovery');
      clear(d.body,
        h('p', { class: 'muted', text: `Host do Docker: ${r.hostGateway}. ${r.docker}` }),
        h('p', { class: 'small muted', text: 'Senhas nunca são descobertas: informe o usuário de backup.' }),
        r.services.length === 0 ? h('div', { class: 'empty', text: 'Nenhum serviço encontrado.' }) :
          h('table', {}, h('thead', {}, h('tr', {}, ['Origem', 'Tipo', 'Nome', 'Host:porta', 'Detalhes', ''].map((t) => h('th', { text: t })))),
            h('tbody', {}, r.services.map((s) => h('tr', {},
              h('td', { text: s.source }), h('td', { text: TYPE_LABEL[s.type] || s.type }), h('td', { text: s.name }),
              h('td', { class: 'mono', text: `${s.suggestedHost}:${s.suggestedPort}` }), h('td', { class: 'small', text: s.detail }),
              h('td', {}, h('button', {
                class: 'small', type: 'button', text: 'Usar',
                onclick: () => { d.close(); openForm(null, { name: s.name, dbType: s.type, host: s.suggestedHost, port: s.suggestedPort }); },
              })))))));
    } catch (err) { clear(d.body, h('div', { class: 'alert error', text: err.message })); }
  }

  // ------------------------------------------------------------------ form
  function openForm(existing, prefill = {}) {
    const c = existing || {
      name: '', dbType: types[0].type, host: '', port: types[0].defaultPort, username: '', sslMode: 'preferred', options: {},
      backupAll: false, includeSystem: false, selectedDatabases: [], excludedDatabases: [], enabled: true, ...prefill,
    };
    const opts = { ...(c.options || {}) };
    const d = openDialog(existing ? `Editar: ${c.name}` : 'Novo banco', { wide: true });

    // 1. Identificação e acesso
    const name = h('input', { type: 'text', value: c.name, maxlength: 80, placeholder: 'VPS - Banco 1' });
    const type = h('select', {}, types.map((t) => h('option', { value: t.type, text: TYPE_LABEL[t.type] || t.type, selected: t.type === c.dbType })));
    const host = h('input', { type: 'text', value: c.host, placeholder: 'IP da VPS, host.docker.internal ou nome do container' });
    const port = h('input', { type: 'number', value: c.port, min: 1, max: 65535 });
    const dbNames = h('input', { type: 'text', value: c.backupAll ? '' : (c.selectedDatabases || []).join(', '), placeholder: 'ex.: loja  (vários: loja, crm)', list: 'db-suggestions' });
    const suggestions = h('datalist', { id: 'db-suggestions' });
    const allDbs = checkbox('Copiar TODOS os bancos deste servidor (novos bancos entram automaticamente)', c.backupAll);
    const user = h('input', { type: 'text', value: c.username, autocomplete: 'off' });
    const pass = h('input', { type: 'password', autocomplete: 'new-password', placeholder: existing ? '(deixe vazio para manter a senha atual)' : '' });

    // 2. Destino
    const driveFolder = h('input', { type: 'text', value: opts.drive_folder || '', placeholder: settings.useVpsSubfolder ? settings.vpsFolderName : 'VPS-Producao/Banco1' });
    const drivePreview = h('code', { class: 'small' });

    // 3. SQL Server
    const serverDir = h('input', { type: 'text', value: opts.server_backup_dir || '/var/opt/mssql/backup' });
    const localDir = h('input', { type: 'text', value: opts.local_backup_dir || '/mssql-backups' });
    const verify = checkbox('Verificar o backup (RESTORE VERIFYONLY)', opts.verify_backup ?? true);
    const nativeComp = checkbox('Compressão nativa do SQL Server (não disponível na edição Express)', opts.native_compression ?? false);
    const sqlBox = h('fieldset', {}, h('legend', { text: 'SQL Server — pasta onde o backup é gerado' }),
      h('p', { class: 'small muted', text: 'O SQL Server grava o arquivo .bak numa pasta do servidor; o app lê essa mesma pasta, compacta e envia ao Drive. A pasta precisa estar compartilhada (veja docs/04-bancos.md).' }),
      h('div', { class: 'form-grid' },
        field('Pasta de backup no SQL Server', serverDir, 'Caminho como o SQL Server enxerga (Linux/Docker: /var/opt/mssql/backup).'),
        field('Mesma pasta no backup manager', localDir, 'Montada no container (padrão /mssql-backups = ./mssql-backups na VPS).')),
      verify.el, nativeComp.el);

    // 4. Avançado
    const ssl = h('select', {}, Object.entries(SSL_LABEL).map(([v, l]) => h('option', { value: v, text: l, selected: v === c.sslMode })));
    const timeout = h('input', { type: 'number', min: 0, max: 2880, value: opts.timeout_minutes ?? '', placeholder: 'padrão das configurações' });
    const excluded = h('input', { type: 'text', value: (c.excludedDatabases || []).join(', '), placeholder: 'bancos a ignorar quando "todos" estiver marcado' });
    const includeSystem = checkbox('Incluir bancos de sistema (master/msdb/model, mysql, postgres)', c.includeSystem);
    const enabled = checkbox('Ativo (incluído nos backups agendados)', c.enabled);
    const mysqlOpts = {
      single_transaction: checkbox('--single-transaction (snapshot consistente, sem travar)', opts.single_transaction ?? true),
      routines: checkbox('Incluir procedures/functions', opts.routines ?? true),
      events: checkbox('Incluir events', opts.events ?? true),
      triggers: checkbox('Incluir triggers', opts.triggers ?? true),
    };
    const dumpBinary = h('select', {}, ['auto', 'mariadb-dump', 'mysqldump'].map((v) => h('option', { value: v, text: v, selected: (opts.dump_binary || 'auto') === v })));
    const mysqlBox = h('div', {}, Object.values(mysqlOpts).map((x) => x.el), field('Ferramenta de dump', dumpBinary));
    const maintenanceDb = h('input', { type: 'text', value: opts.maintenance_db || 'postgres' });
    const dumpGlobals = checkbox('Exportar também roles/tablespaces (pg_dumpall --globals-only)', opts.dump_globals ?? false);
    const pgBox = h('div', {}, field('Banco de manutenção', maintenanceDb), dumpGlobals.el);
    const advanced = h('details', {}, h('summary', { text: 'Opções avançadas' }),
      h('div', { class: 'form-grid' }, field('SSL/TLS', ssl), field('Timeout do backup (min)', timeout), field('Ignorar bancos', excluded)),
      includeSystem.el, mysqlBox, pgBox);

    const syncType = () => {
      const t = type.value;
      sqlBox.classList.toggle('hidden', t !== 'sqlserver');
      mysqlBox.classList.toggle('hidden', !['mysql', 'mariadb'].includes(t));
      pgBox.classList.toggle('hidden', t !== 'postgresql');
    };
    const syncAll = () => { dbNames.disabled = allDbs.input.checked; };
    const updatePreview = () => {
      const folder = driveFolder.value.trim().replace(/^\/+|\/+$/g, '') || (settings.useVpsSubfolder ? settings.vpsFolderName : '');
      const now = new Date();
      const ymd = [now.getFullYear(), String(now.getMonth() + 1).padStart(2, '0'), String(now.getDate()).padStart(2, '0')];
      const slug = (name.value.trim() || 'conexao').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[^A-Za-z0-9_.-]+/g, '_');
      const db = (allDbs.input.checked ? 'banco' : parseList(dbNames.value)[0]) || 'banco';
      const ext = type.value === 'sqlserver' ? 'bak' : type.value === 'postgresql' ? 'dump' : 'sql';
      drivePreview.textContent = [settings.rcloneRemote + ':' + settings.remoteBasePath, folder, ...ymd, type.value, slug,
        `${db}_${ymd.join('-')}_03-00-00.${ext}.gz`].filter(Boolean).join('/');
    };
    type.addEventListener('change', () => {
      const def = types.find((x) => x.type === type.value);
      if (def && (!port.value || [1433, 3306, 5432].includes(Number(port.value)))) port.value = def.defaultPort;
      syncType(); updatePreview();
    });
    allDbs.input.addEventListener('change', () => { syncAll(); updatePreview(); });
    [name, dbNames, driveFolder].forEach((x) => x.addEventListener('input', updatePreview));
    syncType(); syncAll(); updatePreview();

    const result = h('div', {});
    const collect = () => {
      const options = {};
      const folder = driveFolder.value.trim().replace(/^\/+|\/+$/g, '');
      if (folder) options.drive_folder = folder;
      if (timeout.value) options.timeout_minutes = Number(timeout.value);
      if (type.value === 'sqlserver') {
        Object.assign(options, { server_backup_dir: serverDir.value.trim(), local_backup_dir: localDir.value.trim(), verify_backup: verify.input.checked, native_compression: nativeComp.input.checked });
      } else if (type.value === 'postgresql') {
        Object.assign(options, { maintenance_db: maintenanceDb.value.trim() || 'postgres', dump_globals: dumpGlobals.input.checked });
      } else {
        for (const [k, v] of Object.entries(mysqlOpts)) options[k] = v.input.checked;
        options.dump_binary = dumpBinary.value;
      }
      return {
        name: name.value.trim(), dbType: type.value, host: host.value.trim(), port: Number(port.value), username: user.value.trim(),
        password: pass.value || null, sslMode: ssl.value, options, backupAll: allDbs.input.checked,
        includeSystem: includeSystem.input.checked, enabled: enabled.input.checked,
        selectedDatabases: allDbs.input.checked ? [] : parseList(dbNames.value),
        excludedDatabases: parseList(excluded.value),
      };
    };

    const testBtn = h('button', {
      type: 'button', text: 'Testar conexão',
      onclick: (e) => withBusy(e.target, async () => {
        try {
          const qs = existing ? `?id=${existing.id}` : '';
          const r = await api('POST', `/api/connections/test${qs}`, { ...collect(), backupAll: true, selectedDatabases: [] });
          clear(result, h('div', { class: `alert ${r.ok ? 'success' : 'error'}` },
            `${r.message}${r.serverVersion ? ` (versão ${r.serverVersion})` : ''}`,
            r.ok && r.databases ? h('div', { class: 'small', text: `Bancos encontrados: ${r.databases.join(', ') || 'nenhum'}` }) : null));
          if (r.ok && r.databases) clear(suggestions, r.databases.map((x) => h('option', { value: x })));
        } catch (err) { clear(result, h('div', { class: 'alert error', text: err.message })); }
      }),
    });

    d.body.append(
      h('fieldset', {}, h('legend', { text: '1. Banco de dados' }),
        h('div', { class: 'form-grid' },
          field('Nome (identificação)', name, 'Livre. Ex.: VPS - Banco 1, Loja produção.'),
          field('Tipo de banco', type),
          field('IP / host', host, 'Na própria VPS com o app em Docker: host.docker.internal. Em outro container: nome do container.'),
          field('Porta', port),
          field('Nome do banco', dbNames, 'Use "Testar conexão" para ver a lista. Vários: separe por vírgula.'),
          field('Usuário', user),
          field('Senha', pass)),
        suggestions, allDbs.el),
      h('fieldset', {}, h('legend', { text: '2. Destino no Google Drive' }),
        field('Pasta no Drive', driveFolder, `Dentro de "${settings.remoteBasePath}". Criada automaticamente no primeiro backup. Vazio = ${settings.useVpsSubfolder ? settings.vpsFolderName : 'raiz'}.`),
        h('p', { class: 'small' }, 'Exemplo de arquivo: ', drivePreview)),
      sqlBox, advanced, enabled.el, result);

    d.foot.append(testBtn,
      h('button', { type: 'button', text: 'Cancelar', onclick: () => d.close() }),
      h('button', {
        type: 'button', class: 'primary', text: 'Salvar',
        onclick: (e) => withBusy(e.target, async () => {
          try {
            if (existing) await api('PUT', `/api/connections/${existing.id}`, collect());
            else await api('POST', '/api/connections', collect());
            toast('Banco salvo.', 'success');
            d.close();
            render();
          } catch (err) { clear(result, h('div', { class: 'alert error', text: err.message })); }
        }),
      }));
  }

  render();
}
