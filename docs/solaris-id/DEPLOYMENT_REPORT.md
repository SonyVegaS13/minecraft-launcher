# Solaris ID — подготовка к контролируемому развёртыванию

Дата: 22 октября 2026. Ветка: `feature/solaris-id`. Начальная точка этого этапа:
`baa5612a2384c79910ca116fea4b761e27477b83`, чистый checkout. Предыдущая работа,
посторонний stash и исходная DEV-ветка сохранены. Production-окружение не подключалось.

## Аудит: что уже работает

Продолжена реализация из [предыдущего отчёта](REPORT.md): ASP.NET Identity/
PostgreSQL, четыре аддитивные миграции, подтверждение email/сброс, отзывные device
sessions и одноразовый refresh, MFA/Control, профиль/скин, доверенное игровое время,
WPF-клиент и серверные Paper/Forge-мосты. Эти компоненты не создавались заново.

API и клиентский протокол уже имели автоматические проверки. Не хватало
проверенного reverse proxy, учебного восстановления backup, теста сохранности
сессий между настоящими перезапусками и подробной ручной приёмки/плана выпуска.
Обнаружен и исправлен дополнительный дефект Paper identity hook.

## Исправления и добавления

- **Paper:** старый `Player` с повторно используемым Minecraft UUID мог получить
  Solaris ID нового подключения через публичный hook. Теперь проверяются точный
  живой объект и состояние после чтения coordinator. Старые quit/disconnect и
  поздние callbacks не очищают новое подключение. Добавлено 7 регрессионных
  сценариев, включая конкурентную замену, равные по UUID разные объекты и shutdown.
  Коммит: [`192126b`](https://github.com/SonyVegaS13/minecraft-launcher/commit/192126b).
- **HTTPS proxy:** [HAProxy](../../deploy/solaris-id/REVERSE_PROXY.md) с проверкой
  frontend и upstream TLS, private loopback Kestrel, заменой forwarded-заголовков,
  запретом чужого Host/plaintext и журналами без сырых URI/query/header/body.
  Ошибки upstream остаются видимыми; TLS verification не отключается.
- **PostgreSQL:** [план backup/restore/отката](../../deploy/solaris-id/BACKUP_ROLLBACK.md)
  и настоящий `pg_dump -Fc` → `pg_restore` в новую пустую тестовую БД. Проверены
  данные/миграции, отзыв восстановленных credentials и SQL-права runtime-роли.
  Production Down/restore/удаление пользователей не выполняются.
- **Сессии:** новый тест трёх Production API starts с теми же БД/key ring,
  двумя устройствами, сохранённым refresh, ссылкой подтверждения, logout и outage.
- **Стенды:** [отдельные Paper/Forge-конфигурации](../../bridges/examples/README.md)
  с loopback, новыми мирами, отдельными audience, без секретов и с `eula=false`.
  Конфигурации не копировались на рабочие серверы; EULA автоматически не принималась.
- **Приёмка и выпуск:** [Windows/Minecraft checklist](ACCEPTANCE.md) и
  [план обновления](../../deploy/solaris-id/RELEASE_PLAN.md), совместимость версий,
  список исключаемых файлов и критерии GO/NO-GO. Version/channel/updater не менялись.

## Результаты текущего запуска

Все шесть наборов прошли на **новой** изолированной PostgreSQL/SMTP/HTTPS fixture:

| Набор | Результат | Что подтверждено |
| --- | --- | --- |
| API/безопасность | 29 групп, 0 failures/errors/skips | Реальные регистрация/почта/вход/MFA/сессии/скин/Control/tickets/статистика |
| Production-политика | 7/7 | HTTPS, ключи, rate-limit, недоступная БД, безопасные логи |
| Миграция старой БД | 10/10 | Предварительный отказ на дубликатах без потери данных; upgrade/backfill |
| Backup/restore | 23/23 | Настоящие dump/restore, сохранность SQL fixtures, отзыв восстановленного состояния, DML без DDL |
| HAProxy → Production API | 11/11 | Обе TLS-цепочки/hostname, Host/forwarded spoofing, auth denial, rate-limit, outage, отсутствие canary-секретов |
| Сессии между перезапусками | 46/46 | 3 API starts, доступ/refresh, key ring/email links, независимый logout, отказ при outage |
| Java | 38/38 | 31 shared-core/TLS + 7 новых Paper binding; выполнены повторно, без skips |
| C# transport/configuration | 20 сценариев, 221 проверка | Refresh/replay/logout/expiry/error-paths; scripted HTTP |
| Paper / Forge | Сборки успешны | Реальные API, Forge reobfuscation и 39+2 refmap entries |
| Launcher | Release win-x64 publish успешен на Linux и в Windows CI | Настоящий Windows PE AMD64, проверка состава кандидата; интерактивный запуск не выполнен |
| Обратная совместимость | 50/50 protected files без изменений | Updater/install/vendor/release config и version/channel/dependencies сохранены |

Доказательства: [API](verification/deployment/tests-results.json),
[Production](verification/deployment/production-policy-results.json),
[миграции](verification/deployment/migration-upgrade-results.json),
[backup](verification/deployment/backup-restore-results.json),
[proxy](verification/deployment/reverse-proxy-results.json),
[restart](verification/deployment/session-restart-results.json),
[Java](verification/deployment/java-results.json),
[клиент](verification/deployment/client-results.json),
[сохранность](verification/deployment/preservation-results.json),
[бинарники/размеры/SHA-256](verification/deployment/build-review.json).
Сырые письма, ключи, DB dumps, пароли и логи в Git не включены.

Проверка framing первоначально ожидала HTTP400, но реальный HAProxy отклоняет
конфликтующие Content-Length немедленным закрытием соединения (в журнале status400).
Тест учитывает оба корректных отказа и не принимает нормальный ответ маршрута.
При упаковочной проверке обнаружен лишний vendor PDB; чистый кандидат опубликован
в новый локальный каталог с `DebugSymbols=false/DebugType=None`, без установки.
Сохранились прежние неблокирующие предупреждения obsolete/nullability vendor/EF
и deprecated Gradle/Actions; эти защищённые компоненты не переписывались.

## Сохранение в GitHub

Ветка [feature/solaris-id](https://github.com/SonyVegaS13/minecraft-launcher/tree/feature/solaris-id):
исправление Paper — `192126b`, deployment/tests —
[`910a814`](https://github.com/SonyVegaS13/minecraft-launcher/commit/910a814).
[GitHub Actions 39762744601](https://github.com/SonyVegaS13/minecraft-launcher/actions/runs/39762744601)
**успешен по всем трём задачам**: реальные API/deployment/security тесты, Java/Paper/Forge,
Windows compile/publish/packaging и клиентские тесты. [CI receipt](verification/deployment/github-ci-results.json).
После проверенного source commit меняются только документы/результаты. Бинарные
хеши в build-review относятся к локальным review artifacts; они не выдаются за
совпадающие с отдельной CI-пересборкой. Релизы, merge и production rollout не выполнялись.

## Что не проверено и что требуется владельцу

В Cloud отсутствуют production origin, DB/SMTP bindings и операторский
`solaris-id.json`; секреты не запрашивались в чате и не подменялись тестовыми.
Домен/хост/firewall/публичный сертификат/SMTP delivery не развёрнуты.

1. Выделить staging auth-хост, домен, PostgreSQL с отдельными runtime/migration
   правами, защищённый key ring/backup vault и SMTP sender. Настроить TLS, renewal,
   внешний мониторинг и проверенную доставку писем. Нагрузочная проверка,
   DDoS-защита/квоты, certificate renewal и off-host backup/PITR на хостинге не выполнены;
   их параметры и проверку задаёт владелец. Без этого внешняя доступность
   и безопасность реальной топологии остаются непроверенными.
2. На Windows пройти W01–W16: реальный DPAPI CurrentUser, перезапуск EXE, API outage,
   local/cloud переход, скин, MFA, launcher focus, оба клиента и отмена загрузки.
   Воссоздание HTTP-клиента в Python/C# не считается перезапуском WPF или DPAPI.
3. В двух новых серверных песочницах пройти M01–M14 для каждого моста с настоящими
   игровыми клиентами и полным разрешённым комплектом плагинов/модов. Компиляция
   и HTTP-probe не заменяют карантин/пакеты/identity hook внутри Minecraft.
4. Принять решение о рисках offline transport и pre-auth playerdata. Мост проверяет
   конкретное подключение через API; ник/offline UUID не доказывают личность.
   Сам игровой offline-транспорт не зашифрован, возможны duplicate-nickname DoS
   и утечки playerdata до join gate. Публичный запуск без отдельного решения — NO-GO.
5. Утвердить RPO/RTO/retention и восстановление состояний после backup: старый dump
   может вернуть прежние пароли, баны, роли, MFA и расходованные recovery-коды.
   Отзыв сессий не исправляет такой откат credentials. Без достоверной сверки
   оставлять maintenance, а не открывать восстановленный снимок пользователям.
6. Отдельно определить Minecraft skins/inventory по постоянному ID и автоматический
   handshake. Сейчас сохраняется ручное подтверждение кода; эти будущие функции
   не выдаются за готовые. Vanilla 26.2 не подменяется Paper-клиентом 1.20.1.
7. После приёмки выбрать новую release version/channel, проверить точные binary
   hashes и отдельно разрешить rollout. Обновление, production-миграция и
   установка bridge на рабочие серверы автоматически не выполняются.

Список путей этого этапа: [DEPLOYMENT_CHANGED_FILES.txt](DEPLOYMENT_CHANGED_FILES.txt).
Итог: подготовлен проверенный комплект для **контролируемого стендового развёртывания**;
публичный production-запуск и перечисленные ручные проверки остаются открытыми.

В черновике настроек Cloud сохранены обновлённые инструкции запуска новых проверок.
Для применения в следующих сессиях черновик требуется сохранить и опубликовать
в настройках среды; это не запускает production-сервис.
