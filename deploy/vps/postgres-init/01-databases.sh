#!/bin/bash
# Single Postgres instance, three databases (lean profile D4): catalog + shipping for the
# services (Aspire runs two Postgres containers locally; one here to save RAM), keycloak for
# the IdP. Runs once on first init of an empty data volume.
set -e
for db in catalog shipping keycloak; do
  psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -tc "SELECT 1 FROM pg_database WHERE datname='$db'" | grep -q 1 \
    || psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -c "CREATE DATABASE $db"
done
