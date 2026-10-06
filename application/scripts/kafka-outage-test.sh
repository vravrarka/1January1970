#!/usr/bin/env bash
# Проверка SR-03 / T-03: при недоступности Kafka операции выполняются, события аудита
# сохраняются в локальном журнале сервиса (таблица audit_outbox) и доставляются
# в централизованный журнал после восстановления Kafka.
# Запускать из каталога application при поднятом стеке.

set -uo pipefail
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a

BASE_URL="https://localhost:${HTTPS_PORT:-8443}"
FAIL=0

json_post() { # path token json
  printf '%s' "$3" > /tmp/kafka_req.$$
  curl -sk -X POST "$BASE_URL$1" -H "Authorization: Bearer $2" \
       -H "Content-Type: application/json; charset=utf-8" --data-binary "@/tmp/kafka_req.$$"
}
field() { grep -o "\"$1\":\"[^\"]*\"" | head -1 | cut -d'"' -f4; }
pending_outbox() {
  docker compose exec -T postgres psql -U postgres -d device_db -tAc \
    "SELECT count(*) FROM audit_outbox WHERE published_at IS NULL" | tr -d '[:space:]'
}
check() { # condition description
  if eval "$1"; then echo "  ✔ $2"; else echo "  ✘ $2"; FAIL=$((FAIL + 1)); fi
}

TOKEN="$(json_post /auth/login "" "{\"login\":\"$ADMIN_LOGIN\",\"password\":\"$ADMIN_PASSWORD\"}" | field accessToken)"
[ -n "$TOKEN" ] || { echo "Не удалось войти как администратор"; exit 1; }

echo "== Останавливаем Kafka"
docker compose stop kafka >/dev/null 2>&1

RESPONSE="$(json_post /devices "$TOKEN" '{"name":"Устройство при отказе Kafka","type":"router"}')"
DEVICE_ID="$(printf '%s' "$RESPONSE" | field id)"
check '[ -n "$DEVICE_ID" ]' "Устройство добавлено, хотя Kafka недоступна (id=$DEVICE_ID)"

sleep 3
PENDING="$(pending_outbox)"
check '[ "${PENDING:-0}" -ge 1 ]' "Событие лежит в локальном журнале и ждёт отправки (неотправленных: $PENDING)"

echo "== Запускаем Kafka и ждём доставки"
docker compose start kafka >/dev/null 2>&1
for _ in $(seq 1 30); do
  sleep 3
  PENDING="$(pending_outbox)"
  [ "${PENDING:-1}" = "0" ] && break
done
check '[ "$PENDING" = "0" ]' "Локальный журнал выгружен в Kafka после восстановления"

DELIVERED=0
for _ in $(seq 1 20); do
  if curl -sk "$BASE_URL/audit?action=device.created&limit=50" -H "Authorization: Bearer $TOKEN" | grep -q "$DEVICE_ID"; then
    DELIVERED=1; break
  fi
  sleep 2
done
check '[ "$DELIVERED" = "1" ]' "Событие device.created появилось в централизованном журнале аудита"

rm -f /tmp/kafka_req.$$
echo "Итого ошибок: $FAIL"
[ "$FAIL" -eq 0 ]
