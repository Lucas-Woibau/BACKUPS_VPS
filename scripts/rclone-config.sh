#!/usr/bin/env bash
# Configura o remote do Google Drive DENTRO do container (o arquivo fica em ./rclone/rclone.conf).
# Fluxo headless: o rclone pedirá um token gerado no seu PC com `rclone authorize "drive"` (docs/03-google-drive.md).
set -euo pipefail
cd "$(dirname "$0")/.."

case "${1:-config}" in
  config)
    docker compose exec app rclone config
    ;;
  test)
    REMOTE="${2:-gdrive}"
    echo "Listando a raiz de ${REMOTE}: (máx. 20 itens)"
    docker compose exec -T app rclone lsf "${REMOTE}:" --max-depth 1 | head -20
    docker compose exec -T app rclone about "${REMOTE}:" || true
    ;;
  reconnect)
    REMOTE="${2:-gdrive}"
    docker compose exec app rclone config reconnect "${REMOTE}:"
    ;;
  *)
    echo "Uso: $0 [config | test <remote> | reconnect <remote>]"; exit 1;;
esac

[ -f rclone/rclone.conf ] && sudo chmod 600 rclone/rclone.conf 2>/dev/null || true
