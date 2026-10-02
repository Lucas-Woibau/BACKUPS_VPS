# VPS Backup Manager

Sistema de backup automático para bancos **SQL Server, MySQL, MariaDB e PostgreSQL** de uma VPS (Hostinger ou qualquer
Linux com Docker), com envio para uma pasta configurável do **Google Drive**. Roda 100% na VPS, inicia sozinho
após reboot e é administrado por um painel web protegido.

**Stack:** C# / ASP.NET Core (.NET 10 LTS) · JavaScript puro · SQLite · rclone · age · Docker Compose.

## Recursos

- Cadastro simples por banco ("VPS - Banco 1"): IP, porta, nome do banco, usuário, senha e pasta no Drive
- Google Drive conectado só com o Gmail (um clique em "Permitir" na primeira vez)
- Conexões por servidor, listagem dos bancos, "todos os bancos (inclui novos automaticamente)" ou seleção
- Backups nativos (SQL Server `BACKUP DATABASE ... COPY_ONLY, CHECKSUM` + `RESTORE VERIFYONLY`, `mariadb-dump`/`mysqldump`, `pg_dump -Fc`, `pg_dumpall --globals-only`) em streaming
- Compressão gzip (padrão) ou zstd, verificação por descompressão, SHA-256 + arquivo `.sha256`
- Criptografia opcional com **age** (chave pública na VPS, privada offline)
- Upload via rclone com retries (backoff exponencial) e conferência de tamanho + MD5 no Drive
- Estrutura `Backups/<VPS>/AAAA/MM/DD/<tipo>/<conexão>/<banco>_AAAA-MM-DD_HH-mm-ss.sql.gz`
- Agendas por dias da semana + vários horários ou por intervalo, timezone configurável, recuperação de execuções perdidas
- Backup manual (tudo / uma conexão / um banco) com lock contra execuções simultâneas
- Retenção por dias, por quantidade ou GFS (diário/semanal/mensal) com proteções fortes de exclusão
- Dashboard com **último backup bem-sucedido**, falhas consecutivas, disco, status do Drive
- Histórico detalhado, eventos por execução, logs JSON com rotação e sem segredos
- Webhook (HMAC) para sucesso/erro; arquitetura para e-mail/Telegram/Discord
- Login Argon2id, sessões revogáveis, CSRF, CSP estrita, primeiro acesso por token

## Instalação rápida

```bash
git clone <seu-repo> /opt/vps-backup-manager && cd /opt/vps-backup-manager
chmod +x scripts/*.sh
sudo ./scripts/install.sh              # Docker, .env com chave aleatória, permissões, build, up
sudo ./scripts/show-setup-token.sh     # token do primeiro acesso
# no seu PC:
ssh -L 8080:127.0.0.1:8080 root@IP_DA_VPS     # abra http://localhost:8080
# Configurações → Google Drive → informe o Gmail → Conectar (ver docs/03)
```

Depois: Configurações → Google Drive → **Salvar e testar upload**; Conexões → **Nova conexão**;
Agendamentos → **Nova agenda**; Dashboard → **Executar backup agora**.

## Documentação (fases)

| Fase | Documento |
|---|---|
| 1–2 Arquitetura e estrutura | [docs/01-arquitetura.md](docs/01-arquitetura.md) |
| 3 Implementação | código em [src/VpsBackupManager](src/VpsBackupManager) · testes em [tests](tests/VpsBackupManager.Tests) |
| 4 Docker | [Dockerfile](Dockerfile) · [docker-compose.yml](docker-compose.yml) · [override de redes](docker-compose.override.example.yml) |
| 5 Segurança | [docs/07-seguranca.md](docs/07-seguranca.md) |
| 6 Instalação na Hostinger | [docs/02-instalacao-hostinger.md](docs/02-instalacao-hostinger.md) |
| 7 Google Drive | [docs/03-google-drive.md](docs/03-google-drive.md) |
| 8 Bancos (host/Docker, usuários mínimos) | [docs/04-bancos.md](docs/04-bancos.md) |
| 9 Teste inicial | [docs/05-teste-inicial.md](docs/05-teste-inicial.md) |
| 10 Restore | [docs/06-restore.md](docs/06-restore.md) |
| 11 Produção | [docs/08-producao.md](docs/08-producao.md) |
| Deploy GitHub Actions + GHCR | [docs/09-deploy-github.md](docs/09-deploy-github.md) |

## API REST

Todas exigem sessão, exceto `GET /api/health`, `GET /api/auth/status`, `POST /api/auth/login|setup`.
Mutações exigem `X-Requested-With: vbm` e `X-CSRF-Token`.

```
GET  /api/health                         GET  /api/dashboard           GET /api/system
GET  /api/connections                    POST /api/connections         PUT/DELETE /api/connections/{id}
POST /api/connections/test               POST /api/connections/{id}/test
GET  /api/connections/{id}/databases     GET  /api/discovery
GET  /api/schedules                      POST /api/schedules           PUT/DELETE /api/schedules/{id}
POST /api/schedules/preview
POST /api/backups/run                    GET  /api/backups[?status&connectionId&q&limit&offset]
GET  /api/backups/{id}                   GET  /api/runs  /api/runs/{id}  /api/runs/current
GET  /api/settings                       PUT  /api/settings
POST /api/settings/storage/test          POST /api/settings/webhook/test  POST /api/settings/webhook-secret
GET  /api/logs?lines=200                 GET  /api/events?level=error
POST /api/auth/logout                    POST /api/auth/change-password
```

## Desenvolvimento

```bash
dotnet test                                   # 75 testes (unidade + orquestrador ponta a ponta + API)
pwsh scripts/dev-run.ps1                      # Windows: app em http://127.0.0.1:8080 com dados em .dev/
```

## Operação

```bash
docker compose ps                     # status/health
docker compose logs -f app            # logs
docker compose up -d --build          # atualizar após git pull
./scripts/fetch-backup.sh "<destino>" # baixar + validar SHA-256 de um backup
```
