# Solaris Modded — автозаход и космическая загрузка

## Проверенный владельцем адрес

- Modded: solarismodded.millida.host:25565 (если хостинг не указывает другой порт).
- Используется одинаково для статуса сервера и CmlLib ServerIp/ServerPort.
- Vanilla не изменяется.

## Красный экран Forge

В новой DEV-ветке кнопка Modded Play теперь вызывает PrepareModdedLoadingScreenAsync перед запуском.

- Для Minecraft 1.20.1 Forge 47.4.20 используется Simple Custom Early Loading v2.2.
- Лаунчер скачивает совместимый клиентский JAR через официальный Modrinth API и проверяет SHA-512.
- Создаёт фон и config/simple-custom-early-loading.json с изображением на весь экран и индикатором прогресса.
- Для Forge 1.20.1 сохраняет earlyWindowProvider = fmlearlywindow по инструкции автора; НЕ заменяет на вариант NeoForge.
- Задаёт darkMojangStudiosBackground:true в Modded options.txt, чтобы первоначальная заставка была тёмной, если появляется до модифицированного экрана.
- При ошибке получения/инициализации изображения обычный Minecraft продолжает запуск.

## Выбранное изображение

Для ТОЧНО последней утверждённой картинки положить файл solaris-loading.png в пользовательскую папку SolarisDirectories.StateDir (обычно %APPDATA%\Solaris\solaris-loading.png). При Modded Play картинка автоматически переносится в папку Forge config/simple-custom-early-loading. В этом коммите отправить сам PNG с вложения ChatGPT напрямую в GitHub не удалось, поэтому пока в проекте есть только код подключения; без выбранного файла используется уже встроенная картинка Assets/SolarisCosmicHome.jpg. Не называть эту резервную картинку точной копией последнего арта.

Если config/simple-custom-early-loading.json уже создан игроком вручную, лаунчер его не перезаписывает. После добавления своего PNG для первого включения требуется убедиться, что значение image в конфиге соответствует solaris-loading.png.

Источники: https://github.com/lukaskabc/SimpleCustomEarlyLoading ; https://modrinth.com/mod/simple-custom-early-loading

## Статус проверки

Работа пока сохранена только в feature/modded-autojoin-splash-prep. Сборка WPF (.NET 8) и запуск на реальном Windows/Forge ещё не выполнены. Стабильный Solaris 2.2.10 и его Release не изменялись. Перед публикацией проверить dotnet build, клиентские моды, первый запуск, серверное соединение и обновления клиента. Это косметическое изменение; отдельные фазы Forge/Mojang могут мелькать при старте.
