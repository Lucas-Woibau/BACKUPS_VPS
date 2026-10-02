# Teste local completo com Docker Desktop (Windows/PowerShell). Mensagens sem acento (compatibilidade PS 5.1).
#   .\scripts\local-test.ps1          sobe tudo (build da imagem + SQL Server de teste + banco "loja")
#   .\scripts\local-test.ps1 -Down    derruba e apaga os dados de teste
param([switch]$Down)
$ErrorActionPreference = "Continue"  # docker escreve progresso no stderr
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$compose = @("compose", "-f", "docker-compose.local.yml", "--env-file", ".env.local")

function New-Secret([int]$bytes) {
    $b = New-Object byte[] $bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    return [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', 'x').Replace('/', 'y')
}

if ($Down) {
    docker @compose down -v
    Write-Host "Ambiente local removido."
    return
}

# ---- .env.local (gitignored): chave do app + senha do SA de teste
if (-not (Test-Path .env.local)) {
    $sa = "Vbm#" + (New-Secret 12) + "9a"
    @(
        "APP_SECRET_KEY=$(New-Secret 48)",
        "SA_PASSWORD=$sa",
        "VPS_HOSTNAME=pc-local",
        "TZ_DEFAULT=America/Sao_Paulo",
        "SESSION_COOKIE_SECURE=false"
    ) | Set-Content -Encoding ascii .env.local
    Write-Host "Criado .env.local (senhas de teste)."
}
$sa = (Get-Content .env.local | Where-Object { $_ -like "SA_PASSWORD=*" }) -replace "^SA_PASSWORD=", ""

docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Desktop nao esta rodando." }

Write-Host "==> Build da imagem e subida dos containers (primeira vez demora alguns minutos)..."
docker @compose up -d --build
if ($LASTEXITCODE -ne 0) { throw "docker compose up falhou." }

Write-Host "==> Aguardando SQL Server..."
$sqlcmd = "/opt/mssql-tools18/bin/sqlcmd"
for ($i = 0; $i -lt 40; $i++) {
    docker exec teste_db $sqlcmd -S localhost -U sa -P $sa -C -Q "SELECT 1" *> $null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep 3
}
if ($LASTEXITCODE -ne 0) { throw "SQL Server nao respondeu." }

Write-Host "==> Criando banco de teste 'loja' com dados..."
$seed = @"
IF DB_ID('loja') IS NULL CREATE DATABASE loja;
GO
USE loja;
IF OBJECT_ID('produtos') IS NULL
BEGIN
  CREATE TABLE produtos (id INT IDENTITY PRIMARY KEY, nome NVARCHAR(100), preco DECIMAL(10,2), criado DATETIME2 DEFAULT SYSUTCDATETIME());
  INSERT INTO produtos (nome, preco) SELECT TOP 5000 CONCAT('Produto ', ROW_NUMBER() OVER (ORDER BY (SELECT 1))), ABS(CHECKSUM(NEWID())) % 1000
  FROM sys.all_objects a CROSS JOIN sys.all_objects b;
END
GO
SELECT COUNT(*) AS produtos FROM loja.dbo.produtos;
"@
$seedFile = Join-Path $env:TEMP "vbm-seed.sql"
[IO.File]::WriteAllText($seedFile, $seed, (New-Object Text.UTF8Encoding $false))  # sem BOM
docker cp $seedFile teste_db:/tmp/vbm-seed.sql | Out-Null
docker exec teste_db $sqlcmd -S localhost -U sa -P $sa -C -i /tmp/vbm-seed.sql
Remove-Item $seedFile

Write-Host "==> Aguardando o painel..."
for ($i = 0; $i -lt 40; $i++) {
    try { $r = Invoke-WebRequest -UseBasicParsing http://127.0.0.1:8095/api/health -TimeoutSec 3; if ($r.StatusCode -eq 200) { break } } catch { }
    Start-Sleep 3
}

$hasToken = docker exec vbm_local sh -c "test -f /data/setup_token && echo yes"
Write-Host ""
Write-Host "Pronto! Painel: http://localhost:8095"
if ($hasToken -eq "yes") { Write-Host "Token de primeiro acesso: docker exec vbm_local cat /data/setup_token" }
else { Write-Host "Administrador ja criado (token consumido)." }
Write-Host ""
Write-Host "Cadastre em Bancos -> + Novo banco:"
Write-Host "  Tipo: SQL Server | Host: teste_db | Porta: 1433 | Banco: loja | Usuario: sa"
Write-Host "  Senha: SA_PASSWORD do arquivo .env.local"
Write-Host "  Pasta de backup no SQL Server: /var/opt/mssql/backup | Mesma pasta no backup manager: /mssql/teste"
Write-Host ""
Write-Host "Logs: docker compose -f docker-compose.local.yml logs -f app"
Write-Host "Remover tudo: .\scripts\local-test.ps1 -Down"
