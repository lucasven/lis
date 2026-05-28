#!/usr/bin/env bash
# Installs Postgres 17 + pgvector from the PGDG apt repo, then creates the
# 'lis' role + 'lis' database and enables the vector extension.
# Run with: sudo bash /home/agenticcompany/lis/setup-system.sh
set -euo pipefail

LIS_DB_PASSWORD="${LIS_DB_PASSWORD:-changeme}"

echo "[1/5] Installing prerequisites..."
apt-get update -y
apt-get install -y curl ca-certificates gnupg lsb-release

echo "[2/5] Adding PostgreSQL PGDG apt repo..."
install -d /usr/share/postgresql-common/pgdg
curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
  https://www.postgresql.org/media/keys/ACCC4CF8.asc
echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" \
  > /etc/apt/sources.list.d/pgdg.list
apt-get update -y

echo "[3/5] Installing postgresql-17 + postgresql-17-pgvector..."
apt-get install -y postgresql-17 postgresql-17-pgvector

echo "[4/5] Ensuring postgres service is running..."
systemctl enable --now postgresql

echo "[5/5] Creating lis role + database + vector extension..."
sudo -u postgres psql -v ON_ERROR_STOP=1 <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'lis') THEN
    CREATE ROLE lis LOGIN PASSWORD '${LIS_DB_PASSWORD}';
  ELSE
    ALTER ROLE lis WITH LOGIN PASSWORD '${LIS_DB_PASSWORD}';
  END IF;
END
\$\$;
SQL

# CREATE DATABASE can't run inside a DO block, so do it conditionally via shell.
if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='lis'" | grep -q 1; then
  sudo -u postgres createdb -O lis lis
fi

sudo -u postgres psql -d lis -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS vector;"

echo
echo "Done. Verify with:"
echo "  PGPASSWORD='${LIS_DB_PASSWORD}' psql -h localhost -U lis -d lis -c 'SELECT extname FROM pg_extension;'"
