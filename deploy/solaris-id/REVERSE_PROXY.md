# HTTPS reverse proxy

Подготовлен вариант **HAProxy 3.2 → Kestrel HTTPS на loopback**. Он дополняет
прямой Kestrel HTTPS из README; одновременно публиковать оба входа не следует.
Шаблон: [haproxy.cfg.example](haproxy.cfg.example). Работающие серверы он не меняет.

1. Выделить auth-домен, установить поддерживаемый HAProxy и доверенный сертификат.
   `frontend.pem` содержит цепочку сертификата и приватный ключ, хранится вне Git,
   читается только root/службой proxy. Настроить обновление сертификата и reload.
2. Заменить **все** `auth.example.invalid`, проверить пути и доверенную CA upstream.
   Для публичного сертификата подходит системный CA bundle; для внутреннего PKI
   указать проверенный CA. Требуются совпадение hostname/SNI и `verify required`.
3. В API оставить `ASPNETCORE_ENVIRONMENT=Production`, установить
   `ASPNETCORE_URLS=https://127.0.0.1:8443` и правильный внешний
   `SOLARIS_ID_PUBLIC_ORIGIN=https://ВАШ_AUTH_ДОМЕН`. Использовать постоянный Data
   Protection key ring, PostgreSQL/SMTP из защищённого env. Kestrel получает
   соответствующий upstream TLS-сертификат. Не включать DEV HTTP override.
4. API и proxy в этом шаблоне находятся **на одном хосте**. Стандартная обработка
   ASP.NET доверяет loopback proxy. Для отдельного контейнера/хоста это предположение
   неверно: сначала настроить конкретный trusted proxy и private firewall, затем
   повторить проверки. Не очищать KnownNetworks/KnownProxies и не открывать 8443
   наружу. Проверка из этой среды не подтверждает удалённую Docker-топологию.
5. Проверить `haproxy -c -f /ПУТЬ/haproxy.cfg` до запуска. Установить штатную
   службу с минимальными правами; проверить права сертификатов, restart/reload,
   мониторинг `/health` и ошибок upstream. На Windows/Linux рабочих игроков
   ничего для этого автоматически не устанавливается.

Proxy удаляет входящие forwarded/original заголовки и устанавливает собственные
IP/HTTPS/Host. Чужой Host отклоняется, plaintext HTTP отвечает 400 даже с
`X-Forwarded-Proto: https`. Access/error события остаются видимыми; формат
request log содержит status/duration/termination/backend, **не** URL/query,
Authorization, cookies или body. Не включать debug/header captures/HTTP traces:
URL подтверждения email содержит секрет. Это относится и к внешнему CDN/WAF,
журналам хостинга и мониторингу, которые здесь не настраиваются.

`test_reverse_proxy.py` запускает настоящий HAProxy в отдельном контейнере и
Production API с проверкой обоих TLS-соединений. Проверяются health, заголовки,
Host/forwarded spoofing, rate-limit, неверный upstream hostname, API outage,
ошибки HTTP framing и отсутствие canary-секретов в сохранённых журналах. Образ
тестового proxy закреплён digest; это не политика автоматических обновлений
production. Публичный домен/сертификат, firewall, сертификатное renewal и
доступность из внешней сети требуют приёмки владельцем.
