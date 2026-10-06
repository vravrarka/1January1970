#!/bin/bash
# Создаёт отдельную базу данных и отдельного пользователя для каждого микросервиса
# (database-per-service). Сервис имеет права только на свою базу.
# Скрипт выполняется образом postgres один раз, при первой инициализации тома.
set -euo pipefail

create_service_db() {
  local db="$1" user="$2" password="$3"
  if [ -z "$password" ]; then
    echo "Не задан пароль для пользователя $user" >&2
    exit 1
  fi

  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
       -v db="$db" -v usr="$user" -v pwd="$password" <<-'EOSQL'
	CREATE ROLE :"usr" LOGIN PASSWORD :'pwd';
	CREATE DATABASE :"db" OWNER :"usr";
	REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
EOSQL
  echo "Создана база $db для пользователя $user"
}

create_service_db auth_db    auth_svc    "${AUTH_DB_PASSWORD:-}"
create_service_db device_db  device_svc  "${DEVICE_DB_PASSWORD:-}"
create_service_db request_db request_svc "${REQUEST_DB_PASSWORD:-}"
create_service_db audit_db   audit_svc   "${AUDIT_DB_PASSWORD:-}"
