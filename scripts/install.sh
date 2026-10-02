#!/usr/bin/env bash
# VPS Backup Manager — instalação/atualização em VPS Ubuntu/Debian.
# Uso (na pasta do projeto):  sudo ./scripts/install.sh
set -euo pipefail

cd "$(dirname "$0")/.."
APP_UID=1654   # usuário "app" das imagens .NET

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[aviso]\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m[erro]\033[0m %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "Execute com sudo/root (necessário para instalar Docker e ajustar permissões)."

# ---------------------------------------------------------------- 1. ambiente
. /etc/os-release || die "Não foi possível identificar a distribuição (/etc/os-release)."
log "Distribuição: ${PRETTY_NAME} | Arquitetura: $(uname -m)"
case "${ID}:${ID_LIKE:-}" in
  ubuntu:*|debian:*|*:*debian*) ;;
  *) warn "Distribuição não testada (${ID}). Instale Docker manualmente e rode novamente." ;;
esac

# ---------------------------------------------------------------- 2. Docker
if ! command -v docker >/dev/null 2>&1; then
  log "Instalando Docker Engine pelo repositório oficial (apt)…"
  apt-get update
  apt-get install -y ca-certificates curl
  install -m 0755 -d /etc/apt/keyrings
  DIST_ID="${ID}"; [ "${ID}" = "ubuntu" ] || [ "${ID}" = "debian" ] || DIST_ID="debian"
  curl -fsSL "https://download.docker.com/linux/${DIST_ID}/gpg" -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${DIST_ID} ${VERSION_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi
systemctl enable --now docker >/dev/null 2>&1 || true
docker compose version >/dev/null 2>&1 || die "Docker Compose v2 (plugin) não encontrado. Instale docker-compose-plugin."
log "Docker: $(docker --version) | $(docker compose version)"

# ---------------------------------------------------------------- 3. .env
if [ ! -f .env ]; then
  log "Criando .env com chave aleatória…"
  cp .env.example .env
  SECRET="$(openssl rand -base64 48 | tr -d '\n')"
  sed -i "s|^APP_SECRET_KEY=.*|APP_SECRET_KEY=${SECRET}|" .env
  sed -i "s|^VPS_HOSTNAME=.*|VPS_HOSTNAME=$(hostname)|" .env
else
  log ".env já existe — mantido."
  grep -qE '^APP_SECRET_KEY=.{32,}' .env || die "APP_SECRET_KEY no .env está vazia ou curta."
fi
chmod 600 .env

# ---------------------------------------------------------------- 4. diretórios
log "Preparando diretórios persistentes (data, backups, rclone)…"
mkdir -p data backups rclone
chown -R "${APP_UID}:${APP_UID}" data backups rclone
chmod 700 data backups rclone
[ -f rclone/rclone.conf ] && chmod 600 rclone/rclone.conf
# Pasta compartilhada com o SQL Server: dono = usuário mssql (UID 10001), grupo = app (GID 1654), setgid
# para que os .bak criados pelo SQL Server fiquem legíveis/removíveis pelo backup manager.
mkdir -p mssql-backups
chown 10001:"${APP_UID}" mssql-backups
chmod 2770 mssql-backups

# ---------------------------------------------------------------- 5. build + start
log "Construindo imagem e iniciando (pode levar alguns minutos)…"
docker compose build --pull
docker compose up -d

log "Aguardando health check…"
for i in $(seq 1 30); do
  status="$(docker inspect -f '{{.State.Health.Status}}' vps-backup-manager 2>/dev/null || echo starting)"
  [ "$status" = "healthy" ] && break
  sleep 4
done
docker compose ps

PORT="$(grep -E '^APP_PORT=' .env | cut -d= -f2)"; PORT="${PORT:-8080}"
echo
log "Instalação concluída."
if [ -f data/setup_token ]; then
  echo "  Token de primeiro acesso:  sudo ./scripts/show-setup-token.sh"
fi
echo "  Painel (somente localhost da VPS): http://127.0.0.1:${PORT}"
echo "  Do seu PC, via túnel SSH:   ssh -L ${PORT}:127.0.0.1:${PORT} usuario@IP_DA_VPS   e abra http://localhost:${PORT}"
echo "  Configurar Google Drive:    sudo ./scripts/rclone-config.sh"
echo "  Logs:                       docker compose logs -f app"
