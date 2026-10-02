import { h, clear, $, initPage, helpContent } from './core.js';
import { HELP } from './help.js';

const STEPS = [
  {
    title: '1. Conectar o Google Drive (uma vez)',
    items: [
      'Configurações → Google Drive → digite seu Gmail → Conectar Google Drive.',
      'Na aba do Google: entre na conta e clique em Permitir.',
      'O navegador vai para http://127.0.0.1:53682/... e a página NÃO abre — é esperado.',
      'Copie esse endereço inteiro, cole em "Endereço copiado" e clique em Concluir conexão.',
      'Deve aparecer "Google Drive conectado (seu@gmail.com)".',
    ],
  },
  {
    title: '2. Cadastrar cada banco',
    items: [
      'Bancos → + Novo banco.',
      'Nome: algo como "VPS - MySeeds". Tipo: SQL Server.',
      'IP / host: nome do container do banco (myseeds_db ou bibliotrack_db). Porta: 1433.',
      'Usuário: sa. Senha: a SA_PASSWORD do projeto (veja Perguntas frequentes).',
      'Clique em Testar conexão: aparecem os bancos encontrados. Copie o nome para "Nome do banco".',
      'Pasta no Drive: ex. VPS/MySeeds.',
      'SQL Server: Pasta de backup no SQL Server = /var/opt/mssql/backup; Mesma pasta no backup manager = /mssql/myseeds (ou /mssql/bibliotrack).',
      'Salvar.',
    ],
  },
  {
    title: '3. Fazer o primeiro backup',
    items: [
      'Bancos → Backup agora no banco cadastrado.',
      'A janela mostra o andamento. Status "Sucesso" = arquivo enviado e conferido no Drive.',
      'Confira no drive.google.com: Backups/VPS/MySeeds/AAAA/MM/DD/sqlserver/...bak.gz',
    ],
  },
  {
    title: '4. Agendar',
    items: [
      'Agendamentos → Nova agenda.',
      'Frequência: Dias da semana + horários → Todos os dias → 03:00.',
      'Alvo: Todas as conexões (uma agenda cobre todos os bancos).',
      'Clique em Prévia para conferir as próximas execuções e depois em Salvar.',
    ],
  },
  {
    title: '5. Acompanhar',
    items: [
      'Dashboard: "Último backup bem-sucedido" deve ser sempre de menos de 24 h.',
      'Bancos com falha aparecem em vermelho no topo do Dashboard.',
      'Histórico: detalhes de cada backup (tamanho, checksum, destino, erro).',
      'Opcional: Configurações → Webhook para receber aviso de erro (Discord, n8n…).',
    ],
  },
];

const FAQ = [
  {
    q: 'Qual é a senha do "sa" do SQL Server?',
    a: ['É a senha definida no projeto do banco (variável SA_PASSWORD no .env do MySeeds/Bibliotrack).', 'Para ver direto na VPS:'],
    code: 'docker exec myseeds_db printenv MSSQL_SA_PASSWORD\ndocker exec bibliotrack_db printenv MSSQL_SA_PASSWORD',
  },
  {
    q: 'Quero um usuário só para backup (mais seguro que o sa)',
    a: ['Rode no SQL Server (como sa), trocando o nome do banco e a senha:'],
    code: "CREATE LOGIN backup_user WITH PASSWORD = 'SenhaForte#2026';\nUSE [NomeDoBanco];\nCREATE USER backup_user FOR LOGIN backup_user;\nALTER ROLE db_backupoperator ADD MEMBER backup_user;\nUSE [master];\nGRANT VIEW ANY DATABASE TO backup_user;",
  },
  {
    q: 'Não sei o nome do banco',
    a: ['No cadastro, preencha IP, usuário e senha e clique em "Testar conexão". A lista de bancos aparece embaixo.'],
  },
  {
    q: 'Esqueci a senha do painel',
    a: ['Na VPS, gere uma nova senha (troque admin pelo seu usuário):'],
    code: 'docker exec -it vps_backup_manager dotnet VpsBackupManager.dll reset-admin-password admin',
  },
  {
    q: 'Como abro o painel do meu PC?',
    a: ['O painel só escuta dentro da VPS (mais seguro). Abra um túnel no PowerShell e deixe a janela aberta:'],
    code: 'ssh -L 8095:127.0.0.1:8095 root@IP_DA_VPS\n# depois abra http://localhost:8095',
  },
  {
    q: 'Como restauro um backup?',
    a: [
      'Baixe o .bak.gz do Google Drive e descompacte (gunzip ou 7-Zip).',
      'Copie o .bak para a pasta de backup do SQL Server e restaure como um banco NOVO (não sobrescreve o original):',
    ],
    code: "RESTORE FILELISTONLY FROM DISK = N'/var/opt/mssql/backup/arquivo.bak';\nRESTORE DATABASE [Banco_Restore] FROM DISK = N'/var/opt/mssql/backup/arquivo.bak'\n  WITH MOVE N'NomeLogico' TO N'/var/opt/mssql/data/Banco_Restore.mdf',\n       MOVE N'NomeLogico_log' TO N'/var/opt/mssql/data/Banco_Restore_log.ldf';",
  },
  {
    q: 'Erro "pasta compartilhada não existe"',
    a: ['O campo "Mesma pasta no backup manager" está errado. Use /mssql/myseeds ou /mssql/bibliotrack.', 'Se o banco for novo, rode de novo o setup na VPS: sudo ./scripts/setup-vps.sh'],
  },
  {
    q: 'Erro "Login falhou" / "Não foi possível conectar"',
    a: ['Login falhou: usuário ou senha do banco errados.', 'Não foi possível conectar: o IP / host está errado ou o container do banco está parado (docker ps na VPS).'],
  },
  {
    q: 'Erro "Token do Google Drive expirado ou revogado"',
    a: ['Configurações → Google Drive → Reconectar e repita a autorização.'],
  },
  {
    q: 'O backup interfere no sistema que está rodando?',
    a: ['Não. O SQL Server faz o backup online (COPY_ONLY), sem parar o banco nem bloquear usuários. Mesmo assim, prefira horários de madrugada.'],
  },
];

const SECTIONS = [
  {
    title: 'Bancos (cadastro)',
    keys: ['Nome (identificação)', 'Tipo de banco', 'IP / host', 'Porta', 'Nome do banco',
      'Copiar TODOS os bancos deste servidor (novos bancos entram automaticamente)', 'Usuário', 'Senha', 'Pasta no Drive',
      'Pasta de backup no SQL Server', 'Mesma pasta no backup manager', 'Verificar o backup (RESTORE VERIFYONLY)',
      'Compressão nativa do SQL Server (não disponível na edição Express)', 'SSL/TLS', 'Timeout do backup (min)',
      'Ignorar bancos', 'Ferramenta de dump', 'Banco de manutenção'],
  },
  {
    title: 'Agendamentos',
    keys: ['Nome', 'Frequência', 'Dias', 'Horários (HH:mm, separados por vírgula)', 'Intervalo em minutos (mín. 15)', 'Alvo', 'Conexão', 'Banco'],
  },
  {
    title: 'Configurações',
    keys: ['Nome amigável', 'IP público (informativo)', 'Timezone', 'Espaço mínimo livre (MB)', 'Gmail', 'Endereço copiado',
      'Pasta principal', 'Subpasta da VPS (padrão)', 'Nome interno do remote (rclone)', 'Compressão', 'Nível',
      'Chaves públicas age (destinatários)', 'Política', 'Retenção — dias', 'Quantidade por banco', 'Diários', 'Semanais', 'Mensais',
      'Manter por (dias)', 'URL', 'Segredo HMAC', 'Tentativas de upload', 'Espera inicial entre tentativas (s)', 'Espera máxima (s)',
      'Timeout padrão do dump (min)', 'Espera por backup em andamento (min)', 'Janela de recuperação (h)', 'Senha atual', 'Nova senha'],
  },
];

if (await initPage()) {
  const app = $('#app');
  const search = h('input', { type: 'text', placeholder: 'Buscar campo (ex.: senha, pasta, host)…' });
  const fieldsBox = h('div', {});

  const renderFields = () => {
    const q = search.value.trim().toLowerCase();
    clear(fieldsBox, SECTIONS.map((sec) => {
      const items = sec.keys.filter((k) => HELP[k]).filter((k) => {
        if (!q) return true;
        const e = HELP[k];
        return [k, e.text, e.example, e.warn, ...(e.list || [])].join(' ').toLowerCase().includes(q);
      });
      if (items.length === 0) return null;
      return h('div', { class: 'help-section' }, h('h3', { text: sec.title }),
        items.map((k) => h('div', { class: 'help-item', id: 'campo-' + k.replace(/[^A-Za-z0-9]+/g, '-') },
          h('strong', { text: k.replace('Retenção — dias', 'Dias (retenção)') }), ...helpContent(HELP[k]))));
    }));
  };
  search.addEventListener('input', renderFields);

  clear(app,
    h('div', { class: 'page-head' }, h('div', {}, h('h1', { text: 'Ajuda — como usar' }),
      h('div', { class: 'muted', text: 'Em qualquer tela, clique no ⓘ ao lado de um campo para ver o que colocar nele.' }))),

    h('div', { class: 'card' },
      h('h2', { text: 'Como funciona' }),
      h('p', { text: 'O sistema roda dentro da VPS, ao lado dos seus bancos. No horário agendado, ele pede ao banco um backup, compacta, confere o arquivo (checksum), envia ao seu Google Drive e apaga o temporário. Backups antigos são removidos do Drive conforme a política de retenção. Tudo funciona com o seu PC desligado.' }),
      h('pre', { class: 'code', text: 'Banco (myseeds_db) → .bak → compacta (.gz) → confere SHA-256 → Google Drive\n                                         Backups/VPS/MySeeds/2026/10/02/sqlserver/...bak.gz' })),

    h('div', { class: 'card' },
      h('h2', { text: 'Passo a passo' }),
      STEPS.map((s) => h('div', { class: 'help-section' }, h('h3', { text: s.title }),
        h('ol', { class: 'steps' }, s.items.map((i) => h('li', { text: i })))))),

    h('div', { class: 'card' },
      h('h2', { text: 'Perguntas frequentes' }),
      FAQ.map((f) => h('details', {}, h('summary', { text: f.q }),
        f.a.map((p) => h('p', { text: p })), f.code ? h('pre', { class: 'code', text: f.code }) : null))),

    h('div', { class: 'card' },
      h('div', { class: 'row' }, h('h2', { text: 'O que colocar em cada campo' }), h('span', { class: 'spacer' })),
      search, fieldsBox),
  );
  renderFields();
}
