# Fase 10 — Restauração

> Restaure sempre primeiro em um **banco novo/temporário** (ex.: `loja_restore`), confira, e só então troque.
> O painel não faz restore (proposital na v1: operação destrutiva).

Formato dos arquivos:

| Tipo | Arquivo | Conteúdo |
|---|---|---|
| MySQL/MariaDB | `banco_AAAA-MM-DD_HH-mm-ss.sql.gz` | SQL de `mariadb-dump`/`mysqldump` **sem** `CREATE DATABASE` (restaura em qualquer nome) |
| PostgreSQL | `banco_AAAA-MM-DD_HH-mm-ss.dump.gz` | `pg_dump --format=custom` (use `pg_restore`) |
| SQL Server | `banco_AAAA-MM-DD_HH-mm-ss.bak.gz` | `BACKUP DATABASE ... COPY_ONLY, CHECKSUM` (use `RESTORE DATABASE`) |
| PG globals | `pg-globals_AAAA-...sql.gz` | roles/tablespaces (`psql`) |
| Sufixos | `.zst` = zstd, `.age` = criptografado, `.sha256` = checksum |

## 1. Baixar do Google Drive

**Na própria VPS** (o caminho exato está na coluna Destino do Histórico):

```bash
./scripts/fetch-backup.sh "gdrive:Backups/VPS-Producao/2026/10/01/mysql/MySQL_principal/loja_2026-10-01_03-00-00.sql.gz"
cd backups/restore
```

**Em outra máquina** (VPS nova, PC): instale o rclone, configure o remote (Fase 7) e:

```bash
rclone copy "gdrive:Backups/VPS-Producao/2026/10/01/mysql/MySQL_principal/" ./restore --include "loja_2026-10-01_03-00-00*"
cd restore && sha256sum -c loja_2026-10-01_03-00-00.sql.gz.sha256
```

Ou baixe pelo site drive.google.com (os dois arquivos) e confira o `sha256sum -c`.

> Escopo `drive.file`: arquivos são visíveis no site do Drive normalmente; em outra máquina, use o mesmo
> client ID/remote ou baixe pelo navegador.

## 2. Descriptografar (se `.age`)

A chave privada **nunca** fica na VPS. Na máquina onde ela está:

```bash
age -d -i age-key.txt -o loja_2026-10-01_03-00-00.sql.gz loja_2026-10-01_03-00-00.sql.gz.age
```

(O `.sha256` confere o arquivo `.age` — rode `sha256sum -c` antes de descriptografar.)

## 3. Descompactar

```bash
gunzip -k loja_2026-10-01_03-00-00.sql.gz          # → loja_2026-10-01_03-00-00.sql
# zstd:
zstd -d loja_2026-10-01_03-00-00.sql.zst
```

Ou sem gravar o descompactado: `gunzip -c arquivo.sql.gz | mysql ...` (abaixo).

## 4A. Restaurar MySQL / MariaDB

```bash
# criar banco
mysql -u root -p -e "CREATE DATABASE loja_restore CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"
# importar
gunzip -c loja_2026-10-01_03-00-00.sql.gz | mysql -u root -p loja_restore
# conferir
mysql -u root -p -e "SELECT table_name, table_rows FROM information_schema.tables WHERE table_schema='loja_restore';"
```

Banco em container:

```bash
docker exec -i loja-mysql-1 mysql -uroot -p"$MYSQL_ROOT_PASSWORD" -e "CREATE DATABASE loja_restore CHARACTER SET utf8mb4;"
gunzip -c loja_2026-10-01_03-00-00.sql.gz | docker exec -i loja-mysql-1 mysql -uroot -p"$MYSQL_ROOT_PASSWORD" loja_restore
```

Problemas comuns:
- `ERROR 1227 ... DEFINER`: views/rotinas com `DEFINER` de um usuário inexistente. Crie o usuário ou remova o definer:
  `gunzip -c arq.sql.gz | sed -E 's/DEFINER=`[^`]+`@`[^`]+`//g' | mysql ...`
- `max_allowed_packet`: aumente no servidor (`SET GLOBAL max_allowed_packet=1073741824;`).

Trocar para produção (após validar): renomeie bancos/ajuste a aplicação para `loja_restore`, ou importe o mesmo
arquivo no banco original numa janela de manutenção (`DROP DATABASE loja; CREATE DATABASE loja ...;` — **destrutivo**).

## 4B. Restaurar PostgreSQL

```bash
gunzip -k crm_2026-10-01_03-00-00.dump.gz              # → crm_2026-10-01_03-00-00.dump
pg_restore -l crm_2026-10-01_03-00-00.dump | head      # inspeciona o conteúdo (valida o arquivo)

# (opcional) roles antes, se exportou globals:
gunzip -c pg-globals_2026-10-01_03-00-00.sql.gz | psql -U postgres -d postgres

createdb -U postgres -O app_user crm_restore
pg_restore -U postgres -d crm_restore --no-owner --role=app_user -j 4 crm_2026-10-01_03-00-00.dump
# mantendo donos originais (se as roles existem): omita --no-owner/--role
psql -U postgres -d crm_restore -c "\dt+"
```

Banco em container:

```bash
docker exec -i crm-postgres-1 createdb -U postgres crm_restore
docker exec -i crm-postgres-1 pg_restore -U postgres -d crm_restore --no-owner < crm_2026-10-01_03-00-00.dump
```

Versão: `pg_restore` deve ser da mesma versão ou mais nova que o `pg_dump` que gerou o arquivo (a imagem usa cliente 18).
Para restaurar num servidor mais antigo, use o `pg_restore` 18 apontando para ele (`-h host -p porta`), por exemplo
dentro do container do backup manager:

```bash
docker compose exec -T app pg_restore --version
```

## 4C. Restaurar SQL Server

```bash
gunzip -k loja_2026-10-01_03-00-00.bak.gz
cp loja_2026-10-01_03-00-00.bak /opt/vps-backup-manager/mssql-backups/   # pasta compartilhada com o SQL Server
```

No SQL Server (sqlcmd, SSMS ou Azure Data Studio), como `sa` ou dono:

```sql
-- 1. ver os arquivos lógicos dentro do backup
RESTORE FILELISTONLY FROM DISK = N'/var/opt/mssql/backup/loja_2026-10-01_03-00-00.bak';

-- 2. restaurar como banco NOVO (não sobrescreve o original)
RESTORE DATABASE [loja_restore]
FROM DISK = N'/var/opt/mssql/backup/loja_2026-10-01_03-00-00.bak'
WITH MOVE N'loja'     TO N'/var/opt/mssql/data/loja_restore.mdf',
     MOVE N'loja_log' TO N'/var/opt/mssql/data/loja_restore_log.ldf',
     CHECKSUM, STATS = 10;
```

(Os nomes `loja` / `loja_log` vêm da coluna `LogicalName` do passo 1.)

Container: `docker exec -it mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -Q "RESTORE FILELISTONLY FROM DISK=N'/var/opt/mssql/backup/loja_2026-10-01_03-00-00.bak'"`

Substituir o banco original (**destrutivo**, janela de manutenção): `RESTORE DATABASE [loja] FROM DISK = ... WITH REPLACE, CHECKSUM`.
Usuários órfãos após restaurar em outro servidor: `ALTER USER app_user WITH LOGIN = app_user;`.

## 5. Teste periódico de restauração (recomendado mensal)

```bash
# MySQL temporário isolado
docker run -d --name restore-test -e MYSQL_ROOT_PASSWORD=teste --network none mysql:8.4
sleep 30
docker exec restore-test mysql -uroot -pteste -e "CREATE DATABASE t"
gunzip -c loja_*.sql.gz | docker exec -i restore-test mysql -uroot -pteste t
docker exec restore-test mysql -uroot -pteste -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='t'"
docker rm -f restore-test
```

```bash
# PostgreSQL temporário isolado
docker run -d --name restore-test -e POSTGRES_PASSWORD=teste --network none postgres:18
sleep 10
docker exec restore-test createdb -U postgres t
docker exec -i restore-test pg_restore -U postgres -d t --no-owner < crm_*.dump
docker exec restore-test psql -U postgres -d t -c "\dt"
docker rm -f restore-test
```

## 6. Recuperação de desastre (VPS perdida)

1. Nova VPS → instale o projeto (Fase 6).
2. Se exportou a configuração (`scripts/export-app-config.sh`): `age -d -i age-key.txt vbm-config-*.tar.gz.age | tar -xz`
   antes do `install.sh` (restaura `.env`, `data/app.db`, `rclone/rclone.conf`).
   Sem export: reconfigure rclone e conexões (as senhas dependem da `APP_SECRET_KEY` antiga).
3. Baixe e restaure os bancos conforme acima.
