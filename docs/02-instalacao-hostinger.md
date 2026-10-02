# Fase 6 — Instalação na VPS Hostinger

Pré-requisito: VPS Linux (Ubuntu 22.04/24.04 ou Debian 12 recomendados) com acesso SSH como root ou sudo.

## 1. Acessar e atualizar a VPS

```bash
ssh root@IP_DA_VPS
apt update && apt -y upgrade
apt -y install git curl openssl ufw
timedatectl set-timezone America/Sao_Paulo   # opcional (o app tem timezone própria)
```

## 2. Identificar o ambiente

```bash
cat /etc/os-release        # distribuição
uname -m                   # arquitetura (x86_64 / aarch64)
docker --version; docker compose version   # Docker já instalado?
```

Depois de clonar o projeto, `./scripts/detect-env.sh` mostra tudo isso e também bancos no host,
portas abertas e containers de banco — sem alterar nada.

## 3. Firewall (antes de expor qualquer coisa)

```bash
ufw default deny incoming
ufw default allow outgoing
ufw allow OpenSSH
ufw enable
ufw status
```

O painel escuta apenas em `127.0.0.1:8080`; **não** abra a porta 8080. Se for usar proxy HTTPS, abra só 80/443.

> Atenção: Docker publica portas contornando o UFW. Por isso o compose publica em `127.0.0.1`.
> Nunca publique bancos de dados com `-p 3306:3306`; use `127.0.0.1:3306:3306` ou nada.

## 4. Clonar e instalar

```bash
mkdir -p /opt && cd /opt
git clone <URL_DO_SEU_REPOSITORIO> vps-backup-manager
cd vps-backup-manager
chmod +x scripts/*.sh
sudo ./scripts/install.sh
```

O `install.sh`:
- instala Docker Engine + Compose pelo repositório oficial (se faltar);
- cria `.env` com `APP_SECRET_KEY` aleatória e `chmod 600`;
- cria `data/ backups/ rclone/` com dono UID 1654 (usuário do container) e `chmod 700`;
- faz `docker compose build` e `up -d`, aguardando o health check.

Instalação manual equivalente:

```bash
cp .env.example .env
sed -i "s|^APP_SECRET_KEY=.*|APP_SECRET_KEY=$(openssl rand -base64 48 | tr -d '\n')|" .env
chmod 600 .env
mkdir -p data backups rclone && chown -R 1654:1654 data backups rclone && chmod 700 data backups rclone
docker compose up -d --build
```

## 5. Abrir o painel do seu PC (túnel SSH)

```bash
ssh -L 8080:127.0.0.1:8080 root@IP_DA_VPS
```

Abra `http://localhost:8080` no navegador do PC. (O túnel só é necessário enquanto você usa o painel; os backups continuam rodando com o PC desligado.)

Alternativas: Tailscale na VPS (acesse `http://IP_TAILSCALE:8080` após `BIND_ADDRESS=IP_TAILSCALE`) ou reverse proxy HTTPS (`deploy/`).

## 6. Primeiro acesso

```bash
sudo ./scripts/show-setup-token.sh
```

Em `/setup.html` informe o token, usuário e senha forte (≥ 12 caracteres). O token é apagado em seguida.
Não existe usuário/senha padrão.

## 7. Próximos passos

1. **Configurações** → nome da VPS, timezone, espaço mínimo.
2. **Google Drive** → `docs/03-google-drive.md`, depois "Salvar e testar upload".
3. **Conexões** → `docs/04-bancos.md` (crie usuários de backup com privilégio mínimo).
4. **Agendamentos** → ex.: todos os dias às 03:00.
5. **Executar backup agora** e seguir o checklist `docs/05-teste-inicial.md`.

## Reinício automático

- `restart: unless-stopped` + `systemctl enable docker` (feito pelo install) ⇒ o app volta sozinho após reboot.
- Teste: `sudo reboot`, reconecte e rode `docker compose ps` (status `healthy`).

## Atualização

```bash
cd /opt/vps-backup-manager
git pull
docker compose up -d --build
docker image prune -f
```

Migrations do SQLite rodam automaticamente na subida. Dados ficam em `data/`, `backups/`, `rclone/`.

## Logs no servidor

```bash
docker compose logs -f app          # stdout (JSON, rotacionado pelo Docker: 5 x 10 MB)
ls data/logs/                        # arquivos diários app-AAAAMMDD.log (20 MB, 30 arquivos)
```
