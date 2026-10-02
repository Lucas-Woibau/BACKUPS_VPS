# syntax=docker/dockerfile:1.7
# VPS Backup Manager — imagem de produção.
# Contém: app .NET 10 (ASP.NET Core), mariadb-dump/mysqldump, pg_dump/pg_dumpall (cliente PG 18 — compatível
# com servidores 9.2+), rclone, age, zstd.

ARG DOTNET_VERSION=10.0
ARG RCLONE_IMAGE=rclone/rclone:1.71

# ------------------------------------------------------------------ build
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
WORKDIR /src
COPY src/VpsBackupManager/VpsBackupManager.csproj src/VpsBackupManager/packages.lock.json src/VpsBackupManager/
RUN dotnet restore src/VpsBackupManager/VpsBackupManager.csproj
COPY src/ src/
RUN dotnet publish src/VpsBackupManager/VpsBackupManager.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ------------------------------------------------------------------ rclone (binário estático oficial)
FROM ${RCLONE_IMAGE} AS rclone

# ------------------------------------------------------------------ runtime
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble AS runtime

# Versão do cliente PostgreSQL: deve ser >= à versão do servidor mais novo que você vai copiar.
ARG PG_CLIENT_VERSION=18
# mariadb  -> mariadb-dump (recomendado para MariaDB; funciona com MySQL 5.7/8.x na maioria dos casos)
# mysql    -> mysqldump 8.0 da Oracle (use se seus servidores são MySQL 8 com recursos específicos)
ARG MYSQL_CLIENT_FLAVOR=mariadb

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl tzdata age zstd gzip; \
    install -d /usr/share/postgresql-common/pgdg; \
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc; \
    . /etc/os-release; \
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" > /etc/apt/sources.list.d/pgdg.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends "postgresql-client-${PG_CLIENT_VERSION}"; \
    if [ "$MYSQL_CLIENT_FLAVOR" = "mysql" ]; then \
        apt-get install -y --no-install-recommends mysql-client-core-8.0; \
    else \
        apt-get install -y --no-install-recommends mariadb-client-core; \
    fi; \
    rm -rf /var/lib/apt/lists/*; \
    install -d -o "$APP_UID" -g "$APP_UID" -m 700 /data /backups /config/rclone

COPY --from=rclone /usr/local/bin/rclone /usr/local/bin/rclone
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DATA_DIR=/data \
    BACKUP_ROOT=/backups \
    RCLONE_CONFIG=/config/rclone/rclone.conf \
    HOME=/data/home \
    DOTNET_gcServer=0

# Usuário não-root (UID 1654, padrão das imagens .NET).
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=60s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "VpsBackupManager.dll"]
