#!/usr/bin/env bash
# Сквозная проверка запущенного стека через шлюз (bash + curl; работает в Git Bash и Linux).
# Требует демо-данные (SEED_DEMO_DATA=true). Каждый запуск создаёт новые устройства,
# поэтому скрипт можно выполнять повторно.
#
#   ./scripts/smoke-test.sh
#   BASE_URL=https://localhost:8443 ADMIN_PASSWORD=... DEMO_PASSWORD=... ./scripts/smoke-test.sh

set -uo pipefail

ENV_FILE="$(dirname "$0")/../.env"
if [ -f "$ENV_FILE" ]; then
  # shellcheck disable=SC1090
  set -a; . "$ENV_FILE"; set +a
fi

BASE_URL="${BASE_URL:-https://localhost:${HTTPS_PORT:-8443}}"
HTTP_URL="${HTTP_URL:-http://localhost:${HTTP_PORT:-8080}}"
ADMIN_LOGIN="${ADMIN_LOGIN:-admin}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:?Задайте ADMIN_PASSWORD}"
DEMO_PASSWORD="${DEMO_PASSWORD:?Задайте DEMO_PASSWORD}"
RUN_ID="$(date +%s)"

PASS=0
FAIL=0
STATUS=""
BODY=""

# call METHOD PATH [TOKEN] [JSON]
call() {
  local method="$1" path="$2" token="${3:-}" data="${4:-}"
  local args=(-sk -o /tmp/smoke_body.$$ -w "%{http_code}" -X "$method" "$BASE_URL$path")
  [ -n "$token" ] && args+=(-H "Authorization: Bearer $token")
  if [ -n "$data" ]; then
    # Тело передаётся через файл: на Windows кириллица в аргументах curl искажается.
    printf '%s' "$data" > /tmp/smoke_req.$$
    args+=(-H "Content-Type: application/json; charset=utf-8" --data-binary "@/tmp/smoke_req.$$")
  fi
  STATUS="$(curl "${args[@]}")"
  BODY="$(cat /tmp/smoke_body.$$ 2>/dev/null)"
}

# field NAME — первое строковое значение поля NAME в последнем ответе
field() {
  printf '%s' "$BODY" | grep -o "\"$1\":\"[^\"]*\"" | head -1 | cut -d'"' -f4
}

expect() {
  local expected="$1" description="$2"
  if [ "$STATUS" = "$expected" ]; then
    PASS=$((PASS + 1)); printf '  \033[32m✔\033[0m %-70s %s\n' "$description" "$STATUS"
  else
    FAIL=$((FAIL + 1)); printf '  \033[31m✘\033[0m %-70s ожидалось %s, получено %s\n' "$description" "$expected" "$STATUS"
    printf '      %s\n' "${BODY:0:300}"
  fi
}

expect_body() {
  local needle="$1" description="$2"
  if printf '%s' "$BODY" | grep -q -- "$needle"; then
    PASS=$((PASS + 1)); printf '  \033[32m✔\033[0m %s\n' "$description"
  else
    FAIL=$((FAIL + 1)); printf '  \033[31m✘\033[0m %s (нет «%s» в ответе)\n' "$description" "$needle"
    printf '      %s\n' "${BODY:0:300}"
  fi
}

expect_no_body() {
  local needle="$1" description="$2"
  if printf '%s' "$BODY" | grep -q -- "$needle"; then
    FAIL=$((FAIL + 1)); printf '  \033[31m✘\033[0m %s (найдено «%s»)\n' "$description" "$needle"
  else
    PASS=$((PASS + 1)); printf '  \033[32m✔\033[0m %s\n' "$description"
  fi
}

login() {
  call POST /auth/login "" "{\"login\":\"$1\",\"password\":\"$2\"}"
  field accessToken
}

new_device() { # name type
  call POST /devices "$ADMIN" "{\"name\":\"$1 #$RUN_ID\",\"type\":\"$2\"}"
  field id
}

echo "Шлюз: $BASE_URL"

echo "== SR-04 / T-04: только HTTPS"
code="$(curl -s -o /dev/null -w "%{http_code}" "$HTTP_URL/devices")"
STATUS="$code"; BODY=""
expect 308 "HTTP-запрос перенаправляется на HTTPS"
location="$(curl -s -o /dev/null -w "%{redirect_url}" "$HTTP_URL/devices")"
STATUS="$( [[ "$location" == https://* ]] && echo ok || echo "$location")"
expect ok "Перенаправление ведёт на https://"

echo "== SR-01 / T-01: доступ только авторизованным"
call GET /devices
expect 403 "GET /devices без токена"
expect_body "Требуется авторизация" "Ответ содержит уведомление о необходимости авторизации"
call GET /devices "not-a-real-token"
expect 403 "GET /devices с поддельным токеном"
call POST /auth/login "" "{\"login\":\"$ADMIN_LOGIN\",\"password\":\"wrong-password\"}"
expect 401 "Вход с неверным паролем"
call GET /internal/holders/00000000-0000-0000-0000-000000000000/devices
expect 404 "Внутренние маршруты сервисов не публикуются шлюзом"

ADMIN="$(login "$ADMIN_LOGIN" "$ADMIN_PASSWORD")";  [ -n "$ADMIN" ] || { echo "Не удалось войти как $ADMIN_LOGIN"; exit 1; }
TEACHER="$(login teacher01 "$DEMO_PASSWORD")";     [ -n "$TEACHER" ] || { echo "Не удалось войти как teacher01 (SEED_DEMO_DATA=true?)"; exit 1; }
S1="$(login student01 "$DEMO_PASSWORD")"
S2="$(login student02 "$DEMO_PASSWORD")"
call GET /auth/me "$S2"
S2_ID="$(field id)"
call GET /auth/me "$S1"
S1_ID="$(field id)"
expect 200 "Вход ученика и GET /auth/me"

# Очистка состояния демо-учеников после прошлых запусков: отклоняем ожидающие заявки
# и принимаем возврат выданных устройств.
for sid in "$S1_ID" "$S2_ID"; do
  call GET "/requests?status=pending&studentId=$sid" "$TEACHER"
  for rid in $(printf '%s' "$BODY" | grep -o '"id":"[^"]*","student"' | cut -d'"' -f4); do
    call PATCH "/requests/$rid/review" "$TEACHER" '{"decision":"rejected"}'
  done
  call GET "/requests?status=approved&studentId=$sid" "$TEACHER"
  for did in $(printf '%s' "$BODY" | grep -o '"device":{"id":"[^"]*"' | cut -d'"' -f6); do
    call PATCH "/devices/$did/return" "$TEACHER"
  done
done

echo "== SR-05 / T-05: управлять аккаунтами может только администратор"
USER_BODY="{\"name\":\"Пётр Сидоров\",\"login\":\"smoke$RUN_ID\",\"role\":\"student\",\"confirmation\":\"Подтверждаю\"}"
call POST /users "$TEACHER" "$USER_BODY";  expect 403 "Учитель создаёт ученика"
call POST /users "$S1" "$USER_BODY";       expect 403 "Ученик создаёт ученика"
call POST /users "$ADMIN" "{\"name\":\"Пётр Сидоров\",\"login\":\"smoke$RUN_ID\",\"role\":\"student\"}"
expect 400 "Администратор без фразы «Подтверждаю»"
call POST /users "$ADMIN" "{\"name\":\"X\",\"login\":\"evil$RUN_ID\",\"role\":\"admin\",\"confirmation\":\"Подтверждаю\"}"
expect 400 "Через API нельзя создать администратора"
call POST /users "$ADMIN" "$USER_BODY";    expect 201 "Администратор создаёт ученика с подтверждением"
NEW_USER_ID="$(field id)"
expect_body "temporaryPassword" "Сгенерирован временный пароль"
call DELETE "/users/$NEW_USER_ID" "$TEACHER";  expect 403 "Учитель удаляет ученика"
call DELETE "/users/$NEW_USER_ID" "$ADMIN";    expect 400 "Удаление без подтверждения"
call DELETE "/users/$NEW_USER_ID?confirmation=%D0%9F%D0%BE%D0%B4%D1%82%D0%B2%D0%B5%D1%80%D0%B6%D0%B4%D0%B0%D1%8E" "$ADMIN"
expect 204 "Администратор удаляет ученика с подтверждением"
call GET "/users?role=student" "$TEACHER";     expect 200 "Учитель просматривает список учеников"
expect_no_body "\"role\":\"admin\"" "Учителю не показываются учётные записи администраторов"

echo "== Сценарий 3, SR-06 / T-06: устройства редактирует только администратор"
call POST /devices "$TEACHER" '{"name":"Hack","type":"laptop"}';  expect 403 "Учитель добавляет устройство"
call POST /devices "$S1" '{"name":"Hack","type":"laptop"}';       expect 403 "Ученик добавляет устройство"
LAPTOP_A="$(new_device "Laptop A" laptop)";   [ -n "$LAPTOP_A" ] && STATUS=201; expect 201 "Администратор добавляет ноутбук A"
LAPTOP_B="$(new_device "Laptop B" laptop)"
TABLET_C="$(new_device "Tablet C" tablet)"
CAMERA_D="$(new_device "Camera D" camera)"
TABLET_E="$(new_device "Tablet E" tablet)"
LAPTOP_F="$(new_device "Laptop F" laptop)"
call PATCH "/devices/$LAPTOP_A" "$S1" '{"status":"maintenance"}';     expect 403 "Ученик меняет статус устройства"
call PATCH "/devices/$LAPTOP_A" "$TEACHER" '{"status":"maintenance"}'; expect 403 "Учитель меняет статус устройства"
call PATCH "/devices/$LAPTOP_A" "$ADMIN" '{"status":"issued"}';        expect 400 "Статус issued нельзя установить вручную"
call PATCH "/devices/$LAPTOP_A" "$ADMIN" '{"type":1}';                 expect 400 "Числовое значение перечисления отклоняется"
call GET /devices "$S1";               expect 200 "Ученик просматривает список устройств"
call GET "/devices?status=available&type=laptop" "$TEACHER"; expect 200 "Фильтрация списка устройств"
call GET /device-types "$S1";          expect 200 "Список типов устройств"

echo "== Сценарий 1, SR-08: создание заявок"
call POST /requests "$TEACHER" "{\"deviceId\":\"$LAPTOP_A\"}";  expect 403 "Учитель создаёт заявку"
call POST /requests "$S1" "{\"deviceId\":\"$LAPTOP_A\",\"comment\":\"Для проекта\"}"; expect 201 "student01: заявка на ноутбук A"
REQ_A="$(field id)"
call POST /requests "$S1" "{\"deviceId\":\"$LAPTOP_A\"}";  expect 409 "Повторная заявка на то же устройство"
call POST /requests "$S1" "{\"deviceId\":\"$LAPTOP_B\"}";  expect 409 "Заявка на второй ноутбук (тот же тип)"
call POST /requests "$S1" "{\"deviceId\":\"$TABLET_C\"}";  expect 201 "student01: заявка на планшет C"
REQ_C="$(field id)"
call POST /requests "$S1" "{\"deviceId\":\"$CAMERA_D\"}";  expect 409 "Третья заявка сверх лимита в 2 устройства"
expect_body "device_limit_reached" "Код ошибки device_limit_reached"

echo "== SR-07 / T-07: рецензирует только учитель"
call PATCH "/requests/$REQ_A/review" "$S1" '{"decision":"approved"}';    expect 403 "Ученик одобряет свою заявку"
call PATCH "/requests/$REQ_A/review" "$ADMIN" '{"decision":"approved"}'; expect 403 "Администратор одобряет заявку"
call PATCH "/requests/$REQ_A/review" "$TEACHER" '{"decision":"maybe"}'; expect 400 "Недопустимое решение"
call PATCH "/requests/$REQ_A/review" "$TEACHER" '{"decision":"approved"}'; expect 200 "Учитель одобряет заявку A"
expect_body "\"status\":\"approved\"" "Заявка A в статусе approved"
call PATCH "/requests/$REQ_A/review" "$TEACHER" '{"decision":"rejected"}'; expect 409 "Повторное рецензирование"
call GET "/devices/$LAPTOP_A" "$S1"
expect_body "\"status\":\"issued\"" "Ноутбук A выдан"
expect_body "$S1_ID" "Владелец ноутбука A — student01"
call PATCH "/requests/$REQ_C/review" "$TEACHER" '{"decision":"approved"}'; expect 200 "Учитель одобряет заявку C"
call POST /requests "$S1" "{\"deviceId\":\"$CAMERA_D\"}";  expect 409 "С двумя устройствами новую заявку создать нельзя"
call PATCH "/devices/$LAPTOP_A" "$ADMIN" '{"status":"maintenance"}'; expect 409 "Нельзя сменить статус выданного устройства"

echo "== D-02 / T-08: повторная проверка ограничения при одобрении"
call POST /requests "$S2" "{\"deviceId\":\"$LAPTOP_F\"}";  expect 201 "student02: заявка на ноутбук F"
REQ_F="$(field id)"
call POST /requests "$S2" "{\"deviceId\":\"$TABLET_E\"}";  expect 201 "student02: заявка на планшет E"
REQ_E="$(field id)"
call PATCH "/devices/$TABLET_E" "$ADMIN" '{"type":"laptop"}'; expect 200 "Администратор меняет тип устройства E на laptop"
call PATCH "/requests/$REQ_F/review" "$TEACHER" '{"decision":"approved"}'; expect 200 "Одобрение ноутбука F"
call PATCH "/requests/$REQ_E/review" "$TEACHER" '{"decision":"approved"}'; expect 409 "Одобрение второго ноутбука отклонено при выдаче"
expect_body "duplicate_device_type" "Код ошибки duplicate_device_type"
call GET "/requests/$REQ_E" "$TEACHER"
expect_body "\"status\":\"pending\"" "Заявка E осталась pending, состояние не изменилось"
call GET "/devices/$TABLET_E" "$TEACHER"
expect_body "\"status\":\"available\"" "Устройство E не выдано"
call PATCH "/requests/$REQ_E/review" "$TEACHER" '{"decision":"rejected","comment":"Уже есть ноутбук"}'; expect 200 "Учитель отклоняет заявку E"

echo "== SR-09 / T-09: ученик видит только свои заявки"
call GET /requests/my "$S1";  expect 200 "student01: GET /requests/my"
expect_body "$REQ_A" "В списке student01 есть его заявка"
expect_no_body "$REQ_F" "В списке student01 нет заявок student02"
call GET "/requests/$REQ_F" "$S1";  expect 403 "student01 открывает заявку student02"
call GET "/requests/$REQ_A" "$S1";  expect 200 "student01 открывает свою заявку"
expect_body "Мария Петровна" "В заявке указан учитель-рецензент"
call GET /requests "$S1";           expect 403 "Ученик запрашивает список всех заявок"
call GET "/requests?status=pending" "$TEACHER"; expect 200 "Учитель просматривает ожидающие заявки"

echo "== Сценарий 2: возврат устройства"
call PATCH "/devices/$LAPTOP_A/return" "$S1";    expect 403 "Ученик подтверждает возврат"
call PATCH "/devices/$LAPTOP_A/return" "$ADMIN"; expect 403 "Администратор подтверждает возврат"
call PATCH "/devices/$LAPTOP_A/return" "$TEACHER"; expect 200 "Учитель подтверждает возврат ноутбука A"
expect_body "\"status\":\"returned\"" "Заявка A закрыта со статусом returned"
call GET "/devices/$LAPTOP_A" "$S1"
expect_body "\"status\":\"available\"" "Ноутбук A снова доступен"
expect_body "\"inStorage\":true" "Ноутбук A на хранении"
call PATCH "/devices/$LAPTOP_A/return" "$TEACHER"; expect 409 "Повторный возврат"
call POST /requests "$S1" "{\"deviceId\":\"$CAMERA_D\"}"; expect 201 "После возврата student01 снова может подать заявку"
for dev in "$TABLET_C" "$LAPTOP_F"; do call PATCH "/devices/$dev/return" "$TEACHER"; done
expect 200 "Возврат остальных выданных устройств"

echo "== SR-02 / SR-03: журнал аудита"
call GET /audit "$TEACHER"; expect 403 "Учитель читает журнал аудита"
sleep 6
call GET "/audit?limit=500" "$ADMIN"; expect 200 "Администратор читает журнал аудита"
for action in auth.login user.created user.deleted device.created request.created request.approved request.rejected device.issued device.returned request.return_confirmed access.denied; do
  expect_body "\"action\":\"$action\"" "В журнале есть $action"
done
call GET /audit/warnings "$ADMIN"; expect 200 "Журнал предупреждений"
expect_body "role_violation" "Попытки превышения полномочий помечены предупреждением"

echo
echo "Итого: пройдено $PASS, не пройдено $FAIL"
rm -f /tmp/smoke_body.$$ /tmp/smoke_req.$$
[ "$FAIL" -eq 0 ]
