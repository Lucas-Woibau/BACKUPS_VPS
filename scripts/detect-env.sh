#!/usr/bin/env bash
# Diagnóstico somente-leitura do ambiente da VPS. Não altera nada, não lê senhas.
set -uo pipefail

section() { printf '\n\033[1m== %s ==\033[0m\n' "$*"; }

section "Sistema"
. /etc/os-release 2>/dev/null && echo "Distribuição: ${PRETTY_NAME:-?} (ID=${ID:-?}, LIKE=${ID_LIKE:-})"
echo "Kernel: $(uname -r) | Arquitetura: $(uname -m)"
echo "Hostname: $(hostname) | Timezone: $(cat /etc/timezone 2>/dev/null || timedatectl show -p Timezone --value 2>/dev/null)"
df -h / | tail -1 | awk '{print "Disco /: " $4 " livres de " $2}'
free -h | awk '/Mem/ {print "Memória: " $7 " disponíveis de " $2}'

section "Docker"
if command -v docker >/dev/null; then
  docker --version
  docker compose version 2>/dev/null || echo "docker compose (plugin v2): NÃO encontrado"
else
  echo "Docker: NÃO instalado"
fi

section "Bancos instalados no host (serviços systemd)"
for s in mysql mysqld mariadb postgresql; do
  systemctl is-active --quiet "$s" 2>/dev/null && echo "ativo: $s"
done
ls -d /etc/postgresql/*/main 2>/dev/null | sed 's|^|cluster PG: |'

section "Portas de banco escutando (somente local)"
if command -v ss >/dev/null; then
  ss -ltnH 2>/dev/null | awk '{print $4}' | grep -E ':(3306|3307|5432|5433)$' | sort -u | while read -r addr; do
    case "$addr" in
      127.0.0.1:*|\[::1\]:*) echo "$addr  (somente loopback — o container não alcança; veja docs/04-bancos.md)";;
      0.0.0.0:*|\[::\]:*|\*:*) echo "$addr  (todas as interfaces — verifique o firewall!)";;
      *) echo "$addr";;
    esac
  done
fi

section "Containers de banco em execução"
if command -v docker >/dev/null; then
  docker ps --format '{{.Names}}\t{{.Image}}\t{{.Ports}}\t{{.Networks}}' 2>/dev/null \
    | grep -Ei 'mysql|mariadb|postgres|postgis|timescale|percona' \
    | awk -F'\t' '{printf "container=%s  imagem=%s\n  portas=%s\n  redes=%s\n", $1, $2, $3, $4}'
fi

section "Firewall"
command -v ufw >/dev/null && ufw status 2>/dev/null | head -20
echo
echo "Use estas informações para cadastrar as conexões no painel (Conexões > Nova conexão)."
