#!/usr/bin/env bash
# Mostra o token de primeiro acesso (existe apenas até o primeiro administrador ser criado).
set -euo pipefail
cd "$(dirname "$0")/.."
if docker compose exec -T app sh -c 'test -f /data/setup_token' 2>/dev/null; then
  echo "Token de primeiro acesso:"
  docker compose exec -T app cat /data/setup_token
  echo "Use-o em http://127.0.0.1:${APP_PORT:-8080}/setup.html. Ele é apagado após criar o administrador."
else
  echo "Nenhum token pendente: o administrador já foi criado (ou o container não está rodando)."
fi
