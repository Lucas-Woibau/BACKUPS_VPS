# Fase 5 — Revisão de segurança

| Área | Medidas implementadas | Onde |
|---|---|---|
| **Autenticação** | Argon2id (64 MiB, t=3, p=2); senha ≥ 12 caracteres e 3 classes; resposta de login com tempo constante para usuário inexistente; limite de 5 falhas / 15 min por IP e por usuário (HTTP 429). | `Security/PasswordHasher.cs`, `Api/AuthEndpoints.cs`, `LoginThrottle` |
| **Primeiro acesso** | Sem credencial padrão. Token aleatório em `data/setup_token` (0600), nunca logado; necessário para criar o 1º admin; apagado após uso; `/api/auth/setup` recusa se já existe admin. | `SetupTokenService.cs` |
| **Sessão** | Token de 256 bits em cookie `HttpOnly`, `SameSite=Strict`, `Secure` (com `SESSION_COOKIE_SECURE=true`); só o SHA-256 é salvo; expiração absoluta (12 h) e por inatividade (60 min); logout e troca de senha revogam sessões. | `SessionService.cs` |
| **CSRF** | Toda requisição mutável exige `X-Requested-With: vbm` (impossível em form cross-site sem preflight CORS, e CORS não é habilitado) **e**, com sessão, `X-CSRF-Token` vinculado à sessão (comparação em tempo constante). | `Api/Middleware.cs` |
| **XSS** | Frontend monta DOM com `textContent`/`createElement` (nunca `innerHTML` com dados). CSP: `default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'`. Também `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`. | `wwwroot/js/core.js`, `SecurityHeadersMiddleware` |
| **Command injection** | Nenhum shell. Processos via `ProcessStartInfo.ArgumentList` (argv). Nomes de banco/host validados (sem `-` inicial, sem controle) antes de chegar ao argv; nomes de bancos no PG vão por `PGDATABASE` (env), não por argumento. Opções de conexão por allow-list (sem "argumentos extras" livres). | `ProcessRunner.cs`, `ProviderHelpers`, `ConnectionService` |
| **SQL injection** | SQLite somente com parâmetros Dapper. Consultas aos bancos monitorados são fixas (`SHOW DATABASES`, `pg_database`). | `Data/`, providers |
| **Path traversal** | Pasta remota e subpasta validadas por segmento (sem `..`, `:`, `\`, `/` inicial). Exclusões locais: caminho resolvido estritamente dentro da raiz, sem symlink, nome no padrão `run_<32hex>` ou de artefato. Exclusões remotas: estrutura datada + regex do nome do artefato + prefixo do destino atual; só `rclone deletefile` (nunca `purge`/`sync`). Certificados SSL só dentro de `data/certs`. | `PathGuard.cs`, `RcloneStorage.cs` |
| **Segredos** | Senhas dos bancos cifradas com AES-256-GCM (HKDF de `APP_SECRET_KEY`); senha nunca retornada pela API (`hasPassword`); passada ao dump via arquivo 0600 temporário (`--defaults-extra-file`/`PGPASSFILE`) apagado logo após — nunca em argv (visível em `ps`) nem em env. Processos filhos recebem ambiente mínimo (sem `APP_SECRET_KEY`). `.env` 0600, suporte a `APP_SECRET_KEY_FILE` (Docker secrets). `.gitignore` cobre `.env`, `data/`, `rclone/`, chaves. | `SecretBox.cs`, providers, `ProcessRunner.cs` |
| **Logs** | JSON estruturado (Serilog compact), rotação (arquivo 20 MB × 30; Docker 10 MB × 5). `Redactor` mascara `password=`, `token:`, URIs com credenciais, tokens OAuth em JSON e todas as senhas de conexão decriptadas (registradas como literais). stderr de ferramentas é redigido e truncado. | `Redactor.cs`, `Program.cs` |
| **Docker** | Usuário não-root (UID 1654); `read_only: true` + tmpfs `/tmp`; `cap_drop: ALL`; `no-new-privileges`; sem `docker.sock` no app (proxy opcional só-leitura em profile separado); porta publicada só em `127.0.0.1`. | `Dockerfile`, `docker-compose.yml` |
| **Rede** | Painel não exposto por padrão. Bancos não precisam ser publicados na Internet: rede Docker compartilhada ou bind na bridge + UFW restrito a `172.16.0.0/12`. Descoberta não varre redes. | `docs/04-bancos.md` |
| **Reverse proxy** | Exemplos Nginx/Caddy com HTTPS, HSTS, rate-limit de login, allow-list opcional; `TRUST_PROXY_HEADERS` com redes confiáveis explícitas. | `deploy/` |
| **Criptografia de backup** | age (padrão aberto) com chave pública; VPS comprometida não consegue ler backups antigos no Drive. | `AgeEncryptor.cs` |
| **Google Drive** | Escopo `drive.file` recomendado; client OAuth próprio publicado (evita expiração de 7 dias). | `docs/03-google-drive.md` |
| **Corpo de requisição** | Limite de 1 MB no Kestrel; JSON inválido → 400 sem stack trace; erros 500 genéricos (detalhe só no log). | `Program.cs`, `ErrorHandlingMiddleware` |

## Riscos residuais e recomendações

- **Quem tem root na VPS tem tudo** (inclusive `.env` e `rclone.conf`). Mitigue com SSH por chave, `PermitRootLogin prohibit-password`, fail2ban, atualizações automáticas (`unattended-upgrades`).
- **Ransomware/invasor apagando o Drive**: o token tem permissão de exclusão. Mitigações: conta Google dedicada, escopo `drive.file`, e uma segunda cópia (ex.: remote S3/B2 com Object Lock) ou cópia periódica manual do Drive.
- **Exposição pública do painel**: se precisar, use HTTPS + allow-list de IP ou Tailscale. Não há 2FA na v1.
- **`APP_SECRET_KEY`**: guarde uma cópia offline (ou use `scripts/export-app-config.sh`).
