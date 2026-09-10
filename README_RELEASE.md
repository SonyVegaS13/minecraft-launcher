# Solaris Launcher — Release package

## Что это

Это готовая структура репозитория для Solaris Launcher с GitHub Actions.

CI автоматически проверяет сборку на push/pull request.
Release workflow собирает self-contained Windows x64 single-file `SolarisLauncher.exe` и создаёт GitHub Release при публикации тега вида `v1.0.0`.

Официальная документация .NET подтверждает single-file/self-contained публикацию через `dotnet publish -r win-x64`; такой файл содержит runtime и не требует отдельной установки .NET на ПК игрока.

## Структура

```text
SolarisLauncher/
├── .github/
│   └── workflows/
│       ├── ci.yml
│       └── release.yml
├── App.xaml
├── App.xaml.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
├── SolarisLauncher.csproj
├── RELEASE_NOTES_v1.0.0.md
└── README.md
```

## Как загрузить в GitHub

1. Открой репозиторий `SonyVegaS13/minecraft-launcher`.
2. Загрузи содержимое этой папки в корень репозитория.
3. Сделай commit в `main`.
4. Открой вкладку **Actions** и дождись `Solaris Launcher CI`.
5. Для первого релиза создай тег `v1.0.0` и отправь его в GitHub.

Команды из локального репозитория:

```powershell
git add .
git commit -m "Prepare Solaris Launcher v1.0.0 release"
git push origin main
git tag v1.0.0
git push origin v1.0.0
```

После этого workflow `Solaris Launcher Release`:

- восстановит NuGet-зависимости;
- соберёт Release;
- опубликует self-contained `win-x64` single-file EXE;
- упакует его в `SolarisLauncher-v1.0.0-win-x64.zip`;
- создаст `SHA256SUMS.txt`;
- создаст GitHub Release с файлами.

## Где будет ссылка для игроков

После успешного workflow открой **Releases** в репозитории.

Там будет ZIP с лаунчером. Эту страницу можно использовать как первую публичную ссылку Solaris.

## Следующий этап

Автообновление самого `SolarisLauncher.exe` пока не включено. Это намеренно: сначала стабилизируем v1.0.0. Затем добавим manifest/version endpoint и безопасное обновление launcher.
