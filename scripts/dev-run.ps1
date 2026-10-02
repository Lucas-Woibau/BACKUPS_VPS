# Desenvolvimento local (Windows/PowerShell). Não usar em produção.
$root = Split-Path -Parent $PSScriptRoot
$dev = Join-Path $root ".dev"
New-Item -ItemType Directory -Force $dev | Out-Null
$env:APP_SECRET_KEY = "dev-only-secret-key-change-me-0123456789abcdef"
$env:DATA_DIR = Join-Path $dev "data"
$env:BACKUP_ROOT = Join-Path $dev "backups"
$env:RCLONE_CONFIG = Join-Path $dev "rclone.conf"
$env:HOST_GATEWAY_NAME = "127.0.0.1"
$env:ASPNETCORE_URLS = "http://127.0.0.1:8080"
dotnet run --project (Join-Path $root "src/VpsBackupManager")
