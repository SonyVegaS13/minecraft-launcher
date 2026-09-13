# Solaris Launcher

Современный WPF-лаунчер Solaris для Minecraft.

## Что уже есть

- Локальная регистрация и вход.
- Пароль хранится как PBKDF2-SHA256 hash + случайная salt.
- Профиль игрока и ник из логина.
- Minecraft 1.20.1 + Forge 47.4.20 для модового режима.
- Minecraft Vanilla 26.2 в отдельной папке.
- Автоматическая установка Minecraft, библиотек и Java через CmlLib.
- Автоподключение Vanilla 26.2 к `solarisplay.millida.host:25565`.
- Автоматическое обновление клиентской модовой сборки через `SolarisClient.zip` из GitHub Releases.
- Настройка RAM: 2048–4096 MB.

## Режимы

- **SOLARIS MODDED** — Minecraft 1.20.1 + Forge 47.4.20.
- **SOLARIS VANILLA** — Minecraft 26.2 + автоматическое подключение к Solaris Vanilla серверу.

## Важно

Локальная регистрация аккаунта пока работает только на конкретном ПК и не является серверной авторизацией.

Серверные файлы, мир и серверная конфигурация хранятся на хостинге Millida и не входят в репозиторий лаунчера.

## Сборка

Требуется .NET 8 SDK.

```powershell
dotnet clean
Remove-Item -Recurse -Force .\bin, .\obj -ErrorAction SilentlyContinue
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Готовый EXE:

`bin\Release\net8.0-windows\win-x64\publish\SolarisLauncher.exe`
