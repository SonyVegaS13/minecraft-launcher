# Реальная интеграционная проверка Solaris ID

Проверки используют ASP.NET API, PostgreSQL 16, HTTPS с проверяемым сертификатом
и SMTP с обязательными STARTTLS и AUTH. Это тестовая инфраструктура, а не
настройка рабочего сервера. Нужны Docker, OpenSSL, .NET 8, Python 3.12 и dotnet-ef 8.0.11.

Из корня репозитория:

```sh
python3 -m venv /tmp/solaris-id-tests-venv
/tmp/solaris-id-tests-venv/bin/pip install -r tests/SolarisId.Integration/requirements.txt
dotnet tool install dotnet-ef --version 8.0.11 --tool-path /tmp/solaris-id-ef
/tmp/solaris-id-tests-venv/bin/python tests/SolarisId.Integration/run_local.py --ef /tmp/solaris-id-ef/dotnet-ef
python3 tests/SolarisId.Integration/check_preserved_systems.py
dotnet run --project tests/SolarisIdClient.Tests/SolarisIdClient.Tests.csproj -c Release
```

`run_local.py` создаёт собственный контейнер, случайные учётные данные, CA и
почтовый приёмник. Выполняет 29 групп API-проверок, 7 проверок политики Production
и 10 проверок миграции старой базы. Проверяется отказ миграции при дублированных
email без удаления пользователей, затем сохранение ID/хешей и резервирование ников.
Скрипт завершает только запущенные им процессы и удаляет только свой контейнер.
Для новых запусков нужен новый `--work-dir` вне репозитория либо временный каталог по умолчанию.

Каталог результатов содержит **секреты**: пароли тестовых пользователей, токены,
письма подтверждения/сброса, ключи сертификатов и конфигурацию. Не коммитить и
не загружать весь каталог или сырые логи в Actions artifacts. В
`docs/solaris-id/verification` сохранены только проверенные обезличенные итоги.

Отдельные Java-тесты: `gradle -p bridges :core:test :paper:build` (Java 17,
Gradle 8.1.1). Forge: см. `bridges/forge/BUILD_STATUS.md`.
Тест `RealApiProbe` предназначен для отдельного запуска с изолированным API;
полная ручная проверка настоящим Minecraft-клиентом описана в `bridges/README.md`.
Локальные HTTP-моки тестов транспорта лаунчера не являются проверкой WPF/DPAPI.

Для проверки сохранности нужен Git-объект базового DEV-коммита
`efb37d9596b4c73b9d24a38372035225658e6f5b` (в CI используется `fetch-depth: 0`).
Обычный CI не публикует бинарники, релизы, теги или updater manifests.
