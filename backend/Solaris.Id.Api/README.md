# SOLARIS ID — серверная часть Neon 2.2.10

Статус: код ASP.NET Core 8 + PostgreSQL + ASP.NET Identity подготовлен, но НЕ развернут на Millida VDS. До запуска HTTPS API, PostgreSQL и SMTP облачные аккаунты игроков недоступны. Не выпускать в production без приёмочного теста.

## Реализовано в исходниках
- Регистрация Solaris ID по уникальному игровому нику и email; подтверждение почты через SMTP, блокировка встроенного endpoint /identity/register.
- Вход ASP.NET Identity bearer-токеном, обновление токена, восстановление пароля по email-коду, ограничения попыток входа и rate limiting.
- Локальное защищённое DPAPI-хранение сессии Windows-клиента; офлайн-кэш ранее прошедшего авторизацию игрока (ограниченный функционал).
- Cloud-сессии Vanilla и Modded: GUID для каждой записи, идемпотентная запись и защищённая очередь повтора; отдельная статистика, включая одиночные миры.
- Скины Minecraft PNG 64x64 для владельца аккаунта и синхронизация между ПК.
- Миграция истории старого локального аккаунта: клиент требует прежний пароль, сервер учитывает устойчивый ID источника, повторный запрос не удваивает часы; исходный профиль остаётся на ПК.
- SOLARIS CONTROL в лаунчере: поиск игроков, бан, снятие бана, отзыв сессий, запрос письма восстановления, журнал действий.
- Доступ к админским endpoint требует роли SolarisAdmin и claim подтверждённой MFA. Роль выдаётся ТОЛЬКО вне публичных API.
- Backend health проверяет подключение PostgreSQL; миграции InitialSolarisSchema и LegacyImports находятся в backend/Solaris.Id.Api/Migrations.

## Развёртывание на Millida
1. Арендовать ОТДЕЛЬНЫЙ VDS Millida под Solaris ID, НЕ на том же игровом процессе Minecraft. Установить поддерживаемую ОС, .NET 8 ASP.NET Core runtime, PostgreSQL и HTTPS reverse proxy (Nginx/Caddy).
2. Создать отдельные PostgreSQL-базу и роль с минимальными полномочиями. Сделать регулярные backup и проверить восстановление.
3. Настроить домен/сертификат TLS. Kestrel должен слушать только localhost, на Internet доступен только HTTPS.
4. На сервере секретно задать переменные окружения: SOLARIS_ID_CONNECTION_STRING, SOLARIS_ID_PUBLIC_ORIGIN, SOLARIS_SMTP_HOST, SOLARIS_SMTP_PORT, SOLARIS_SMTP_FROM, SOLARIS_SMTP_USER, SOLARIS_SMTP_PASSWORD. Никогда не коммитить реальные секреты.
5. Сделать backup и применить EF Core миграции. Не применять EnsureCreated в production. Порядок миграций важен.
6. Проверить реальные письма регистрации и сброса, ограничения перебора, два клиента, возврат в online после offline и идемпотентность часов.
7. Назначить администратора вне публичного API, включить 2FA и проверить SOLARIS CONTROL. Администратор не должен знать пароли игроков.
8. После приёмки выдать публичный HTTPS origin и указать его в OfficialApiOrigin файла SolarisCloudClient.cs; заново собрать Windows EXE.
9. Выполнить все tests/docs/ACCEPTANCE-TESTS-2.2.10.md (действительный путь: docs/ACCEPTANCE-TESTS-2.2.10.md).

## Применение миграций (только после backup)
Установить dotnet-ef версии 8.x:
~~~
dotnet tool install --global dotnet-ef --version 8.0.11
dotnet ef database update --project backend/Solaris.Id.Api/Solaris.Id.Api.csproj --startup-project backend/Solaris.Id.Api/Solaris.Id.Api.csproj
~~~
Команда должна видеть переменную SOLARIS_ID_CONNECTION_STRING и выполнять миграции на ЦЕЛЕВОМ сервере.

GitHub Actions Solaris ID API Build and Database Smoke Test собирает проект, применяет миграции к временной PostgreSQL и проверяет /health и запрет анонимного доступа к админ-маршрутам. Это НЕ продакшн-аудит.

## Отдельная тестовая среда Windows
Только для DEV-режима разрешается указать SOLARIS_ID_API_ORIGIN. Выполнять отдельно от рабочего клиента 2.2.9:
~~~
$env:SOLARIS_ID_API_ORIGIN = 'https://ТЕСТОВЫЙ_HTTPS_API'
& '.\SolarisLauncher.exe' --dev-test
~~~
В обычной сборке официальный origin пуст до подтверждённого развёртывания, поэтому игрок видит честное сообщение о недоступном Solaris ID и может пользоваться локальным входом.

## Предупреждения и ограничения
- GitHub Actions build != проверка на Windows с Minecraft, сетью и Millida.
- Заявленный в коде offline Minecraft session не доказывает владение Microsoft Minecraft-аккаунтом и может не подключиться к online-mode серверу.
- Время игры, передаваемое самим клиентом, нельзя считать полностью доказанным серверным событием.
- Проверка 2FA claim, принудительной инвалидизации токенов и конкурирующих загрузок часов требует отдельного E2E/security теста.
- Не вводить в чат и не выкладывать в GitHub пароли, конфиги БД, API-токены, секреты SMTP или root VDS.
- Не утверждать, что облачный Solaris ID уже работает: реальный VDS до сих пор НЕ развёрнут.
