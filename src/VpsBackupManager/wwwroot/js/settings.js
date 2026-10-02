import {
  api, h, clear, $, initPage, errorToast, toast, field, checkbox, withBusy, fmtBytes, fmtDate, reauthHeader,
} from './core.js';

if (await initPage()) {
  const app = $('#app');
  const [s, timezones, sys, drive, driveStatus] = await Promise.all([
    api('GET', '/api/settings'), api('GET', '/api/settings/timezones'), api('GET', '/api/system'), api('GET', '/api/dashboard'),
    api('GET', '/api/drive/status'),
  ]);

  const text = (v, attrs = {}) => h('input', { type: 'text', value: v ?? '', ...attrs });
  const num = (v, attrs = {}) => h('input', { type: 'number', value: v ?? '', ...attrs });
  const select = (value, options) => h('select', {}, options.map(([v, l]) => h('option', { value: v, text: l, selected: String(value) === String(v) })));

  const f = {
    vpsName: text(s.vpsName, { maxlength: 80 }),
    publicIp: text(s.publicIp, { placeholder: 'informativo' }),
    timezone: select(s.timezone, timezones.map((t) => [t, t])),
    minFreeDiskMb: num(s.minFreeDiskMb, { min: 100 }),
    rcloneRemote: text(s.rcloneRemote),
    remoteBasePath: text(s.remoteBasePath),
    useVpsSubfolder: checkbox('Criar subpasta por VPS', s.useVpsSubfolder),
    vpsFolderName: text(s.vpsFolderName),
    compression: select(s.compression, [['gzip', 'gzip (padrão, máxima compatibilidade)'], ['zstd', 'zstd (mais rápido/menor)'], ['none', 'Sem compressão']]),
    compressionLevel: num(s.compressionLevel, { min: 1, max: 19 }),
    encryptionEnabled: checkbox('Criptografar com age antes do envio', s.encryptionEnabled),
    ageRecipients: h('textarea', { placeholder: 'age1... (uma chave pública por linha)' }),
    retentionMode: select(s.retentionMode, [['days', 'Manter por N dias'], ['count', 'Manter os últimos N de cada banco'], ['gfs', 'GFS (diários/semanais/mensais)'], ['none', 'Nunca apagar']]),
    retentionDays: num(s.retentionDays, { min: 1 }),
    retentionCount: num(s.retentionCount, { min: 1 }),
    gfsDaily: num(s.gfsDaily, { min: 0 }), gfsWeekly: num(s.gfsWeekly, { min: 0 }), gfsMonthly: num(s.gfsMonthly, { min: 0 }),
    keepLocalCopy: checkbox('Manter cópia local após o upload', s.keepLocalCopy),
    keepLocalDays: num(s.keepLocalDays, { min: 1 }),
    webhookEnabled: checkbox('Enviar webhook', s.webhookEnabled),
    webhookUrl: h('input', { type: 'url', value: s.webhookUrl || '', placeholder: 'https://…' }),
    webhookOnSuccess: checkbox('Ao concluir com sucesso', s.webhookOnSuccess),
    webhookOnFailure: checkbox('Ao ocorrer erro', s.webhookOnFailure),
    uploadMaxAttempts: num(s.uploadMaxAttempts, { min: 1, max: 10 }),
    uploadInitialBackoffSeconds: num(s.uploadInitialBackoffSeconds, { min: 1 }),
    uploadMaxBackoffSeconds: num(s.uploadMaxBackoffSeconds, { min: 1 }),
    defaultDumpTimeoutMinutes: num(s.defaultDumpTimeoutMinutes, { min: 1 }),
    scheduledLockWaitMinutes: num(s.scheduledLockWaitMinutes, { min: 0 }),
    catchUpHours: num(s.catchUpHours, { min: 0, max: 72 }),
  };
  f.ageRecipients.value = (s.ageRecipients || []).join('\n');

  const pathPreview = h('code', {});
  const updatePreview = () => {
    const now = new Date();
    const parts = [f.remoteBasePath.value.replace(/^\/+|\/+$/g, '')];
    if (f.useVpsSubfolder.input.checked) parts.push(f.vpsFolderName.value.trim());
    const y = now.getFullYear(); const m = String(now.getMonth() + 1).padStart(2, '0'); const d = String(now.getDate()).padStart(2, '0');
    parts.push(`${y}/${m}/${d}/mysql/minha-conexao/banco_${y}-${m}-${d}_03-00-00.sql.gz`);
    pathPreview.textContent = `${f.rcloneRemote.value}:${parts.join('/')}`;
  };
  [f.rcloneRemote, f.remoteBasePath, f.vpsFolderName].forEach((x) => x.addEventListener('input', updatePreview));
  f.useVpsSubfolder.input.addEventListener('change', updatePreview);
  updatePreview();

  const retentionBoxes = { days: field('Dias', f.retentionDays, undefined, 'Retenção — dias'), count: field('Quantidade por banco', f.retentionCount),
    gfs: h('div', { class: 'form-grid' }, field('Diários', f.gfsDaily), field('Semanais', f.gfsWeekly), field('Mensais', f.gfsMonthly)) };
  const syncRetention = () => Object.entries(retentionBoxes).forEach(([k, el]) => el.classList.toggle('hidden', f.retentionMode.value !== k));
  f.retentionMode.addEventListener('change', syncRetention);
  syncRetention();

  const msg = h('div', {});
  const storageResult = h('div', {});
  const webhookResult = h('div', {});
  // Re-authentication for sensitive changes (encryption, retention, destination, webhook, Google account).
  const confirmPw = h('input', { type: 'password', autocomplete: 'current-password', placeholder: 'só para mudanças sensíveis' });
  const webhookSecret = h('input', { type: 'password', autocomplete: 'new-password', placeholder: s.webhookSecretSet ? '(segredo definido — digite para trocar)' : 'opcional' });

  const collect = () => ({
    vpsName: f.vpsName.value.trim(), publicIp: f.publicIp.value.trim() || null, timezone: f.timezone.value,
    minFreeDiskMb: Number(f.minFreeDiskMb.value),
    compression: f.compression.value, compressionLevel: Number(f.compressionLevel.value),
    encryptionEnabled: f.encryptionEnabled.input.checked,
    ageRecipients: f.ageRecipients.value.split(/\s+/).map((x) => x.trim()).filter(Boolean),
    rcloneRemote: f.rcloneRemote.value.trim(), remoteBasePath: f.remoteBasePath.value.trim(),
    useVpsSubfolder: f.useVpsSubfolder.input.checked, vpsFolderName: f.vpsFolderName.value.trim(),
    driveAccountEmail: s.driveAccountEmail || null,
    retentionMode: f.retentionMode.value, retentionDays: Number(f.retentionDays.value), retentionCount: Number(f.retentionCount.value),
    gfsDaily: Number(f.gfsDaily.value), gfsWeekly: Number(f.gfsWeekly.value), gfsMonthly: Number(f.gfsMonthly.value),
    keepLocalCopy: f.keepLocalCopy.input.checked, keepLocalDays: Number(f.keepLocalDays.value),
    webhookEnabled: f.webhookEnabled.input.checked, webhookUrl: f.webhookUrl.value.trim() || null,
    webhookOnSuccess: f.webhookOnSuccess.input.checked, webhookOnFailure: f.webhookOnFailure.input.checked,
    uploadMaxAttempts: Number(f.uploadMaxAttempts.value), uploadInitialBackoffSeconds: Number(f.uploadInitialBackoffSeconds.value),
    uploadMaxBackoffSeconds: Number(f.uploadMaxBackoffSeconds.value), defaultDumpTimeoutMinutes: Number(f.defaultDumpTimeoutMinutes.value),
    scheduledLockWaitMinutes: Number(f.scheduledLockWaitMinutes.value), catchUpHours: Number(f.catchUpHours.value),
  });

  async function save() {
    try {
      await api('PUT', '/api/settings', collect(), reauthHeader(confirmPw.value));
      confirmPw.value = '';
      if (webhookSecret.value) { await api('POST', '/api/settings/webhook-secret', { secret: webhookSecret.value }); webhookSecret.value = ''; }
      clear(msg, h('div', { class: 'alert success', text: 'Configurações salvas.' }));
      toast('Configurações salvas.', 'success');
      return true;
    } catch (err) {
      clear(msg, h('div', { class: 'alert error', text: err.message }));
      window.scrollTo({ top: 0, behavior: 'smooth' });
      return false;
    }
  }

  const tools = Object.entries(sys.tools).map(([k, v]) => h('tr', {}, h('th', { text: k }), h('td', { class: 'mono small', text: v || 'não instalado' })));

  // change password
  const cur = h('input', { type: 'password', autocomplete: 'current-password' });
  const np = h('input', { type: 'password', autocomplete: 'new-password' });
  const pwMsg = h('div', {});

  // ---------------------------------------------------------------- Google Drive via Gmail
  function driveConnectBox() {
    const status = h('div', {});
    const gmail = h('input', { type: 'text', value: driveStatus.email || '', placeholder: 'voce@gmail.com', autocomplete: 'email' });
    const step2 = h('div', { class: 'hidden' });
    const pasted = h('input', { type: 'text', placeholder: 'http://127.0.0.1:53682/?state=...&code=...' });
    const result = h('div', {});

    clear(status, driveStatus.configured
      ? h('div', { class: `alert ${driveStatus.ok === false ? 'warning' : 'success'}` },
        h('strong', { text: 'Conectado' }), ` — conta ${driveStatus.email || '(desconhecida)'}`,
        driveStatus.checkedAt ? h('div', { class: 'small', text: `Último teste: ${fmtDate(driveStatus.checkedAt)} — ${driveStatus.message || ''}` }) : null)
      : h('div', { class: 'alert info', text: 'Google Drive ainda não conectado.' }));

    const connectBtn = h('button', {
      type: 'button', class: 'primary', text: driveStatus.configured ? 'Reconectar' : 'Conectar Google Drive',
      onclick: (e) => withBusy(e.target, async () => {
        try {
          const r = await api('POST', '/api/drive/connect', { email: gmail.value }, reauthHeader(confirmPw.value));
          confirmPw.value = '';
          window.open(r.authUrl, '_blank', 'noopener');
          clear(step2,
            h('ol', { class: 'steps' },
              h('li', {}, 'Na aba do Google que abriu, entre com ', h('strong', { text: gmail.value }), ' e clique em ', h('strong', { text: 'Permitir' }), '.'),
              h('li', { text: 'O navegador vai mostrar uma página que NÃO abre (endereço começando com http://127.0.0.1:53682). Isso é esperado.' }),
              h('li', { text: 'Copie o endereço inteiro da barra do navegador e cole abaixo.' })),
            h('p', { class: 'small' }, 'A aba não abriu? ', h('a', { href: r.authUrl, target: '_blank', rel: 'noopener', text: 'Clique aqui' }), '.'),
            field('Endereço copiado', pasted),
            h('button', {
              type: 'button', class: 'primary', text: 'Concluir conexão',
              onclick: (ev) => withBusy(ev.target, async () => {
                try {
                  const c = await api('POST', '/api/drive/complete', { url: pasted.value });
                  clear(result, h('div', { class: `alert ${c.ok ? 'success' : 'error'}`, text: c.message }));
                  if (c.ok) setTimeout(() => location.reload(), 2500);
                } catch (err) { clear(result, h('div', { class: 'alert error', text: err.message })); }
              }),
            }));
          step2.classList.remove('hidden');
        } catch (err) { clear(result, h('div', { class: 'alert error', text: err.message })); }
      }),
    });

    return h('fieldset', {}, h('legend', { text: 'Google Drive' }),
      status,
      h('p', { class: 'small muted', text: 'Informe o Gmail onde os backups serão guardados. O Google exige uma autorização (um clique em "Permitir") só na primeira vez; depois tudo funciona sozinho, mesmo com seu PC desligado. O acesso fica limitado aos arquivos criados por este sistema.' }),
      h('div', { class: 'row' }, h('div', { class: 'spacer' }, field('Gmail', gmail)), connectBtn),
      h('p', { class: 'small muted', text: 'Exige a senha atual (campo "Confirmar com a senha atual" no topo da página).' }),
      step2, result);
  }

  clear(app,
    h('div', { class: 'page-head' }, h('div', {}, h('h1', { text: 'Configurações' })),
      h('button', { type: 'button', class: 'primary', text: 'Salvar configurações', onclick: (e) => withBusy(e.target, save) })),
    msg,
    h('div', { class: 'card' },
      field('Confirmar com a senha atual', confirmPw,
        'Obrigatória para alterar criptografia, retenção, destino, webhook ou conectar o Google Drive. Alterações sensíveis ficam registradas nos Logs e disparam alerta no webhook.')),
    h('div', { class: 'card' },
      h('fieldset', {}, h('legend', { text: 'VPS' }), h('div', { class: 'form-grid' },
        field('Nome amigável', f.vpsName), field('Hostname', h('input', { type: 'text', value: sys.hostname, disabled: true })),
        field('IP público (informativo)', f.publicIp, 'Não é usado para conectar aos bancos locais.'),
        field('Timezone', f.timezone, 'Usado em agendas, nomes de arquivos, dashboard e histórico.'),
        field('Diretório temporário', h('input', { type: 'text', value: sys.backupRoot + '/work', disabled: true }), 'Definido por BACKUP_ROOT no .env.'),
        field('Espaço mínimo livre (MB)', f.minFreeDiskMb, 'Backups são abortados abaixo deste limite.'))),

      driveConnectBox(),

      h('fieldset', {}, h('legend', { text: 'Pastas no Google Drive' }),
        h('div', { class: 'form-grid' },
          field('Pasta principal', f.remoteBasePath, 'Ex.: Backups. Criada automaticamente.'),
          field('Subpasta da VPS (padrão)', f.vpsFolderName, 'Usada quando o banco não define "Pasta no Drive".'),
          field('Nome interno do remote (rclone)', f.rcloneRemote, 'Normalmente não precisa mudar.')),
        f.useVpsSubfolder.el,
        h('p', { class: 'small' }, 'Exemplo de caminho: ', pathPreview),
        h('div', { class: 'row' },
          h('button', {
            type: 'button', text: 'Salvar e testar upload',
            onclick: (e) => withBusy(e.target, async () => {
              if (!await save()) return;
              try {
                const r = await api('POST', '/api/settings/storage/test');
                clear(storageResult, h('div', { class: `alert ${r.ok ? 'success' : 'error'}`, text: r.message }));
              } catch (err) { clear(storageResult, h('div', { class: 'alert error', text: err.message })); }
            }),
          }),
          h('span', { class: 'muted small', text: drive.drive.checkedAt ? `Último teste: ${fmtDate(drive.drive.checkedAt)} — ${drive.drive.ok ? 'OK' : 'falhou'}` : '' })),
        storageResult),

      h('fieldset', {}, h('legend', { text: 'Compressão e criptografia' }),
        h('div', { class: 'form-grid' }, field('Compressão', f.compression), field('Nível', f.compressionLevel, 'gzip 1–9 (6 recomendado), zstd 1–19 (3 recomendado)')),
        f.encryptionEnabled.el,
        field('Chaves públicas age (destinatários)', f.ageRecipients,
          'Somente a chave PÚBLICA fica na VPS. Guarde a chave privada fora da VPS — sem ela o backup não pode ser restaurado. Ver docs/06-restore.md.')),

      h('fieldset', {}, h('legend', { text: 'Retenção no destino' }),
        field('Política', f.retentionMode, 'O backup mais recente de cada banco nunca é apagado. Só arquivos criados por este sistema são removidos.'),
        retentionBoxes.days, retentionBoxes.count, retentionBoxes.gfs),

      h('fieldset', {}, h('legend', { text: 'Cópia local' }),
        f.keepLocalCopy.el, field('Manter por (dias)', f.keepLocalDays, 'Desativado = arquivos locais apagados logo após o upload confirmado.')),

      h('fieldset', {}, h('legend', { text: 'Webhook (alertas)' }),
        f.webhookEnabled.el, field('URL', f.webhookUrl), f.webhookOnSuccess.el, f.webhookOnFailure.el,
        field('Segredo HMAC', webhookSecret, 'Assinatura no cabeçalho X-VBM-Signature: sha256=…'),
        h('div', { class: 'row' },
          h('button', {
            type: 'button', text: 'Salvar e enviar teste',
            onclick: (e) => withBusy(e.target, async () => {
              if (!await save()) return;
              const r = await api('POST', '/api/settings/webhook/test');
              clear(webhookResult, h('div', { class: `alert ${r.ok ? 'success' : 'error'}`, text: r.message }));
            }),
          }),
          s.webhookSecretSet ? h('button', {
            type: 'button', class: 'small danger', text: 'Remover segredo',
            onclick: async () => { await api('POST', '/api/settings/webhook-secret', { secret: null }); toast('Segredo removido.', 'success'); },
          }) : null),
        webhookResult),

      h('fieldset', {}, h('legend', { text: 'Confiabilidade' }), h('div', { class: 'form-grid' },
        field('Tentativas de upload', f.uploadMaxAttempts), field('Espera inicial entre tentativas (s)', f.uploadInitialBackoffSeconds),
        field('Espera máxima (s)', f.uploadMaxBackoffSeconds, 'Backoff exponencial com jitter.'),
        field('Timeout padrão do dump (min)', f.defaultDumpTimeoutMinutes),
        field('Espera por backup em andamento (min)', f.scheduledLockWaitMinutes, 'Agenda que encontra outro backup rodando aguarda até este limite.'),
        field('Janela de recuperação (h)', f.catchUpHours, 'Se a VPS estava desligada no horário, executa ao voltar dentro desta janela.')))),

    h('div', { class: 'card' }, h('h2', { text: 'Sistema' }),
      h('table', {}, h('tbody', {},
        h('tr', {}, h('th', { text: 'Sistema operacional' }), h('td', { text: `${sys.os} (${sys.arch})` })),
        h('tr', {}, h('th', { text: 'Versão' }), h('td', { text: `${sys.appVersion} · .NET ${sys.dotnet}` })),
        h('tr', {}, h('th', { text: 'Disco (backups)' }), h('td', { text: sys.disk.freeBytes !== undefined ? `${fmtBytes(sys.disk.freeBytes)} livres de ${fmtBytes(sys.disk.totalBytes)}` : '—' })),
        h('tr', {}, h('th', { text: 'Descoberta Docker' }), h('td', { text: sys.dockerDiscovery ? 'habilitada (DOCKER_DISCOVERY_URL definido)' : 'desabilitada' })),
        tools))),

    h('div', { class: 'card' }, h('h2', { text: 'Trocar minha senha' }),
      h('div', { class: 'form-grid' }, field('Senha atual', cur), field('Nova senha', np, 'Mín. 12 caracteres. Outras sessões serão encerradas.')),
      h('button', {
        type: 'button', text: 'Trocar senha',
        onclick: (e) => withBusy(e.target, async () => {
          try {
            await api('POST', '/api/auth/change-password', { currentPassword: cur.value, newPassword: np.value });
            cur.value = ''; np.value = '';
            clear(pwMsg, h('div', { class: 'alert success', text: 'Senha alterada.' }));
          } catch (err) { clear(pwMsg, h('div', { class: 'alert error', text: err.message })); }
        }),
      }), pwMsg));
}
