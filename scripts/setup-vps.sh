#!/usr/bin/env bash
# Primeira instalação na VPS (modo produção com imagem do GHCR).
# Rode na pasta definida em VPS_PATH (a mesma do secret do GitHub), como root:
#   sudo ./scripts/setup-vps.sh
#
# O que faz (sem reiniciar nem alterar os containers de banco existentes):
#   - cria .env com chave aleatória (chmod 600) e pastas data/ backups/ rclone/ (dono 10001)
#   - descobre a rede Docker e a pasta de dados de cada SQL Server (myseeds_db, bibliotrack_db)
#   - cria /var/opt/mssql/backup dentro de cada SQL Server (pasta onde o .bak é gerado)
#   - grava tudo no .env e sobe o container
set -euo pipefail
cd "$(dirname "$0")/.."

RUN_UID=10001
DB_CONTAINERS="${DB_CONTAINERS:-myseeds_db bibliotrack_db}"
COMPOSE="docker compose -f docker-compose.prod.yml"

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m[erro]\033[0m %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "Execute como root (sudo)."
command -v docker >/dev/null || die "Docker não encontrado."
[ -f docker-compose.prod.yml ] || die "docker-compose.prod.yml não está nesta pasta (rode o workflow do GitHub uma vez ou copie o arquivo)."

set_env() { # set_env CHAVE valor
  [ -s .env ] && [ -n "$(tail -c1 .env)" ] && echo >> .env   # garante quebra de linha no fim antes de anexar
  if grep -qE "^$1=" .env; then sed -i "s|^$1=.*|$1=$2|" .env; else echo "$1=$2" >> .env; fi
}

# ---------------------------------------------------------------- .env
if [ ! -f .env ]; then
  log "Criando .env…"
  if [ -f .env.example ]; then cp .env.example .env; else touch .env; fi
  set_env APP_SECRET_KEY "$(openssl rand -base64 48 | tr -d '\n')"
  set_env VPS_HOSTNAME "$(hostname)"
fi
grep -qE '^APP_SECRET_KEY=.{32,}' .env || set_env APP_SECRET_KEY "$(openssl rand -base64 48 | tr -d '\n')"
grep -qE '^APP_PORT=' .env || set_env APP_PORT 8095
set_env APP_PORT "$(grep -E '^APP_PORT=' .env | cut -d= -f2 | sed 's/^8080$/8095/')"
chmod 600 .env

# ---------------------------------------------------------------- pastas
log "Criando pastas data/ backups/ rclone/…"
mkdir -p data backups rclone
chown -R "${RUN_UID}:${RUN_UID}" data backups rclone
chmod 700 data backups rclone

# ---------------------------------------------------------------- bancos existentes
for c in $DB_CONTAINERS; do
  docker inspect "$c" >/dev/null 2>&1 || die "Container $c não encontrado (ajuste DB_CONTAINERS)."
  prefix="$(echo "${c%_db}" | tr '[:lower:]-' '[:upper:]_')"     # myseeds_db -> MYSEEDS

  network="$(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' "$c" | awk '{print $1}')"
  [ -n "$network" ] || die "$c não está em nenhuma rede Docker."

  data_src="$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/opt/mssql"}}{{.Source}}{{end}}{{end}}' "$c")"
  if [ -z "$data_src" ]; then
    die "$c não tem volume em /var/opt/mssql (os dados não são persistentes!). Corrija o compose desse projeto antes."
  fi

  log "$c → rede '$network', dados em $data_src"
  # Pasta de backup dentro do SQL Server (dono mssql). Não reinicia o banco.
  docker exec -u 0 "$c" sh -c 'mkdir -p /var/opt/mssql/backup && chown mssql /var/opt/mssql/backup 2>/dev/null || chown 10001 /var/opt/mssql/backup; chmod 770 /var/opt/mssql/backup'

  set_env "${prefix}_NETWORK" "$network"
  set_env "${prefix}_BACKUP_DIR" "${data_src}/backup"
done

# ---------------------------------------------------------------- subir
log "Baixando imagem e iniciando…"
$COMPOSE pull app
$COMPOSE up -d app

for i in $(seq 1 30); do
  status="$(docker inspect -f '{{.State.Health.Status}}' vps_backup_manager 2>/dev/null || echo starting)"
  [ "$status" = "healthy" ] && break
  sleep 4
done
$COMPOSE ps

PORT="$(grep -E '^APP_PORT=' .env | cut -d= -f2)"
echo
log "Pronto."
echo "  Token do primeiro acesso:  docker exec vps_backup_manager cat /data/setup_token"
echo "  Do seu PC:                 ssh -L ${PORT}:127.0.0.1:${PORT} root@IP_DA_VPS   →  http://localhost:${PORT}"
echo "  Logs:                      $COMPOSE logs -f app"
echo
echo "  No painel (Bancos → + Novo banco), para cada SQL Server:"
for c in $DB_CONTAINERS; do
  name="${c%_db}"
  echo "    ${name}: host=${c}  porta=1433  pasta no SQL Server=/var/opt/mssql/backup  mesma pasta no backup manager=/mssql/${name}"
done
