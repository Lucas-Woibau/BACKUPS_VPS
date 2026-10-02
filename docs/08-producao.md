# Fase 11 — Checklist de produção

Só considere pronto quando **todos** os itens estiverem marcados.

## Segurança

- [ ] SSH somente por chave; `PermitRootLogin prohibit-password` ou usuário sudo dedicado
- [ ] UFW ativo: `deny incoming`, liberado só SSH (e 80/443 se usar proxy)
- [ ] `ss -ltnp` não mostra 8080, 3306 ou 5432 em `0.0.0.0` sem regra de firewall
- [ ] `.env` com `chmod 600`, `APP_SECRET_KEY` aleatória ≥ 32 caracteres, cópia offline guardada
- [ ] `rclone/rclone.conf` com `chmod 600`, escopo `drive.file`, app OAuth **publicado** (não "Teste")
- [ ] Senha do admin forte e única; token de setup consumido (`show-setup-token.sh` diz "nenhum token")
- [ ] Usuários de banco dedicados, só leitura (Fase 8) — nenhum `root`/`postgres` no painel
- [ ] Se acesso por domínio: HTTPS ativo, `SESSION_COOKIE_SECURE=true`, `TRUST_PROXY_HEADERS=true`, allow-list ou VPN
- [ ] Nenhum `docker-socket-proxy`/`docker.sock` acessível pelo app (descoberta Docker removida)
- [ ] `git status` sem `.env`, `data/`, `rclone/` (verifique o `.gitignore`)
- [ ] `unattended-upgrades` ou rotina de atualização da VPS

## Confiabilidade

- [ ] Todas as conexões testadas com sucesso; todos os bancos esperados aparecem como incluídos
- [ ] Ao menos uma agenda ativa; "Próximo backup" correto no dashboard e na timezone certa
- [ ] Execução agendada real concluída com **Sucesso** (não só manual)
- [ ] Reboot testado: app volta `healthy`, agendas preservadas
- [ ] Espaço livre ≥ 2× o maior banco compactado + margem; "Espaço mínimo livre" configurado
- [ ] Timeout de dump adequado ao maior banco
- [ ] Retenção definida e compatível com a cota do Drive (ex.: GFS 7/4/12)
- [ ] Janela de recuperação (catch-up) e espera de lock adequadas ao horário/volume
- [ ] Webhook configurado para **erro** (Discord/Slack/Telegram via integração, n8n, etc.) e teste recebido

## Restauração (sem isso não há backup)

- [ ] Restore de teste **completo** de pelo menos um banco MySQL/MariaDB e um PostgreSQL (Fase 10)
- [ ] Se usa age: restore testado com a chave privada, que está guardada em **2 locais** fora da VPS
- [ ] Procedimento de restore impresso/salvo fora da VPS
- [ ] Export da configuração do app (`scripts/export-app-config.sh`) guardado fora da VPS
- [ ] Teste de restore agendado no calendário (mensal)

## Observabilidade

- [ ] Dashboard: "Último backup bem-sucedido" recente (< 24–48 h)
- [ ] Nenhum banco listado em "Bancos falhando desde o último sucesso"
- [ ] Logs rotacionando (`ls -lh data/logs`, `docker inspect --format '{{.HostConfig.LogConfig}}' vps-backup-manager`)
- [ ] Rotina semanal: abrir o dashboard ou confiar no webhook de erro

## Operação

- [ ] Procedimento de atualização documentado (`git pull && docker compose up -d --build`)
- [ ] Teste de falha: senha errada gera erro visível e alerta, sem afetar outros bancos
- [ ] Drive: pasta `Backups/<VPS>` contém dias consecutivos e arquivos `.sha256`
