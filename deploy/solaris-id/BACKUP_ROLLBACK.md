# Резервное копирование, восстановление и откат Solaris ID

Это инструкция для отдельного auth-хоста. Она **не выполняет** backup production,
не откатывает миграции и не изменяет Minecraft/лаунчер. Не запускать команды с
непроверенной БД. Включить maintenance, остановить API и тестовые bridge-процессы
перед переключением/восстановлением. Операции на рабочем сервере требуют разрешения владельца.

## Что сохранять и когда

До первого развёртывания, перед каждой миграцией/заменой backend и регулярно после
старта сохранять согласованный комплект:

- PostgreSQL: custom-format `pg_dump` выделенной БД, миграционную историю, версию
  PostgreSQL/extensions и схему ролей/привилегий. Не передавать пароль в URI/CLI;
  использовать защищённый `PGPASSFILE` или принятый hosting secret manager.
- Постоянный Data Protection key ring (`SOLARIS_ID_DATA_PROTECTION_KEYS`) с
  владельцем/правами. Если ключи зашифрованы внешним ключом/сертификатом, включить
  управляемый доступ к соответствующему unwrap key. Потеря ключей нарушает старые
  email-confirmation/reset links; не создавать пустой каталог вместо утраченного.
- Точный backend binary/source commit, schema/migration IDs, конфигурацию service,
  версию .NET, несекретную конфигурацию origin/audience. Секреты PostgreSQL/SMTP и
  server keys хранить в отдельном шифрованном vault, не рядом с публичным манифестом.
- Minecraft worlds/playerdata и конфигурации резервируются владельцем отдельно.
  API-backup не является резервной копией серверов. Не менять их для проверки Solaris ID.

Обычный `pg_dump` даёт согласованный снимок одной БД; для согласования с внешними
изменениями key ring и ключами сервиса требуется контролируемое окно без записей
или документированная стратегия storage snapshots. Во время backup не ротировать
ключи и не изменять схему. Каталоги 0700, файлы 0600, шифрование до внешней передачи;
проверять доступ отдельно от наличия SHA-256. Хеш подтверждает целостность, не секретность.

Для начальной эксплуатации предложенный ориентир: ежедневный dump, копия перед
изменениями, 7 дневных и 4 недельных снимка, отдельная зашифрованная off-host копия,
ежемесячная учебная проверка восстановления. Это **предложение**, не обещанные SLA.
Владелец задаёт допустимые RPO/RTO и retention; если потери за сутки недопустимы,
настроить проверенный PostgreSQL PITR/WAL-архив, а не называть ежедневный dump PITR.

Пример формы команд (пути и БД выбирает оператор, не копировать placeholders в production):

```sh
umask 077
export PGPASSFILE=/SECURE_PATH/pgpass
pg_dump --host=AUTH_DB_HOST --username=BACKUP_ROLE --dbname=SOLARIS_DB \
  --format=custom --no-owner --no-privileges --file=/SECURE_BACKUP/solaris.dump
pg_restore --list /SECURE_BACKUP/solaris.dump > /SECURE_BACKUP/solaris.contents
sha256sum /SECURE_BACKUP/solaris.dump > /SECURE_BACKUP/solaris.dump.sha256
```

`--no-owner --no-privileges` делает восстановление переносимым, но означает, что
ограниченные runtime-права назначаются **после** restore отдельным оператором.
Нельзя запускать API с superuser/DDL-ролью, только потому что restore выполнен ею.

## Учебное восстановление в новую БД

1. Проверить целевой хост, SHA-256, версию PostgreSQL (не старше несовместимого формата
   dump), достаточно места и свободное **новое** имя БД. Не использовать `--clean`,
   `DROP DATABASE` на исходной базе или автоматический restore поверх неё.
2. Создать новую пустую БД с подходящими encoding/collation. Командой
   `pg_restore --exit-on-error --single-transaction --no-owner --no-privileges`
   восстановить снимок только в неё. При ошибке не переключать API; исходная БД сохраняется.
3. Сверить все четыре текущие EF migration IDs, количество/ID аккаунтов, nickname
   reservations, skins, audit/legacy/trusted history и значения парольных колонок.
   Не печатать PasswordHash, MFA data, tokens или PII в отчёт/чат. Проверить constraints,
   уникальные email/ники, FK и список привилегий.
4. Вернуть права runtime-роли: CONNECT к нужной БД, USAGE на schema, нужные DML-права
   на таблицы и USAGE/SELECT на sequences; без SUPERUSER/CREATEDB/CREATEROLE и без
   CREATE/ownership schema. Migration role остаётся отдельной. Проверить DML и отказ DDL.
5. Не открывать восстановленный снимок во внешнюю сеть. Подключить только отдельный
   тестовый API с соответствующим key ring, отдельными audience/SMTP-данными и origin.
   Убедиться, что он не отправит реальные восстановительные письма пользователям.
6. Проверить реальный HTTPS login/refresh/logout/MFA и `/health` на **контрольном**
   аккаунте, доставку в тестовый mailbox и несколько bridge-сценариев. Сверка SQL
   строк сама по себе не подтверждает действительность пароля или сертификата.
7. Зафиксировать длительность, объём, hash, schema/source version и результат;
   удалить учебные копии по retention и правилам хранения персональных данных.

## Отзыв восстановленных сессий перед аварийным возвратом

Backup содержит состояние **на момент снимка**: ранее использованные refresh/tickets
могут снова выглядеть неиспользованными. Не разрешать внешние подключения до отзыва.
Следующая транзакция проверена на новой fixture-БД; она не запускается автоматически
и допускается только после явной проверки выбранной восстановленной БД, при
остановленных API/bridges. Она не удаляет аккаунты, парольные хеши, скины или историю:

```sql
BEGIN;
UPDATE "AspNetUsers" SET "SecurityStamp" = gen_random_uuid()::text;
UPDATE "AuthSessions" SET "RevokedUtc" = COALESCE("RevokedUtc", CURRENT_TIMESTAMP);
UPDATE "AuthRefreshTokens" SET "ConsumedUtc" = COALESCE("ConsumedUtc", CURRENT_TIMESTAMP),
  "ExpiresUtc" = LEAST("ExpiresUtc", CURRENT_TIMESTAMP);
UPDATE "SolarisGameTicket" SET "ConsumedUtc" = COALESCE("ConsumedUtc", CURRENT_TIMESTAMP),
  "ExpiresUtc" = LEAST("ExpiresUtc", CURRENT_TIMESTAMP);
UPDATE "SolarisGameConnection" SET "DeniedUtc" = COALESCE("DeniedUtc", CURRENT_TIMESTAMP);
UPDATE "SolarisTrustedGameSession" SET "ClosedUtc" = COALESCE("ClosedUtc", CURRENT_TIMESTAMP);
COMMIT;
```

Все устройства потребуют нового входа, старые Identity email/reset tokens перестанут
совпадать по SecurityStamp, игровые подключения должны создаваться заново. Сохранённые
`PlayedTicks` не начисляются повторно. Внешние server keys, SMTP и DB passwords не
отзываются этой SQL-транзакцией — их проверяет/ротирует оператор через vault и оба
конца интеграции; старые bridge-процессы должны быть остановлены.

**Граница безопасности восстановления:** снимок может вернуть старый пароль,
заблокированный позднее аккаунт, старые роли/MFA-ключи и recovery-коды, использованные
после снимка. Отзыв сессий/смена stamp **не исправляют** такой откат учётных данных и
не удаляют Identity recovery-коды. Перед открытием production оператору необходимы
проверенный журнал изменений после снимка и план восстановления блокировок/ролей,
перевыпуска паролей/MFA/recovery для затронутых владельцев. Если их нельзя восстановить
достоверно, оставлять сервис в maintenance до безопасного recovery; не считать старый
dump автоматически безопасным production rollback. Данные MFA не сбрасываются этой
инструкцией молча, администратор не получает чужие пароли или доступ к чужому OTP.

## Откат версии приложения

- До переключения сохранять предыдущий **проверенный** binary и конфигурацию, schema
  IDs и совместимость API/launcher/bridge. При сбое сначала закрыть новые подключения
  и вернуть поддерживаемый binary **без downgrade схемы**, только если эта комбинация
  отдельно проверена. Сам факт аддитивных миграций не доказывает обратную совместимость.
- Старый starter до server-side sessions и новый Solaris ID различаются форматом
  доверия к bearer/playtime: такой starter нельзя объявлять безопасным fallback.
  Два backend разной модели auth не должны одновременно принимать production трафик.
- Не выполнять `dotnet-ef database update <old migration>` для отката: `Down` удаляет
  session/game tables и данные. Если исправление можно выпустить вперёд без потери
  данных, предпочитать контролируемый roll-forward. Иначе restore в **новую** БД по
  процедуре выше, не уничтожая исходную; явно оценить потерю изменений после снимка.
- Отзыв новых устройств, потеря доступности API, stale bridge poll и истёкшие tickets
  должны закрывать доступ. Не обходить отказ временным входом «по нику».
- Автообновление лаунчера не переключается автоматически. Возврат старого launcher
  возможен только по отдельно проверенной матрице протокола; локальные аккаунты и
  `%APPDATA%/Solaris` не удаляются. Player release/update manifest не меняется этим планом.

## Что проверяет автоматизация

`tests/SolarisId.Integration/test_backup_restore.py` создаёт две собственные временные
БД в контейнере изолированного harness, применяет настоящие EF-миграции, делает
`pg_dump -Fc` / `pg_restore`, сравнивает fixtures, отзывает восстановленные proof/session
строки и проверяет ограниченную runtime-роль. Исходная fixture-БД остаётся неизменной.
SQL-маркеры **не являются** настоящим Identity login; рабочие API-пароли/MFA покрывает
отдельный HTTP-набор. Production backup, off-host encryption, PITR, потеря ключевого
хранилища, ручной Windows/Minecraft сценарий и фактическое RPO/RTO этим не проверяются.
