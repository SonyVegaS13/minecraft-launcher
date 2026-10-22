# План контролируемого внедрения — обновление не опубликовано

Исходная версия лаунчера остаётся **2.2.15**. Это кандидат из `feature/solaris-id`,
а не очередное автоматическое обновление. Version/channel, updater, release workflow,
модпак, клиентские каталоги и рабочие адреса не изменяются этим планом.

## Матрица совместимости

| Часть | Требование | Состояние |
| --- | --- | --- |
| Solaris ID API | .NET 8, PostgreSQL 16, `/api/v1`, постоянный key ring, HTTPS | Реальные локальные проверки; внешний хост не развёрнут |
| Существующий Vanilla | Minecraft 26.2 / Java 25 | Сохранён; **несовместим** с Paper 1.20.1 |
| Отдельный добровольный Vanilla | Minecraft 1.20.1 / Java 17, отдельный каталог | Код собирается; установка/запуск и Windows UI требуют приёмки |
| Paper bridge | Paper API 1.20.1, Java 17, bridge 1.0.0 | Сборка и unit/lifecycle проверки; настоящий join не выполнен |
| Modded / Forge bridge | Minecraft 1.20.1 / Forge **47.4.20**, Java 17, bridge 0.1.0 | Сборка/refmap; реальный установленный модпак не проверен и не пересобирался |
| Клиентская авторизация | Существующий offline transport + ручной HTTPS code approval | Ник/offline UUID не являются доказательством; автоматического handshake нет |
| Minecraft skins / inventory | Постоянный Solaris ID → игровые данные | Отдельная интеграция ещё не реализована; WPF-скин её не заменяет |

## Последовательность решения о выпуске

1. Владелец выбирает отдельные staging-хост/БД, домен, доверенный TLS и SMTP sender.
   Пароли и server keys передаются через защищённые настройки/vault, не чат/Git.
   Для удалённой PostgreSQL использовать проверку сертификата и hostname
   (`SSL Mode=VerifyFull` с доверенной CA), private firewall и ограниченную роль;
   локальная loopback-БД — отдельная топология, не доказательство защищённого remote DB.
2. Проверить backup/restore и сценарий отказа по [BACKUP_ROLLBACK.md](BACKUP_ROLLBACK.md).
   Проверить четыре migration IDs. Production-миграция — отдельное разрешённое
   действие после backup; не выполнять `Down`, `EnsureCreated` или очистку аккаунтов.
3. Поднять staging API/SMTP и выбранный прямой HTTPS или [reverse proxy](REVERSE_PROXY.md).
   Проверить внешний сертификат/renewal, firewall, смену ключей, доставку писем,
   sender DNS (SPF/DKIM/DMARC по требованиям почтового провайдера) и health/alerts.
4. Создать подтверждённый контрольный аккаунт, настроить TOTP, назначить первого
   администратора только по UUID штатным bootstrap. Пройти API и Control приёмку.
5. Подготовить отдельные [серверные песочницы](../../bridges/examples/README.md).
   Пройти [ACCEPTANCE.md](../../docs/solaris-id/ACCEPTANCE.md) на Windows и на обоих
   настоящих серверах, включая точный комплект допустимых модов/плагинов.
6. Выбрать release version/channel **после** приёмки. Подготовить несекретный
   `solaris-id.json` с реальным origin/audience; серверные ключи в клиент не включать.
   Сначала опубликовать согласованные API/bridge в контролируемом окне, убедиться,
   что недоступный API закрывает доступ, затем отдельно разрешать клиентский rollout.
   Старые stateless cloud bearer потребуют нового входа; локальные аккаунты/данные сохраняются.
7. Проверить финальные binary SHA-256 и исходный commit, approved art, версии,
   подпись/издателя Windows при наличии сертификата и отсутствие секретов/кешей.
   Повторить критические проверки именно на этих binary. Только затем владелец
   отдельно разрешает release/update manifest и установку на рабочие серверы.

## Состав проверяемого кандидата

Собирать штатными командами из README в **новые** каталоги вне checkout:
backend publish, `win-x64` self-contained publish и два bridge JAR. Не включать
`.git`, `bin/obj` исходного проекта, Gradle caches/run worlds, SMTP mailbox,
`integration-env.json`, DB dumps, `.env`, TLS/DPAPI/Data Protection/server keys,
пользовательский `%APPDATA%`, токены и сырые логи. Source bundle должен пройти
отдельную проверку состава; упаковка всего рабочего каталога недопустима.

Для каждого фактически собранного файла записать имя, точный размер, SHA-256,
source commit, SDK/JDK/Gradle, schema IDs и runtime requirements. Проверить JAR/ZIP
через `unzip -t` или `ZipFile.testzip()`, структуру Paper plugin/Forge mixins, PE x64
у лаунчера. Команды владельцу:

```powershell
Get-FileHash .\SolarisLauncher.exe -Algorithm SHA256
Get-FileHash .\solaris-id-paper-1.0.0.jar -Algorithm SHA256
Get-FileHash .\solaris-id-forge-1.20.1-0.1.0.jar -Algorithm SHA256
```

Текущие обезличенные результаты сборки/хеши лежат в
`docs/solaris-id/verification/`; они относятся к указанным там build, а не ко
всякой будущей пересборке. Хеш подтверждает совпадение файлов, не подпись издателя.
Существующий updater не изменяется ради Solaris ID. Не запускать candidate из
каталога установленного лаунчера и не перезаписывать его EXE.

## GO / NO-GO

Утечка токенов, возможность получить чужой Solaris ID, снятие карантина без
подтверждения, повреждение данных, обход MFA или поломка запуска — **NO-GO**.
Для публичного игрового доступа отдельно решить риски offline transport,
pre-auth playerdata, duplicate-nickname DoS и сторонних модов: HTTPS-мост сам по
себе не устраняет их. До решения владельца использовать только закрытый стенд.

Непроверенные Windows/Minecraft/external SMTP/backup-hosting сценарии помечать
«НЕ ВЫПОЛНЕНО», не PASS по аналогии. При сбое закрыть новые подключения, сохранить
диагностику без секретов и действовать по плану восстановления; не включать
временный доступ «по нику». Автоматического выпуска этим документом не разрешено.
