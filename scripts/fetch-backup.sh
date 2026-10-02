#!/usr/bin/env bash
# Baixa um backup do Google Drive para ./backups/restore e confere o SHA-256 com o arquivo .sha256.
# NÃO restaura nada — a restauração é manual (docs/06-restore.md), por segurança.
#
# Uso: ./scripts/fetch-backup.sh "gdrive:Backups/VPS-Producao/2026/10/01/mysql/MySQL_principal/loja_2026-10-01_03-00-00.sql.gz"
#      (copie o caminho exato da coluna "Destino" no Histórico do painel)
set -euo pipefail
cd "$(dirname "$0")/.."

SRC="${1:-}"
[ -n "$SRC" ] || { echo "Uso: $0 <remote:caminho/arquivo>"; exit 1; }
case "$SRC" in *..*|*$'\n'*) echo "Caminho inválido."; exit 1;; esac
NAME="$(basename "$SRC")"

docker compose exec -T app mkdir -p /backups/restore
echo "Baixando ${SRC}…"
docker compose exec -T app rclone copyto "$SRC" "/backups/restore/${NAME}"
docker compose exec -T app rclone copyto "${SRC}.sha256" "/backups/restore/${NAME}.sha256" || echo "(sem arquivo .sha256 remoto)"

if docker compose exec -T app test -f "/backups/restore/${NAME}.sha256"; then
  echo "Verificando SHA-256…"
  docker compose exec -T -w /backups/restore app sha256sum -c "${NAME}.sha256"
fi
echo
echo "Arquivo disponível em: ./backups/restore/${NAME}"
echo "Próximos passos (descriptografar/descompactar/restaurar): docs/06-restore.md"
