#!/usr/bin/env bash
# Exporta a configuração do próprio backup manager (SQLite com conexões/agendas/histórico, .env, rclone.conf)
# para um arquivo .tar.gz CRIPTOGRAFADO com age. Guarde-o FORA da VPS (recuperação de desastre).
# Uso: sudo ./scripts/export-app-config.sh age1suachavepublica...
set -euo pipefail
cd "$(dirname "$0")/.."
RECIPIENT="${1:-}"
[[ "$RECIPIENT" =~ ^age1[0-9a-z]{58}$ ]] || { echo "Informe a chave pública age (age1...)."; exit 1; }

OUT="vbm-config-$(date +%Y%m%d-%H%M%S).tar.gz.age"
# Parar o app por alguns segundos garante um SQLite consistente (WAL aplicado).
docker compose stop app >/dev/null
trap 'docker compose start app >/dev/null' EXIT
tar -czf - .env data/app.db rclone/rclone.conf \
  | docker compose run --rm -T --no-deps --entrypoint age app -r "$RECIPIENT" > "$OUT"
chmod 600 "$OUT"
echo "Exportado: $OUT (criptografado). Copie para fora da VPS, ex.: scp usuario@vps:$(pwd)/$OUT ."
