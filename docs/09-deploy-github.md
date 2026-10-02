# Deploy automático (GitHub Actions + GHCR) — igual ao MySeeds/Bibliotrack

```
git push (master) ──► GitHub Actions
                        1. dotnet test
                        2. build + push ghcr.io/lucas-woibau/vps-backup-manager:latest (+ :<sha>)
                        3. scp docker-compose.prod.yml + scripts/ → VPS_PATH
                        4. ssh: docker compose -f docker-compose.prod.yml pull/up -d app
```

## Como fica na sua VPS

| Container | Porta | Observação |
|---|---|---|
| bibliotrack_api / _web / _db | 8080, 8088 | sem alteração |
| myseeds_api / _web / _db | 8090, 8098 | sem alteração |
| **vps_backup_manager** | **127.0.0.1:8095** | só localhost (acesso via túnel SSH) |

O backup manager:
- entra nas redes Docker de `myseeds_db` e `bibliotrack_db` (host no painel = nome do container, porta 1433 interna — nada é publicado);
- monta a pasta `/var/opt/mssql/backup` de cada SQL Server (que fica dentro do volume de dados deles) em `/mssql/myseeds` e `/mssql/bibliotrack`;
- roda como UID 10001 (mesmo do usuário `mssql`) para ler/apagar os `.bak` gerados;
- **não reinicia nem altera** os containers de banco (só cria a pasta `backup` dentro deles).

## 1. Repositório e secrets (uma vez)

1. Crie o repositório `lucas-woibau/vps-backup-manager` no GitHub e faça push deste projeto (branch `master`).
2. Em **Settings → Secrets and variables → Actions**, crie os mesmos secrets dos seus outros projetos:

| Secret | Valor |
|---|---|
| `VPS_HOST` | IP da VPS |
| `VPS_USER` | `root` |
| `VPS_SSH_KEY` | chave privada SSH (a mesma usada no MySeeds) |
| `VPS_SSH_PORT` | `22` |
| `VPS_PATH` | `/root/vps-backup-manager` |

`GITHUB_TOKEN` já existe automaticamente (publica a imagem no GHCR).

3. O primeiro push roda o workflow: testes → imagem → copia `docker-compose.prod.yml`, `.env.example` e `scripts/` para `VPS_PATH`.
   O passo de deploy **falha de propósito** na primeira vez avisando para rodar o setup (o `.env` ainda não existe). Normal.

> Imagem privada: se o pacote `vps-backup-manager` no GHCR ficar privado (padrão), a VPS precisa estar logada no GHCR
> (`docker login ghcr.io -u lucas-woibau` com um token `read:packages`) — provavelmente já está, pois puxa as imagens
> do MySeeds/Bibliotrack. Ou deixe o pacote público em GitHub → Packages → Package settings.

## 2. Setup na VPS (uma vez)

```bash
ssh root@IP_DA_VPS
cd /root/vps-backup-manager
chmod +x scripts/*.sh
sudo ./scripts/setup-vps.sh
```

O script detecta rede e pasta de dados de `myseeds_db` e `bibliotrack_db`, grava no `.env`, cria as pastas,
sobe o container e imprime os valores para preencher no painel.

Outros containers de banco: `DB_CONTAINERS="myseeds_db bibliotrack_db outro_db" ./scripts/setup-vps.sh` e
adicione as linhas equivalentes (`OUTRO_NETWORK`, `OUTRO_BACKUP_DIR`) em `docker-compose.prod.yml`.

## 3. Acessar o painel

```bash
ssh -L 8095:127.0.0.1:8095 root@IP_DA_VPS
```

Abra `http://localhost:8095`. Token do primeiro acesso: `docker exec vps_backup_manager cat /data/setup_token`.

## 4. Cadastrar os bancos (Bancos → + Novo banco)

| Campo | MySeeds | Bibliotrack |
|---|---|---|
| Nome | `VPS - MySeeds` | `VPS - Bibliotrack` |
| Tipo | SQL Server | SQL Server |
| IP / host | `myseeds_db` | `bibliotrack_db` |
| Porta | `1433` | `1433` |
| Nome do banco | valor de `DB_NAME` no `.env` do MySeeds | nome do banco do Bibliotrack |
| Usuário / Senha | `backup_user` só com `db_backupoperator` (docs/04-bancos.md). **Evite `sa`**: quem roubar a senha controla o servidor inteiro | idem |
| Pasta no Drive | `VPS/MySeeds` | `VPS/Bibliotrack` |
| Pasta de backup no SQL Server | `/var/opt/mssql/backup` | `/var/opt/mssql/backup` |
| Mesma pasta no backup manager | `/mssql/myseeds` | `/mssql/bibliotrack` |

**Testar conexão** → **Salvar** → **Backup agora**. Depois: Configurações → Google Drive (Gmail) e Agendamentos.

Não sabe o nome do banco? Clique em "Testar conexão": a lista de bancos encontrados aparece.

## Atualizações

Qualquer push em `master` que altere `src/`, `tests/`, `Dockerfile`, `docker-compose.prod.yml` ou `scripts/`
reconstrói e reimplanta. Também dá para rodar manualmente em Actions → *Build & Deploy Backup Manager* → *Run workflow*.
Dados (`data/`, `rclone/`, `.env`) ficam na VPS e não são tocados pelo deploy.

Voltar uma versão: `IMAGE_TAG=<sha> docker compose -f docker-compose.prod.yml up -d app`.
