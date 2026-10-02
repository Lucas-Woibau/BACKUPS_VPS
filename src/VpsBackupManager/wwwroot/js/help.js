// Textos de ajuda de cada campo. A chave é o rótulo do campo (ou uma chave explícita passada a field()).
// Formato: { text, list?, example?, warn? } — tudo renderizado como texto (sem HTML).

export const HELP = {
  // =================================================================== Bancos
  'Nome (identificação)': {
    text: 'Nome livre só para você reconhecer este banco no painel, no histórico e na pasta do Google Drive.',
    list: ['Use algo que diga onde está e qual sistema é.', 'Vira parte da pasta no Drive (acentos e espaços são trocados por "_").'],
    example: 'VPS - MySeeds   |   VPS - Bibliotrack   |   VPS - Banco 1',
  },
  'Tipo de banco': {
    text: 'Qual programa de banco de dados está rodando. Define o comando de backup usado.',
    list: [
      'SQL Server: myseeds_db e bibliotrack_db são SQL Server (imagem mcr.microsoft.com/mssql/server).',
      'MySQL / MariaDB / PostgreSQL: para outros projetos.',
    ],
    example: 'SQL Server',
  },
  'IP / host': {
    text: 'Endereço do servidor de banco, visto de DENTRO do backup manager (que roda em Docker na VPS).',
    list: [
      'Banco em container Docker na mesma VPS: use o NOME DO CONTAINER (ex.: myseeds_db). O backup manager já está na mesma rede.',
      'Banco instalado direto na VPS (fora do Docker): use host.docker.internal.',
      'Banco em outro servidor: o IP dele (precisa estar liberado no firewall só para a VPS).',
    ],
    example: 'myseeds_db',
    warn: 'Não use "localhost": dentro do container, localhost é o próprio backup manager, não a VPS.',
  },
  'Porta': {
    text: 'Porta TCP do banco. É preenchida automaticamente pelo tipo escolhido.',
    list: ['SQL Server: 1433', 'MySQL / MariaDB: 3306', 'PostgreSQL: 5432', 'Use a porta INTERNA do container, não a publicada no host.'],
    example: '1433',
  },
  'Nome do banco': {
    text: 'Nome do banco (database) dentro do servidor que será copiado.',
    list: [
      'Não sabe o nome? Preencha IP, usuário e senha e clique em "Testar conexão": a lista de bancos aparece.',
      'Vários bancos do mesmo servidor: separe por vírgula.',
      'Ou marque "Copiar TODOS os bancos deste servidor".',
      'No MySeeds, é o valor de DB_NAME no .env do projeto.',
    ],
    example: 'MySeedsDb   |   loja, crm',
  },
  'Copiar TODOS os bancos deste servidor (novos bancos entram automaticamente)': {
    text: 'Quando marcado, cada backup consulta o servidor e copia todos os bancos que existirem naquele momento.',
    list: ['Bancos criados depois entram sozinhos nos próximos backups.', 'Bancos de sistema (master, msdb, model…) ficam de fora, a menos que você marque em Opções avançadas.', 'Para excluir algum: Opções avançadas → Ignorar bancos.'],
  },
  'Usuário': {
    text: 'Login do BANCO DE DADOS (não é o usuário da VPS nem do painel).',
    list: [
      'SQL Server: "sa" funciona; o ideal é criar um login só para backup (veja a página Ajuda → Usuário de backup).',
      'A senha do sa do MySeeds está no .env do projeto (SA_PASSWORD) ou em: docker exec myseeds_db printenv MSSQL_SA_PASSWORD',
    ],
    example: 'sa   |   backup_user',
  },
  'Senha': {
    text: 'Senha do usuário do banco informado acima.',
    list: ['É guardada criptografada (AES-256) e nunca aparece de novo no painel nem nos logs.', 'Ao editar, deixe vazio para manter a senha atual.'],
    warn: 'Não é a senha da VPS nem do painel.',
  },
  'Pasta no Drive': {
    text: 'Pasta dentro do Google Drive onde os backups deste banco serão guardados. É criada automaticamente no primeiro backup.',
    list: [
      'Fica dentro da pasta principal (Configurações → Pastas no Google Drive, padrão "Backups").',
      'Depois dela o sistema cria sozinho: ano / mês / dia / tipo / nome.',
      'Use "/" para subpastas. Vazio = usa a subpasta padrão da VPS.',
    ],
    example: 'VPS/MySeeds  →  Backups/VPS/MySeeds/2026/10/02/sqlserver/VPS_-_MySeeds/MySeedsDb_2026-10-02_03-00-00.bak.gz',
  },
  'Pasta de backup no SQL Server': {
    text: 'Pasta onde o PRÓPRIO SQL Server grava o arquivo .bak, do ponto de vista do container do SQL Server.',
    list: ['Na sua VPS: /var/opt/mssql/backup (o setup-vps.sh já criou essa pasta em myseeds_db e bibliotrack_db).', 'O arquivo é temporário: o backup manager compacta, envia e apaga.'],
    example: '/var/opt/mssql/backup',
  },
  'Mesma pasta no backup manager': {
    text: 'A MESMA pasta acima, mas vista de dentro do backup manager (ela é compartilhada entre os dois containers).',
    list: ['MySeeds: /mssql/myseeds', 'Bibliotrack: /mssql/bibliotrack', 'Esses caminhos vêm do docker-compose.prod.yml; o setup-vps.sh imprime os valores no final.'],
    example: '/mssql/myseeds',
    warn: 'Se estiver errado, o teste avisa "pasta compartilhada não existe" e o backup falha.',
  },
  'SSL/TLS': {
    text: 'Criptografia da conexão com o banco.',
    list: [
      'Banco na mesma VPS (rede Docker interna): "Criptografar (aceita certificado próprio)" — padrão, funciona com SQL Server em Docker.',
      'Desabilitado: só se o servidor não suportar TLS.',
      'Verificar CA / hostname: quando o banco tem certificado válido (servidores externos).',
    ],
  },
  'Timeout do backup (min)': {
    text: 'Tempo máximo de um backup deste banco. Passando disso, ele é cancelado e marcado como erro.',
    list: ['Vazio = usa o padrão de Configurações (360 min).', 'Bancos pequenos (até alguns GB) levam segundos ou poucos minutos.'],
    example: '60',
  },
  'Ignorar bancos': {
    text: 'Usado junto com "Copiar TODOS": bancos que NÃO devem ser copiados. Separe por vírgula.',
    example: 'teste, homologacao',
  },
  'Ferramenta de dump': {
    text: 'Programa usado para MySQL/MariaDB. Deixe "auto".',
    list: ['auto: usa mariadb-dump (incluído na imagem).', 'mysqldump: só se a imagem foi construída com MYSQL_CLIENT_FLAVOR=mysql.'],
  },
  'Banco de manutenção': {
    text: 'PostgreSQL: banco usado para conectar e listar os outros. Quase sempre "postgres".',
    example: 'postgres',
  },
  'Verificar o backup (RESTORE VERIFYONLY)': {
    text: 'Depois de gerar o .bak, o SQL Server confere se o arquivo é legível e se os checksums das páginas batem.',
    list: ['Recomendado deixar marcado.', 'Se o usuário não tiver permissão CREATE DATABASE, a verificação é pulada com aviso (o backup continua).'],
  },
  'Compressão nativa do SQL Server (não disponível na edição Express)': {
    text: 'Pede ao SQL Server para compactar o .bak. Normalmente desnecessário: o sistema já compacta com gzip.',
    warn: 'Na edição Express o backup FALHA com essa opção marcada.',
  },

  // =================================================================== Agendamentos
  'Nome': { text: 'Nome da agenda, para identificar no painel.', example: 'Diário 03:00' },
  'Frequência': {
    text: 'Como a agenda se repete.',
    list: ['Dias da semana + horários: ex. todos os dias às 03:00, ou seg/qua/sex às 02:30.', 'Intervalo fixo: a cada N minutos (mínimo 15).'],
  },
  'Dias': {
    text: 'Dias da semana em que o backup roda. Use os atalhos (Todos os dias, Dias úteis…) ou marque manualmente.',
  },
  'Horários (HH:mm, separados por vírgula)': {
    text: 'Horário(s) no fuso configurado (America/Sao_Paulo). Formato 24h.',
    list: ['Prefira horários de pouco uso (madrugada).', 'Vários horários no mesmo dia: separe por vírgula.', 'Use "Prévia" para conferir as próximas execuções.'],
    example: '03:00   |   03:00, 15:00',
  },
  'Intervalo em minutos (mín. 15)': { text: 'Tempo entre um backup e o próximo.', example: '360  (= a cada 6 horas)' },
  'Alvo': {
    text: 'O que esta agenda copia.',
    list: ['Todas as conexões: todos os bancos ativos (recomendado — uma agenda só).', 'Uma conexão: só um servidor.', 'Um banco específico: um único banco.'],
  },
  'Conexão': { text: 'Qual banco cadastrado (da página Bancos) esta agenda copia.' },
  'Banco': { text: 'Nome exato do banco dentro da conexão escolhida.', example: 'MySeedsDb' },

  // =================================================================== Configurações
  'Nome amigável': { text: 'Nome desta VPS no painel e nos alertas (webhook).', example: 'VPS-Producao' },
  'Hostname': { text: 'Nome da máquina, só informativo (não editável).' },
  'IP público (informativo)': {
    text: 'Só para registro. Não é usado para conectar aos bancos (eles são acessados pela rede interna do Docker).',
    example: '168.231.92.5',
  },
  'Timezone': {
    text: 'Fuso horário usado nos agendamentos, nos nomes dos arquivos, no dashboard e no histórico.',
    example: 'America/Sao_Paulo',
  },
  'Diretório temporário': { text: 'Onde os arquivos ficam enquanto são compactados e enviados (definido no .env). Limpo após cada backup.' },
  'Espaço mínimo livre (MB)': {
    text: 'Se o disco da VPS tiver menos que isso livre, o backup é cancelado (para não lotar o disco e derrubar os outros sistemas).',
    example: '2048  (= 2 GB)',
  },
  'Gmail': {
    text: 'Conta Google onde os backups serão guardados.',
    list: [
      '1. Digite o Gmail e clique em Conectar Google Drive.',
      '2. Na aba do Google, entre na conta e clique em Permitir.',
      '3. O navegador vai para um endereço http://127.0.0.1:53682/... que NÃO abre. Isso é esperado.',
      '4. Copie esse endereço inteiro e cole no campo "Endereço copiado" → Concluir conexão.',
      'Só é feito uma vez. Depois funciona sozinho, com o PC desligado.',
    ],
    warn: 'A senha do Gmail é digitada apenas na página do Google, nunca no painel.',
  },
  'Endereço copiado': {
    text: 'Cole o endereço completo da barra do navegador depois de clicar em Permitir no Google.',
    example: 'http://127.0.0.1:53682/?state=AbC123...&code=4/0Ab...&scope=https://www.googleapis.com/auth/drive.file',
    warn: 'O endereço expira em ~10 minutos. Se demorar, clique em Conectar de novo.',
  },
  'Pasta principal': { text: 'Pasta raiz no Google Drive. Tudo fica dentro dela. Criada automaticamente.', example: 'Backups' },
  'Subpasta da VPS (padrão)': {
    text: 'Subpasta usada quando o banco não tem "Pasta no Drive" própria. Útil se você tiver mais de uma VPS.',
    example: 'VPS-Producao',
  },
  'Nome interno do remote (rclone)': { text: 'Nome técnico da conexão com o Drive. Não precisa mudar.', example: 'gdrive' },
  'Compressão': {
    text: 'Como os arquivos são compactados antes do envio.',
    list: ['gzip (padrão): abre em qualquer computador.', 'zstd: mais rápido e menor, mas precisa do programa zstd para restaurar.', 'Sem compressão: arquivos maiores.'],
  },
  'Nível': { text: 'Quanto compactar. Maior = arquivo menor, porém mais lento.', example: 'gzip: 6   |   zstd: 3' },
  'Chaves públicas age (destinatários)': {
    text: 'Opcional. Criptografa os arquivos antes de enviar ao Drive — nem o Google consegue ler.',
    list: ['No SEU PC: age-keygen -o age-key.txt', 'Cole aqui só a linha "public key: age1..." (a chave pública).', 'Guarde o arquivo age-key.txt (chave privada) em 2 lugares seguros fora da VPS.'],
    example: 'age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p',
    warn: 'Sem a chave privada, os backups criptografados NÃO podem ser restaurados.',
  },
  'Política': {
    text: 'Quando apagar backups antigos do Google Drive.',
    list: [
      'Manter por N dias: apaga o que tiver mais de N dias.',
      'Manter os últimos N: guarda só os N mais recentes de cada banco.',
      'GFS: guarda X diários, Y semanais e Z mensais (melhor custo-benefício).',
      'Nunca apagar: o Drive vai encher com o tempo.',
      'O backup mais recente de cada banco nunca é apagado.',
    ],
  },
  'Retenção — dias': { text: 'Quantos dias manter os backups no Drive.', example: '30' },
  'Quantidade por banco': { text: 'Quantos backups manter de cada banco.', example: '30' },
  'Diários': { text: 'GFS: quantos dias recentes manter (1 backup por dia).', example: '7' },
  'Semanais': { text: 'GFS: quantas semanas manter (1 backup por semana).', example: '4' },
  'Mensais': { text: 'GFS: quantos meses manter (1 backup por mês).', example: '12' },
  'Manter por (dias)': { text: 'Por quantos dias guardar uma cópia local na VPS além do Drive (só se "Manter cópia local" estiver marcado).', example: '3' },
  'URL': {
    text: 'Endereço que recebe um aviso (POST JSON) quando um backup termina ou falha.',
    list: ['Pode ser n8n, Make, Zapier, Discord (webhook de canal), Slack etc.', 'Use "Salvar e enviar teste" para conferir.'],
    example: 'https://discord.com/api/webhooks/...',
  },
  'Segredo HMAC': { text: 'Opcional. Se preenchido, cada aviso vem assinado no cabeçalho X-VBM-Signature para o destino validar a origem.' },
  'Tentativas de upload': { text: 'Quantas vezes tentar enviar ao Drive se a Internet ou o Google falharem.', example: '5' },
  'Espera inicial entre tentativas (s)': { text: 'Espera antes da 2ª tentativa. Dobra a cada nova falha.', example: '30' },
  'Espera máxima (s)': { text: 'Limite da espera entre tentativas.', example: '600' },
  'Timeout padrão do dump (min)': { text: 'Tempo máximo de cada backup, quando o banco não define o próprio.', example: '360' },
  'Espera por backup em andamento (min)': { text: 'Se um backup agendado encontrar outro rodando, aguarda até este tempo antes de desistir.', example: '120' },
  'Janela de recuperação (h)': {
    text: 'Se a VPS estava desligada no horário agendado, o backup roda quando ela voltar — desde que dentro desta janela.',
    example: '6',
  },
  'Senha atual': { text: 'Sua senha de login do painel.' },
  'Nova senha': { text: 'Mínimo 12 caracteres, com pelo menos 3 tipos: minúsculas, maiúsculas, números e símbolos. Outras sessões abertas serão encerradas.' },
};
