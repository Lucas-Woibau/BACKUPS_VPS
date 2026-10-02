# Fase 8 — Bancos de dados

## Conceito: conexão ≠ banco

Uma **conexão** é um servidor (`host:porta` + usuário). Depois de "Testar conexão" o painel lista os bancos
desse servidor. Opções:

- **Fazer backup automaticamente de todos os bancos encontrados nesta conexão** — a lista é consultada a cada
  execução; bancos criados depois entram sozinhos. Você pode desmarcar bancos para excluí-los.
- Desmarcado: apenas os bancos selecionados. Se um selecionado sumir do servidor, a execução registra erro.

Bancos de sistema (`information_schema`, `performance_schema`, `sys`, `mysql`, `postgres`, templates) são
ignorados por padrão.

## Princípio de menor privilégio — usuários de backup

Nunca use `root`/`postgres` no app. Crie um usuário só de leitura.

### SQL Server (2017+ em Linux/Docker, ou Windows)

```sql
-- Login só para backup (SQL Server authentication)
CREATE LOGIN backup_user WITH PASSWORD = 'SENHA-FORTE-AQUI', CHECK_POLICY = ON;
-- Para cada banco a copiar:
USE [loja];
CREATE USER backup_user FOR LOGIN backup_user;
ALTER ROLE db_backupoperator ADD MEMBER backup_user;
-- Para listar os bancos e para RESTORE VERIFYONLY (verificação do .bak):
USE [master];
GRANT VIEW ANY DATABASE TO backup_user;
GRANT CREATE ANY DATABASE TO backup_user;   -- opcional: sem ele a verificação é pulada (aviso no histórico)
```

Limitações:
- O app usa `BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM` — **não** interfere na cadeia de backups de log
  existente. Bancos em modelo FULL continuam precisando de backups de log próprios se você os usa.
- `WITH COMPRESSION` não existe na edição **Express** (deixe a opção desmarcada; o app compacta com gzip).
- O `.bak` é gerado pelo próprio SQL Server, numa pasta que precisa ser **compartilhada** com o backup manager (abaixo).

### MySQL 8.x

```sql
-- '172.%' cobre as redes Docker (bridge padrão 172.17.x e redes do compose 172.18+).
CREATE USER 'backup_user'@'172.%' IDENTIFIED BY 'SENHA-FORTE-AQUI';
GRANT SELECT, SHOW VIEW, TRIGGER, EVENT, LOCK TABLES ON *.* TO 'backup_user'@'172.%';
GRANT SHOW_ROUTINE ON *.* TO 'backup_user'@'172.%';      -- 8.0.20+: exportar procedures/functions
-- Só alguns bancos? Troque *.* por `loja`.* (SHOW_ROUTINE continua global).
FLUSH PRIVILEGES;
```

Limitações:
- `--single-transaction` garante consistência apenas para **InnoDB**. Tabelas MyISAM podem ficar inconsistentes
  sob escrita; para elas é preciso `LOCK TABLES` (bloqueia escrita durante o dump).
- MySQL 8.0.32+ com GTID: se aparecer "you need the RELOAD or FLUSH_TABLES privilege", mantenha a opção
  `--set-gtid-purged=OFF` (padrão do app com mysqldump Oracle) ou conceda `RELOAD`.
- Sem `PROCESS` o app usa `--no-tablespaces` (não exporta CREATE TABLESPACE — irrelevante para a maioria).
- Usuários/grants do servidor (tabela `mysql.user`) não são exportados; anote-os ou exporte com
  `mysqldump mysql` usando outro usuário, se necessário.

### MariaDB 10.x / 11.x

```sql
CREATE USER 'backup_user'@'172.%' IDENTIFIED BY 'SENHA-FORTE-AQUI';
GRANT SELECT, SHOW VIEW, TRIGGER, EVENT, LOCK TABLES ON *.* TO 'backup_user'@'172.%';
-- MariaDB 11.3+: para procedures/functions
GRANT SHOW CREATE ROUTINE ON *.* TO 'backup_user'@'172.%';
FLUSH PRIVILEGES;
```

Em MariaDB < 11.3, o `SELECT` global (que cobre `mysql.proc`) já permite exportar rotinas.

### PostgreSQL 14+

```sql
CREATE ROLE backup_user LOGIN PASSWORD 'SENHA-FORTE-AQUI';
GRANT pg_read_all_data TO backup_user;          -- leitura de todas as tabelas/sequências/schemas
```

PostgreSQL 13 ou anterior (por banco):

```sql
CREATE ROLE backup_user LOGIN PASSWORD 'SENHA-FORTE-AQUI';
\c loja
GRANT CONNECT ON DATABASE loja TO backup_user;
GRANT USAGE ON SCHEMA public TO backup_user;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO backup_user;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO backup_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO backup_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON SEQUENCES TO backup_user;
```

Limitações:
- `pg_read_all_data` **não** ignora Row Level Security: tabelas com RLS ativo fazem o `pg_dump` falhar.
  Para esses bancos use o dono do banco ou um papel com `BYPASSRLS`.
- Large objects (`lo_*`) exigem privilégio sobre os objetos; em caso de erro, use o dono do banco.
- `pg_dumpall --globals-only` (roles/tablespaces) roda como não-superusuário apenas com `--no-role-passwords`
  (padrão do app). As senhas das roles não serão exportadas.
- `pg_hba.conf`: permitir o usuário vindo da rede Docker:
  ```
  host  all  backup_user  172.16.0.0/12  scram-sha-256
  ```

---

## Exemplos de conexão

### SQL Server em Docker (mais comum em VPS Linux)

O SQL Server grava o `.bak` dentro do container dele; o backup manager precisa enxergar o mesmo arquivo.
O `install.sh` já cria `/opt/vps-backup-manager/mssql-backups` (dono 10001 = usuário `mssql`, grupo 1654 = app,
`chmod 2770`) e o monta no backup manager em `/mssql-backups`. Monte **a mesma pasta** no container do SQL Server:

```yaml
# docker-compose.yml do projeto que roda o SQL Server
services:
  mssql:
    image: mcr.microsoft.com/mssql/server:2022-latest
    volumes:
      - mssql-data:/var/opt/mssql
      - /opt/vps-backup-manager/mssql-backups:/var/opt/mssql/backup   # pasta compartilhada
```

`docker compose up -d` no projeto do SQL Server e, no painel → **Bancos → + Novo banco**:

| Campo | Valor |
|---|---|
| Nome | `VPS - Banco 1` |
| Tipo | SQL Server |
| IP / host | nome do container (`mssql`, com rede compartilhada — veja C abaixo) **ou** `host.docker.internal` se a porta 1433 está publicada em `127.0.0.1:1433` |
| Porta | `1433` |
| Nome do banco | `loja` (vários: `loja, crm`) ou marque "Copiar TODOS" |
| Usuário / Senha | `backup_user` / senha |
| Pasta no Drive | `VPS-Producao/Banco1` (criada automaticamente) |
| Pasta de backup no SQL Server | `/var/opt/mssql/backup` |
| Mesma pasta no backup manager | `/mssql-backups` |

Resultado no Drive: `Backups/VPS-Producao/Banco1/2026/10/01/sqlserver/VPS_-_Banco_1/loja_2026-10-01_03-00-00.bak.gz`

**Testar conexão** confirma login e se a pasta compartilhada existe.

### SQL Server instalado direto no host (Linux)

Pasta padrão do host: `/var/opt/mssql/backup`. Adicione no `docker-compose.override.yml` do backup manager:

```yaml
services:
  app:
    volumes:
      - /var/opt/mssql/backup:/mssql-backups
```

e garanta leitura para o grupo do app: `chgrp 1654 /var/opt/mssql/backup && chmod 2770 /var/opt/mssql/backup`.
Host no painel: `host.docker.internal` (o SQL Server deve escutar em `0.0.0.0`/`172.17.0.1`; firewall:
`ufw allow from 172.16.0.0/12 to any port 1433 proto tcp`).

### SQL Server em Windows (outra máquina)

Use o IP do servidor e uma pasta compartilhada (SMB) montada na VPS (`mount -t cifs`) e no compose do app.
Campo "Pasta de backup no SQL Server" no formato Windows (ex.: `D:\Backups\vbm`). Exige VPN/rede privada:
nunca exponha a porta 1433 na Internet.

### A) MySQL/MariaDB instalado no host

Dentro do container, `localhost` é o **próprio container**, não a VPS. Use `host.docker.internal`
(mapeado para o IP do host via `extra_hosts: host-gateway`, normalmente `172.17.0.1`).

1. O MySQL precisa escutar também nesse IP. Em `/etc/mysql/mysql.conf.d/mysqld.cnf` (ou `mariadb.conf.d/50-server.cnf`):
   ```ini
   bind-address = 127.0.0.1,172.17.0.1     # MySQL 8.0.13+ / MariaDB 10.11+ aceitam lista
   ```
   (Versões antigas: `bind-address = 0.0.0.0` **e** firewall bloqueando 3306 externamente.)
2. `systemctl restart mysql`
3. Firewall: permitir só a rede Docker:
   ```bash
   ufw allow from 172.16.0.0/12 to any port 3306 proto tcp
   ```
4. Painel: Host `host.docker.internal`, porta `3306`, usuário `backup_user`.

Alternativa sem mexer no bind: `network_mode: host` (veja `docker-compose.override.example.yml`) e host `127.0.0.1`.

### B) PostgreSQL instalado no host

1. `/etc/postgresql/16/main/postgresql.conf`:
   ```ini
   listen_addresses = 'localhost,172.17.0.1'
   ```
2. `/etc/postgresql/16/main/pg_hba.conf`:
   ```
   host  all  backup_user  172.16.0.0/12  scram-sha-256
   ```
3. `systemctl restart postgresql` e `ufw allow from 172.16.0.0/12 to any port 5432 proto tcp`
4. Painel: Host `host.docker.internal`, porta `5432`, banco de manutenção `postgres`.

### C) MySQL em Docker (outro projeto)

Melhor opção — **rede compartilhada**, sem publicar porta nenhuma:

```bash
docker inspect loja-mysql-1 --format '{{json .NetworkSettings.Networks}}'
# → {"loja_default": {...}}
cp docker-compose.override.example.yml docker-compose.override.yml   # ajuste os nomes das redes
docker compose up -d
```

Painel: Host = nome do serviço/container (`mysql` ou `loja-mysql-1`), porta `3306` (porta interna).
Crie o usuário de backup dentro do container:

```bash
docker exec -it loja-mysql-1 mysql -uroot -p
```

Isso adiciona o backup manager à rede existente **sem modificar** o outro projeto. Se o projeto publica a porta
só em `127.0.0.1:3307`, use a opção A com `host.docker.internal:3307` (requer o bind no IP da bridge) ou a rede compartilhada.

### D) PostgreSQL em Docker (outro projeto)

Mesmo procedimento: override com a rede `crm_backend` e host `postgres` (nome do serviço), porta `5432`.

```bash
docker exec -it crm-postgres-1 psql -U postgres -c "CREATE ROLE backup_user LOGIN PASSWORD '...'; GRANT pg_read_all_data TO backup_user;"
```

O `pg_hba.conf` das imagens oficiais aceita conexões da rede Docker por padrão (`host all all all scram-sha-256`).

---

## Descoberta automática (opcional)

Botão **Descobrir serviços** em Conexões:

- Sempre: testa conexão TCP **somente** em `host.docker.internal` nas portas 3306/3307/5432/5433. Nenhuma varredura de rede.
- Docker (opcional): lista containers com imagens mysql/mariadb/postgres via **docker-socket-proxy somente leitura**:
  ```bash
  echo 'DOCKER_DISCOVERY_URL=http://docker-proxy:2375' >> .env
  docker compose --profile discovery up -d
  ```
  Riscos: o `docker.sock` dá controle total do host. Por isso ele **nunca** é montado no app; o proxy libera apenas
  `GET /containers` (sem POST/exec). O app usa somente `/containers/json`, que não retorna variáveis de ambiente.
  Mesmo assim, o proxy pode expor detalhes dos containers a quem acessar a rede interna — habilite só se for útil e
  desligue depois (`docker compose --profile discovery down docker-proxy`).

Credenciais nunca são descobertas: você informa o usuário de backup.

## Opções avançadas por conexão

| Opção (painel) | Efeito |
|---|---|
| `--single-transaction` | snapshot consistente InnoDB sem travar escrita (padrão: ligado) |
| routines / events / triggers | inclui procedures, eventos e triggers |
| Ferramenta de dump | `auto` (mariadb-dump se existir), `mariadb-dump`, `mysqldump` |
| Timeout do dump | por conexão; padrão nas Configurações (360 min) |
| Banco de manutenção (PG) | banco usado para conectar/listar (padrão `postgres`) |
| roles/tablespaces (PG) | adiciona item `pg-globals` com `pg_dumpall --globals-only` |
| SSL/TLS | disabled / preferred / required / verify_ca / verify_identity; certificados CA em `data/certs/` |
