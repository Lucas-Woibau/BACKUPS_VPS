# Fase 1 — Arquitetura

## Visão geral

```
                      VPS Hostinger (Ubuntu/Debian)
 ┌─────────────────────────────────────────────────────────────────────────────┐
 │  docker compose (restart: unless-stopped)                                   │
 │  ┌───────────────────────────── vps-backup-manager ───────────────────────┐ │
 │  │ ASP.NET Core (.NET 10)                                                 │ │
 │  │  ├─ Painel web (HTML + JS puro)  ◄── 127.0.0.1:8080 (túnel SSH/VPN/    │ │
 │  │  ├─ API REST /api/*                   proxy HTTPS)                     │ │
 │  │  ├─ SchedulerService (BackgroundService, tick 20 s)                    │ │
 │  │  ├─ BackupOrchestrator ──► IDatabaseBackupProvider (MySQL/MariaDB/PG)  │ │
 │  │  │        │                   └─ mariadb-dump / pg_dump (argv, sem shell)│
 │  │  │        ├─► Compression (gzip/zstd, streaming) ─► verify ─► SHA-256  │ │
 │  │  │        ├─► age (opcional, chave pública)                            │ │
 │  │  │        └─► IStorageProvider ─► rclone ─► Google Drive               │ │
 │  │  ├─ Retenção (só arquivos criados pelo app, caminho validado)          │ │
 │  │  └─ Webhook (INotifier)                                                │ │
 │  │ Volumes: ./data (SQLite, logs) ./backups (temp) ./rclone (token)       │ │
 │  └────────────────────────────────────────────────────────────────────────┘ │
 │        │ host.docker.internal            │ redes Docker externas            │
 │   MySQL/PG no host                 containers MySQL/PG de outros projetos   │
 └─────────────────────────────────────────────────────────────────────────────┘
                                   │ HTTPS (rclone)
                                   ▼
                     Google Drive: Backups/VPS-Producao/2026/10/01/mysql/...
```

Tudo roda na VPS. O PC do administrador só é usado uma vez, para autorizar o Google Drive
(`rclone authorize`) — depois pode ficar desligado.

## Tecnologias e decisões

| Decisão | Escolha | Motivo |
|---|---|---|
| Backend | **C# / ASP.NET Core .NET 10 (LTS)**, minimal APIs | Linguagem que o administrador já domina; runtime LTS até 2028; um único processo leve. |
| Frontend | HTML + CSS + **JavaScript puro** (ES modules) | Sem build, sem framework; CSP estrita (`script-src 'self'`). |
| Banco interno | **SQLite** (WAL) + Dapper + migrations SQL versionadas embutidas | Zero manutenção; só configuração e histórico (nunca dumps). |
| Agendamento | **BackgroundService próprio** lendo agendas do SQLite | Fonte única de verdade editável no painel; sobrevive a reinícios; recupera execuções perdidas (VPS desligada às 03:00) dentro de uma janela configurável. cron/systemd exigiriam acesso ao host e sincronização de duas configs. |
| Dump | Ferramentas nativas: `mariadb-dump`/`mysqldump`, `pg_dump -Fc`, `pg_dumpall --globals-only` | Formatos padrão, restauráveis sem este sistema. |
| Pipeline | stdout do dump → compressor (stream) → arquivo | O dump bruto nunca ocupa o disco; metade do espaço necessário. |
| Compressão | **gzip padrão**, zstd opcional | gzip existe em qualquer máquina de restauração; zstd para bases grandes. |
| Verificação | exit code, tamanho > 0, marcador de fim (`-- Dump completed` / cabeçalho `PGDMP`), **descompressão completa comparando SHA-256 com o stream original**, SHA-256 final, tamanho+MD5 remoto pós-upload | "Arquivo criado" não basta. |
| Envio | **rclone** CLI (`copyto`, uploads em chunks, retries) | Robusto em headless, renova token sozinho, suporta 70+ backends (S3, B2, OneDrive, Dropbox, MinIO) sem novo código. |
| Criptografia | **age** com chave pública (X25519 + ChaCha20-Poly1305) | A VPS só cifra; a chave privada fica offline. Arquivo por arquivo, restaurável com uma linha. Sem algoritmo próprio. |
| Segredos | AES-256-GCM com chave derivada (HKDF) de `APP_SECRET_KEY` | Senhas de banco nunca em texto puro no SQLite nem em argv/logs. |
| Senha admin | **Argon2id** (64 MiB, t=3, p=2) | Padrão OWASP. |
| Sessão | Token aleatório em cookie HttpOnly + SameSite=Strict (+Secure com HTTPS); só o hash SHA-256 fica no banco; expiração absoluta e por inatividade | Revogável (logout/troca de senha). |
| CSRF | Cabeçalho `X-Requested-With: vbm` obrigatório em toda mutação + token CSRF por sessão | Formulários cross-site não conseguem enviar cabeçalhos customizados. |
| Concorrência | Lock global: `SemaphoreSlim` + lock de arquivo exclusivo (flock) | Impede backups duplicados mesmo com 2 processos. Manual → HTTP 409; agendado → aguarda até N min. |

## Fluxo de um backup

1. Validar configuração (remote, caminhos, chaves age).
2. Para cada conexão: descriptografar senha, **testar conexão**, listar bancos (inclusão automática de bancos novos quando "todos" está marcado).
3. Criar diretório exclusivo `/backups/work/run_<id>` (0700).
4. Para cada banco (falha de um não interrompe os demais):
   1. Checar espaço livre mínimo.
   2. Gerar nome `banco_AAAA-MM-DD_HH-mm-ss.sql.gz` (timezone configurada); se já existir no destino, sufixo único.
   3. Credenciais em arquivo 0600 temporário (`--defaults-extra-file` / `PGPASSFILE`), apagado logo após.
   4. Dump → compressor em streaming, com timeout.
   5. Validar exit code, tamanho, marcador de fim/cabeçalho.
   6. Descomprimir tudo e comparar SHA-256/tamanho com o stream original.
   7. Criptografar com age (opcional).
   8. SHA-256 do arquivo final + arquivo `.sha256` ao lado.
   9. Upload com retries (backoff exponencial + jitter, limite configurável) e verificação de tamanho + MD5 no Drive.
   10. Registrar tamanhos, checksum, destino, tentativas, avisos/erros.
   11. Apagar temporários (ou mover para cópia local por X dias).
5. Retenção por banco (dias / quantidade / GFS) — nunca apaga o backup mais recente; só apaga arquivos com nome e caminho no padrão do app.
6. Limpeza de cópias locais vencidas, resumo da execução, webhook.

Reinício no meio de um backup: na subida, execuções `running` são marcadas como **erro (interrompido)** e diretórios `run_*` são removidos com validação.

## Extensibilidade

- **Novos bancos**: implementar `IDatabaseBackupProvider` (`TestConnectionAsync`, `ListDatabasesAsync`, `BuildDumpCommand`, `ValidateBackup`, `CreateBackupAsync` padrão) e registrar em `ProviderRegistry`. Ex.: MongoDB (`mongodump --archive`), Redis (`redis-cli --rdb`), SQL Server (`sqlpackage`/`BACKUP DATABASE`), SQLite (`sqlite3 .backup`).
- **Novos destinos**: `IStorageProvider` (`TestConnectionAsync`, `UploadAsync`, `DeleteAsync`, `ExistsAsync`, `ListBackupsAsync`). Com rclone, S3/B2/OneDrive/Dropbox/MinIO já funcionam trocando o remote.
- **Novos alertas**: `INotifier` (e-mail, Telegram, Discord). Webhook genérico já implementado.
- **Teste periódico de restauração (futuro)**: `IRestoreVerifier` executado por agenda própria: baixa o último backup, sobe um container efêmero do mesmo engine numa rede isolada, restaura, roda consultas de sanidade (contagem de tabelas/linhas) e registra o resultado no histórico. Exige um executor com permissão para criar containers — deve rodar como serviço separado, não no painel.

## Premissas adotadas

- VPS Ubuntu 22.04/24.04 ou Debian 12, x86_64 ou arm64, com Docker Engine + Compose v2.
- Painel acessado por túnel SSH/VPN por padrão (escuta só em 127.0.0.1). Domínio é opcional.
- Um único administrador é o caso comum (o modelo suporta vários usuários).
- Cliente PostgreSQL 18 na imagem (compatível com servidores 9.2 a 18). Cliente MySQL padrão: `mariadb-dump` (troque com `MYSQL_CLIENT_FLAVOR=mysql`).
- Cada banco gera um arquivo próprio; a pasta remota inclui o nome da conexão para não misturar bancos homônimos de servidores diferentes.
- Retenção usa o histórico do app (só apaga o que ele próprio enviou e registrou).
- Restauração é manual (documentada) — nada destrutivo é exposto no painel na v1.

---

# Fase 2 — Estrutura do projeto

```
VpsBackups/
├── Dockerfile                         # build multi-stage + clientes de banco + rclone + age
├── docker-compose.yml                 # app + docker-proxy opcional (profile discovery)
├── docker-compose.override.example.yml# redes externas / network_mode host
├── .env.example  .gitignore  .dockerignore  .gitattributes
├── VpsBackupManager.slnx
├── README.md
├── deploy/
│   ├── nginx/vps-backup-manager.conf  # reverse proxy HTTPS (certbot)
│   └── caddy/Caddyfile                # reverse proxy HTTPS automático
├── docs/                              # fases 1–11
├── scripts/
│   ├── install.sh                     # instala Docker, cria .env, permissões, build, up
│   ├── detect-env.sh                  # diagnóstico somente-leitura (SO, Docker, bancos, portas)
│   ├── show-setup-token.sh            # token de primeiro acesso
│   ├── rclone-config.sh               # rclone config/test/reconnect dentro do container
│   ├── fetch-backup.sh                # baixa backup + confere SHA-256 (sem restaurar)
│   ├── export-app-config.sh           # exporta config do app criptografada (DR)
│   └── dev-run.ps1                    # execução local no Windows (desenvolvimento)
├── src/VpsBackupManager/
│   ├── Program.cs                     # DI, Serilog, middlewares, startup (migrations, recovery)
│   ├── Config/AppOptions.cs           # variáveis de ambiente / Docker secrets
│   ├── Data/
│   │   ├── Db.cs                      # SQLite + migration runner + Clock
│   │   ├── Models.cs                  # linhas (users, connections, schedules, runs, backups, events)
│   │   └── Migrations/0001_initial.sql
│   ├── Security/
│   │   ├── SecretBox.cs               # AES-256-GCM p/ credenciais
│   │   ├── PasswordHasher.cs          # Argon2id
│   │   ├── SessionService.cs          # sessões, CSRF, LoginThrottle
│   │   ├── SetupTokenService.cs       # primeiro acesso
│   │   └── Redactor.cs                # remove segredos de logs/mensagens
│   ├── Providers/
│   │   ├── IDatabaseBackupProvider.cs # contrato + DumpCommand + helpers
│   │   ├── MySqlProvider.cs           # MySQL e MariaDB
│   │   ├── PostgreSqlProvider.cs      # pg_dump / pg_dumpall
│   │   ├── ProcessDumpExecutor.cs     # streaming stdout→compressor, hash, timeout
│   │   └── ProviderRegistry.cs
│   ├── Storage/
│   │   ├── IStorageProvider.cs
│   │   ├── RcloneStorage.cs           # Google Drive via rclone
│   │   └── LocalFolderStorage.cs      # testes / disco montado
│   ├── Backup/
│   │   ├── BackupOrchestrator.cs      # fluxo completo, retries, retenção, recovery
│   │   ├── BackupNaming.cs  PathGuard.cs  Compression.cs  AgeEncryptor.cs
│   │   ├── RetentionPolicy.cs         # dias / quantidade / GFS (função pura)
│   │   └── BackupLock.cs
│   ├── Scheduling/
│   │   ├── ScheduleCalculator.cs      # próximas execuções (timezone, DST)
│   │   ├── ScheduleService.cs         # CRUD
│   │   └── SchedulerService.cs        # BackgroundService + manutenção
│   ├── Notifications/Notifiers.cs     # INotifier + webhook HMAC
│   ├── Services/                      # Settings, Connections, Dashboard, Discovery, EventLog, ProcessRunner
│   ├── Api/                           # Middleware (headers, auth gate, erros), AuthEndpoints, ApiEndpoints
│   └── wwwroot/                       # index/connections/schedules/backups/settings/logs/login/setup .html
│       ├── css/app.css
│       └── js/ core.js dashboard.js connections.js schedules.js backups.js settings.js logs.js runs.js login.js setup.js
└── tests/VpsBackupManager.Tests/      # xUnit: nomes, caminhos, retenção, agenda, segurança, providers,
                                       # compressão, orquestrador ponta a ponta (fakes), API (auth/CSRF)
```
