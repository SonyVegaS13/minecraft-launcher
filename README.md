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

## Solaris Neon 2.2.5: постоянный лаунчер

1. Скачай официальный `SolarisLauncher.exe` из Neon GitHub Release и запусти его.
2. На экране входа нажми **«Установить Solaris на ПК (один раз)»**.
3. Solaris копирует себя в `%LOCALAPPDATA%\\Programs\\Solaris Launcher\\SolarisLauncher.exe`, создаёт `Solaris Launcher.lnk` на рабочем столе и в меню «Пуск», затем запускает установленную копию.
4. С этого момента запускай Solaris **через созданный ярлык**: его путь не меняется между версиями, как и иконка в свойствах ярлыка.
5. При следующем запуске лаунчер сам проверит выпуски `neon-vX.Y.Z` в GitHub Releases. Если новая версия опубликована, Solaris **спросит разрешение**, скачает EXE, сверит его SHA256 с `SHA256SUMS.txt` и после перезапуска заменит старый EXE на том же месте.

Предыдущий EXE остаётся рядом в файле `SolarisLauncher.exe.previous` для восстановления. Если обновление не скачалось или не прошло проверку, прежняя версия остаётся работоспособной. Для обновления нужен доступ к GitHub; без сети лаунчер продолжит открываться.

**Данные не переустанавливаются:** аккаунт, локальные настройки, тема и скачанный Minecraft находятся в `%APPDATA%\\Solaris`, отдельно от EXE. Загруженный пользователем ZIP оформления сохраняется и не перезаписывается при обновлении. Стили XAML, встроенный логотип/иконка и новые функции обновляются вместе с EXE.

### Каналы

- **Neon**: GitHub Releases с тегами `neon-v2.2.5`, `neon-v2.2.6` и т. д. Именно этот канал проверяет установленный Neon 2.2.5+.
- **Legacy stable 2.1.2**: отдельный `update.json` в ветке `solaris-2.1-polish`, без изменений.

### Выпуск следующего обновления

1. Закончи разработку в `solaris-2.2-dev`.
2. Обнови версию в `SolarisLauncher.csproj` (Version, FileVersion, AssemblyVersion), `MainWindow.xaml.cs` (LauncherVersion) и метку в `MainWindow.xaml`.
3. Дождись успешной тестовой сборки GitHub Actions.
4. Сделай финальный коммит в ветку с сообщением, начинающимся на `release:` (например, `release: Solaris Neon 2.2.6`).
5. Release workflow опубликует `neon-v2.2.6` и три постоянных файла: `SolarisLauncher.exe`, ZIP и `SHA256SUMS.txt`. Опубликованную версию нельзя перезаписывать под тем же номером — для изменений повышай версию.

**Замечание:** сейчас EXE не подписан сертификатом издателя, поэтому Windows SmartScreen может показывать предупреждение. Контрольная сумма подтверждает целостность скачанного файла по сравнению с релизом, но не заменяет цифровую подпись.
