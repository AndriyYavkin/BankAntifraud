# Банківські перекази та антифрод-аналіз

Навчальний проєкт із дисципліни «Інформаційні системи». Система виконує перекази коштів між рахунками в ACID-транзакціях PostgreSQL, надсилає подію `TransferInitiated` у RabbitMQ, а фоновий сервіс аналізує частоту операцій і шукає шахрайство. Роботу бази даних, сервісів і брокера видно в Grafana через Prometheus, Loki і Tempo. Доступ лише для авторизованих користувачів.

Стек: C# (.NET 8, ASP.NET Core), PostgreSQL 16, SQLite, RabbitMQ 4, Prometheus, Alertmanager, Grafana Loki + Alloy, Grafana Tempo (OpenTelemetry), Grafana, Docker Compose.

## Команда

Студенти 4 курсу, група ІПС-42.

| Учасник | Роль | Зона відповідальності |
|---|---|---|
| Явкін Андрій | бекенд і база даних | схема PostgreSQL, transfer-service (транзакції, блокування, рівні ізоляції), RabbitMQ, fraud-worker |
| Криворучко Кирило | моніторинг | Prometheus і алерти, Alertmanager, дашборд Grafana, Loki + Alloy, Tempo + OpenTelemetry |
| Марченко Данил | інфраструктура, безпека, тестування | Docker Compose і Dockerfile, auth-service (SQLite, JWT, ролі), load-generator, тестування |

## Запуск

Потрібен лише запущений Docker Desktop (Windows 11, WSL2). Команди виконуй у PowerShell у теці проєкту.

```
docker compose up -d --build
```

Перший запуск триває 3–6 хвилин: Docker завантажує образи та збирає чотири сервіси на C#. Перевірка стану:

```
docker compose ps
```

Усі сервіси мають бути в стані `running`, а `postgres` і `rabbitmq` у стані `healthy`.

## Генерація навантаження

Генератор навантаження теж написаний на C# і запускається як контейнер. Він сам входить у систему як `operator`.

```
docker compose run --rm load-generator normal --count 50
docker compose run --rm load-generator fraud
docker compose run --rm load-generator deadlock --count 10
docker compose run --rm load-generator lost-update --count 10
docker compose run --rm load-generator invalid --count 10
docker compose run --rm load-generator unauthorized --count 20
docker compose run --rm load-generator seed --count 10
docker compose run --rm load-generator mixed --duration 180
docker compose run --rm load-generator help
```

| Сценарій | Що робить |
|---|---|
| `normal` | звичайні перекази між клієнтськими рахунками |
| `fraud` | серія переказів з одного рахунку на різні, «пінг-понг» між двома рахунками, переказ майже всього балансу; рахунок блокується |
| `deadlock` | одночасні зустрічні перекази A→B і B→A між розрахунковими рахунками |
| `lost-update` | одночасні перекази з одного рахунку без блокування рядків |
| `invalid` | недостатньо коштів, неіснуючий рахунок, той самий рахунок, від'ємна сума, різні валюти |
| `unauthorized` | запити без токена, з підробленим токеном, неправильний пароль, переказ від імені аналітика |
| `seed` | відкриває нові рахунки (актуалізація даних) |
| `mixed` | усе разом протягом заданого часу |

Параметри `--isolation read-committed|repeatable-read|serializable`, `--lock-mode ordered|naive|none`, `--hold-ms N`, `--retries N` дозволяють порівнювати режими транзакцій без перезапуску сервісів.

## Де дивитись результат

| Що | Адреса | Вхід |
|---|---|---|
| Grafana, дашборд «Bank Transfers & Anti-Fraud» | http://localhost:3000 | `admin` / `bank_grafana` |
| RabbitMQ | http://localhost:15672 | `bank` / `bank_rabbit_pass` |
| Prometheus, цілі збору | http://localhost:9090/targets | без пароля |
| Prometheus, алерти | http://localhost:9090/alerts | без пароля |
| Alertmanager | http://localhost:9093 | без пароля |
| API переказів | http://localhost:8000 | токен JWT |
| Сервер авторизації | http://localhost:8002 | див. нижче |
| PostgreSQL | `localhost:5433`, база `bank` | `bank_admin` / `bank_admin_pass` |

Користувачі сервера авторизації:

| Логін | Пароль | Роль | Може |
|---|---|---|---|
| `operator` | `operator_pass` | operator | перекази, відкриття рахунків, перегляд |
| `analyst` | `analyst_pass` | analyst | перегляд, антифрод-алерти |
| `admin` | `admin_pass_123` | admin | усе, реєстрація користувачів, розблокування рахунків |

Паролі навчальні та призначені лише для локального запуску. Усі порти доступні лише з цього комп'ютера (`127.0.0.1`).

Логи та трейси: Grafana → **Explore** → джерело **Loki** або **Tempo**.

- Логи антифроду: `{service="fraud-worker", level="Warning"}`
- Deadlock-и в журналі PostgreSQL: `{service="postgres"} |= "deadlock"`
- Трейси переказів: `{resource.service.name="transfer-service"}`
- Повільні або невдалі перекази: `{resource.service.name="transfer-service" && (status=error || duration > 500ms)}`

З рядка логу можна перейти до трейсу за `trace_id`, а з трейсу до логів.

## Авторизація вручну

```
$t = (Invoke-RestMethod -Method Post -Uri http://localhost:8002/login -ContentType 'application/json' -Body '{"username":"operator","password":"operator_pass"}').access_token
$h = @{ Authorization = "Bearer $t" }

Invoke-RestMethod -Uri http://localhost:8000/api/accounts?limit=10 -Headers $h

Invoke-RestMethod -Method Post -Uri http://localhost:8000/api/transfers -Headers $h -ContentType 'application/json' -Body '{"from_account_id":7,"to_account_id":8,"amount":150.25,"reference":"rent"}'

Invoke-RestMethod -Uri http://localhost:8000/api/stats -Headers $h
```

Те саме для macOS / Linux (bash або zsh):

```
T=$(curl -s -X POST http://localhost:8002/login -H 'Content-Type: application/json' -d '{"username":"operator","password":"operator_pass"}' | python3 -c 'import sys,json; print(json.load(sys.stdin)["access_token"])')

curl -s "http://localhost:8000/api/accounts?limit=10" -H "Authorization: Bearer $T"

curl -s -X POST http://localhost:8000/api/transfers -H "Authorization: Bearer $T" -H 'Content-Type: application/json' -d '{"from_account_id":7,"to_account_id":8,"amount":150.25,"reference":"rent"}'

curl -s http://localhost:8000/api/stats -H "Authorization: Bearer $T"
```

Перегляд даних у базі:

```
docker compose exec postgres psql -U bank_admin -d bank -c "select id, owner_name, balance, is_blocked from accounts order by id limit 15"
docker compose exec postgres psql -U bank_admin -d bank -c "select fraud_status, count(*) from transfers group by 1"
docker compose exec postgres psql -U bank_admin -d bank -c "select rule, count(*) from fraud_alerts group by 1"
```

## Демонстрація на захисті

1. Відкрий Grafana, увійди як `admin`. Без входу дашборд недоступний.
2. Запусти фоновий трафік в окремому вікні PowerShell: `docker compose run --rm load-generator mixed --duration 600`
3. **Deadlock.** `docker compose run --rm load-generator deadlock --count 10`. Половина пар отримує 409 `deadlock_detected`. На дашборді ростуть «Conflicts per minute» і «PostgreSQL deadlocks», p95 тривалості транзакції близько 1 с (`deadlock_timeout`). У Loki: `{service="postgres"} |= "deadlock"` показує, які процеси чекали один на одного. Через хвилину алерти `DeadlocksDetected` і `PostgresDeadlocks`.
4. **Виправлення deadlock.** `docker compose run --rm load-generator deadlock --count 10 --lock-mode ordered`. Deadlock-ів немає, бо рахунки блокуються в порядку зростання id. Варіант із повторами: `--lock-mode naive --retries 3`.
5. **Втрачене оновлення.** `docker compose run --rm load-generator lost-update --count 10`. Без `FOR UPDATE` у `READ COMMITTED` сума всіх грошей у банку зростає, генератор пише `INCONSISTENT`, спрацьовує алерт `TotalBalanceChanged`.
6. **SERIALIZABLE.** `docker compose run --rm load-generator lost-update --count 10 --isolation serializable`. Сума не змінюється, більшість транзакцій отримують 409 `serialization_failure` (SQLSTATE 40001).
7. **Шахрайство.** `docker compose run --rm load-generator fraud`. На дашборді «Triggered rules», у логах `Suspicious transfer ...`, рахунок блокується, наступний переказ отримує 403 `account_blocked`. Розблокувати може лише `admin`: `POST /api/accounts/{id}/unblock`.
8. **Помилки валідації балансу.** `docker compose run --rm load-generator invalid --count 20`, панель «Balance validation errors».
9. **Безпека.** `docker compose run --rm load-generator unauthorized --count 40`, панелі «Denied API requests» і «Login attempts», алерти `UnauthorizedRequestsSpike` і `LoginFailuresSpike`.
10. **Незалежність сервісів.** `docker compose stop fraud-worker`, потім `normal --count 30`: перекази виконуються, у черзі `transfers.fraud-check` накопичуються події. `docker compose start fraud-worker`: черга спадає, усі перекази перевірені.
11. **Трейс.** У Tempo відкрий трейс `POST /api/transfers`: видно HTTP-запит, SQL-команди з `FOR UPDATE`, публікацію в RabbitMQ і обробку у fraud-worker в одному трейсі.

## Зупинка

```
docker compose down
```

Дані зберігаються в томах Docker. Щоб видалити і їх (почати з чистої бази): `docker compose down -v`.

## Структура

| Тека | Що всередині |
|---|---|
| `docs` | вимоги, архітектура, діаграми, тести, контракт сервісів, трасування, аналіз, звіт |
| `services/auth-service` | сервер авторизації: C#, SQLite, JWT |
| `services/transfer-service` | API рахунків і переказів, ACID-транзакції, публікація подій |
| `services/fraud-worker` | аналіз подій `TransferInitiated`, правила антифроду, блокування рахунків |
| `services/load-generator` | генерація та актуалізація даних |
| `postgres` | схема бази, ролі з мінімальними правами, початкові рахунки |
| `rabbitmq` | користувачі, virtual host, exchange, черги та зв'язки |
| `prometheus` | збір метрик і правила алертів |
| `alertmanager` | обробка алертів |
| `loki`, `alloy` | зберігання та збір логів |
| `tempo` | зберігання трейсів і граф сервісів |
| `grafana` | джерела даних і готовий дашборд |
| `BankAntifraud.sln` | рішення для Visual Studio або Rider |

## Якщо щось не запускається

| Проблема | Що зробити |
|---|---|
| `port is already allocated` | закрий програму, що займає порт (наприклад, локальний PostgreSQL або Grafana), або зміни лівий номер порту в `docker-compose.yml` |
| сервіс `transfer-service` перезапускається | `docker compose logs transfer-service`; найчастіше PostgreSQL ще ініціалізується, сервіс сам підніметься |
| порожній дашборд | зачекай 15–30 с після першого навантаження, перевір http://localhost:9090/targets |
| логів немає в Loki | `docker compose logs alloy`; у Docker Desktop має бути доступний `/var/run/docker.sock` (типово так) |
| треба почати з нуля | `docker compose down -v`, потім `docker compose up -d --build` |

.
