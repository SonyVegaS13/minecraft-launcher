using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

// r174: the visible "Проверить файлы" button audits installed clients,
// checksums and bundled Java. This method NEVER downloads, deletes or repairs
// files automatically. Existing Play/Update actions handle explicit repairs.
public partial class MainWindow
{
    private CancellationTokenSource? _fileCheckCancellation;
    private bool _fileCheckRunning;

    private sealed class FileAudit
    {
        public string Mode { get; }
        public int Verified { get; set; }
        public List<string> Errors { get; } = new();
        public bool Healthy => Errors.Count == 0;
        public FileAudit(string mode) { Mode = mode; }
        public void Add(string message)
        {
            if (Errors.Count < 30) Errors.Add(message);
        }
        public string Summary => Healthy
            ? Mode + ": проверено " + Verified + " файлов, ошибок нет"
            : Mode + ": обнаружены проблемы (" + Errors.Count + ")";
    }

    private sealed record ExpectedCheckFile(string Path, string Name, long Size, string Sha1);

    private void ShowAuditStep(int percentage, string message)
    {
        Progress.Value = Math.Clamp(percentage, 1, 99);
        StatusText.Text = message;
        SetIgnitionDetail("ФАЙЛЫ " + percentage + "%");
    }

    private static bool LooksLikeSha1(string sha) =>
        sha.Length == 40 && sha.All(Uri.IsHexDigit);

    private static bool LooksLikeSha256(string sha) =>
        sha.Length == 64 && sha.All(Uri.IsHexDigit);

    private static bool IsUnderDirectory(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> MatchesSha1Async(string path, string expected, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 65536, useAsync: true);
        string actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddExpectedFromMetadata(JsonElement properties, string localPath,
        List<ExpectedCheckFile> expected, string label)
    {
        if (properties.ValueKind != JsonValueKind.Object) return;
        string sha1 = properties.TryGetProperty("sha1", out JsonElement hash) &&
            hash.ValueKind == JsonValueKind.String ? hash.GetString() ?? "" : "";
        long size = properties.TryGetProperty("size", out JsonElement length) &&
            length.ValueKind == JsonValueKind.Number ? length.GetInt64() : -1;
        expected.Add(new ExpectedCheckFile(localPath, label, size, sha1));
    }

    private async Task<FileAudit> CheckMinecraftBaseAsync(
        string root, string version, string label, int from, int to, CancellationToken ct)
    {
        FileAudit audit = new(label);
        string jsonPath = Path.Combine(root, "versions", version, version + ".json");
        if (!File.Exists(jsonPath))
        {
            audit.Add("Отсутствует описание версии " + version + ". Нужна установка клиента.");
            return audit;
        }

        var expected = new List<ExpectedCheckFile>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, ct));
            JsonElement json = doc.RootElement;
            string jarPath = Path.Combine(root, "versions", version, version + ".jar");
            if (json.TryGetProperty("downloads", out JsonElement downloads) &&
                downloads.TryGetProperty("client", out JsonElement client))
                AddExpectedFromMetadata(client, jarPath, expected, "Minecraft " + version + ".jar");
            else
                expected.Add(new ExpectedCheckFile(jarPath, "Minecraft " + version + ".jar", -1, ""));

            if (json.TryGetProperty("libraries", out JsonElement libraries) &&
                libraries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement library in libraries.EnumerateArray())
                {
                    // Mojang library rules may exclude Windows, Linux or macOS.
                    if (!WindowsLibraryIsEnabled(library)) continue;
                    if (!library.TryGetProperty("downloads", out JsonElement files) ||
                        !files.TryGetProperty("artifact", out JsonElement artifact) ||
                        !artifact.TryGetProperty("path", out JsonElement relative)) continue;
                    string relativePath = relative.GetString() ?? "";
                    if (string.IsNullOrEmpty(relativePath)) continue;
                    string absolute = Path.GetFullPath(Path.Combine(root, "libraries",
                        relativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!IsUnderDirectory(absolute, Path.Combine(root, "libraries")))
                    {
                        audit.Add("В манифесте обнаружен недопустимый путь библиотеки.");
                        continue;
                    }
                    AddExpectedFromMetadata(artifact, absolute, expected,
                        "Библиотека " + Path.GetFileName(absolute));
                }
            }

            if (json.TryGetProperty("assetIndex", out JsonElement index) &&
                index.ValueKind == JsonValueKind.Object &&
                index.TryGetProperty("id", out JsonElement indexId))
            {
                string id = indexId.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(id) &&
                    id.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                {
                    string indexPath = Path.Combine(root, "assets", "indexes", id + ".json");
                    AddExpectedFromMetadata(index, indexPath, expected, "Индекс ресурсов " + id);
                    if (!File.Exists(indexPath))
                    {
                        audit.Add("Отсутствует индекс игровых ресурсов " + id);
                    }
                    else
                    {
                        try
                        {
                            using JsonDocument indexDoc = JsonDocument.Parse(
                                await File.ReadAllTextAsync(indexPath, ct));
                            if (indexDoc.RootElement.TryGetProperty("objects", out JsonElement objects) &&
                                objects.ValueKind == JsonValueKind.Object)
                            {
                                foreach (JsonProperty item in objects.EnumerateObject())
                                {
                                    JsonElement resource = item.Value;
                                    if (!resource.TryGetProperty("hash", out JsonElement hash)) continue;
                                    string sha = hash.GetString() ?? "";
                                    if (!LooksLikeSha1(sha))
                                    {
                                        audit.Add("Недопустимая контрольная сумма ресурса " + item.Name);
                                        continue;
                                    }
                                    string assetPath = Path.Combine(root, "assets", "objects",
                                        sha[..2], sha);
                                    AddExpectedFromMetadata(resource, assetPath, expected, "Ресурс " + item.Name);
                                }
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (ex is IOException or JsonException)
                        {
                            audit.Add("Не читается индекс игровых ресурсов " + id);
                        }
                    }
                }
                else audit.Add("Недопустимое имя индекса игровых ресурсов.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            audit.Add("Повреждены метаданные Minecraft " + version);
            return audit;
        }

        // Count-based progress reflects files actually read, never a fake timer.
        for (int i = 0; i < expected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ExpectedCheckFile file = expected[i];
            try
            {
                if (!File.Exists(file.Path)) audit.Add("Нет файла: " + file.Name);
                else if (file.Size >= 0 && new FileInfo(file.Path).Length != file.Size)
                    audit.Add("Размер не совпадает: " + file.Name);
                else if (LooksLikeSha1(file.Sha1) &&
                         !await MatchesSha1Async(file.Path, file.Sha1, ct))
                    audit.Add("SHA-1 не совпадает: " + file.Name);
                else audit.Verified++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                audit.Add("Не удалось прочесть: " + file.Name);
            }

            if (i % 20 == 0 || i == expected.Count - 1)
                ShowAuditStep(from + (int)((to - from) * (i + 1.0) /
                    Math.Max(1, expected.Count)),
                    label + ": проверено " + (i + 1) + " из " + expected.Count + " файлов...");
        }
        return audit;
    }

    private static bool WindowsLibraryIsEnabled(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out JsonElement rules) ||
            rules.ValueKind != JsonValueKind.Array) return true;
        bool allowed = false;
        foreach (JsonElement rule in rules.EnumerateArray())
        {
            bool matches = true;
            if (rule.TryGetProperty("os", out JsonElement os) && os.ValueKind == JsonValueKind.Object &&
                os.TryGetProperty("name", out JsonElement osName))
                matches = string.Equals(osName.GetString(), "windows",
                    StringComparison.OrdinalIgnoreCase);
            if (matches && rule.TryGetProperty("action", out JsonElement action))
                allowed = string.Equals(action.GetString(), "allow",
                    StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private async Task<FileAudit> CheckManagedModsAsync(int from, int to, CancellationToken ct)
    {
        FileAudit audit = new("Modded: моды");
        if (!File.Exists(ManagedPackManifestPath))
        {
            audit.Add("Отсутствует манифест сборки. Нажмите «Обновить» в Solaris Modded.");
            return audit;
        }
        ManagedPackManifest? manifest;
        try
        {
            await using FileStream input = File.OpenRead(ManagedPackManifestPath);
            manifest = await JsonSerializer.DeserializeAsync<ManagedPackManifest>(input,
                cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            audit.Add("Невозможно прочитать манифест модов.");
            return audit;
        }
        string tagPath = Path.Combine(_stateDir, "client-release.txt");
        string installedTag = File.Exists(tagPath)
            ? (await File.ReadAllTextAsync(tagPath, ct)).Trim() : "";
        if (manifest is null || manifest.Files.Count == 0)
        {
            audit.Add("Нет перечня модов для проверки.");
            return audit;
        }
        if (string.IsNullOrWhiteSpace(installedTag) ||
            !string.Equals(installedTag, manifest.ReleaseTag, StringComparison.OrdinalIgnoreCase))
            audit.Add("Версия сборки отличается от сохранённого манифеста.");

        string mods = Path.Combine(_gameDir, "mods");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ManagedPackFile entry = manifest.Files[i];
            string absolute = Path.GetFullPath(Path.Combine(_gameDir, entry.RelativePath));
            if (!IsUnderDirectory(absolute, mods) || !LooksLikeSha256(entry.Sha256))
            {
                audit.Add("Недопустимая запись в манифесте модов.");
                continue;
            }
            expected.Add(absolute);
            try
            {
                if (!File.Exists(absolute)) audit.Add("Отсутствует мод: " + Path.GetFileName(absolute));
                else if (new FileInfo(absolute).Length != entry.Length)
                    audit.Add("Размер не совпадает: " + Path.GetFileName(absolute));
                else if (!string.Equals(await FileHashAsync(absolute, ct), entry.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                    audit.Add("SHA-256 не совпадает: " + Path.GetFileName(absolute));
                else audit.Verified++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                audit.Add("Ошибка чтения: " + Path.GetFileName(absolute));
            }
            if (i % 5 == 0 || i == manifest.Files.Count - 1)
                ShowAuditStep(from + (int)((to - from) * (i + 1.0) /
                    Math.Max(1, manifest.Files.Count)),
                    "Modded: проверено " + (i + 1) + " из " + manifest.Files.Count + " файлов...");
        }
        if (!Directory.Exists(mods)) audit.Add("Отсутствует папка mods.");
        else
        {
            foreach (string path in Directory.EnumerateFiles(mods, "*.jar", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!expected.Contains(Path.GetFullPath(path)))
                    audit.Add("Неучтённый мод: " + Path.GetFileName(path));
            }
        }
        return audit;
    }

    private async void FileIntegrityCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_fileCheckRunning)
        {
            _fileCheckCancellation?.Cancel();
            StatusText.Text = "Отменяем проверку файлов...";
            return;
        }
        if (_launchCancellation is not null || _installingLauncherUpdate ||
            _serverStateRefreshRunning || _updateOperationRunning)
        {
            StatusText.Text = "Дождитесь завершения текущей операции Solaris.";
            return;
        }

        using var cts = new CancellationTokenSource();
        _fileCheckCancellation = cts;
        _fileCheckRunning = true;
        FileCheckButton.Content = "×  ОТМЕНА";
        RefreshServerActionButtons();

        try
        {
            ShowAuditStep(1, "Проверяем файлы Solaris Vanilla...");
            string vanillaRoot = Path.Combine(_stateDir, "vanilla");
            FileAudit vanilla = await CheckMinecraftBaseAsync(vanillaRoot, _vanillaTargetVersion,
                "Vanilla", 1, 37, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ShowAuditStep(39, "Проверяем Minecraft и Forge...");
            FileAudit minecraft = await CheckMinecraftBaseAsync(_gameDir, MinecraftVersion,
                "Modded: Minecraft", 39, 63, cts.Token);
            if (!HasForgeClientFiles()) minecraft.Add("Не установлена версия Forge " + ForgeClientVersionName);
            else
            {
                try
                {
                    string forge = Path.Combine(_gameDir, "versions", ForgeClientVersionName,
                        ForgeClientVersionName + ".json");
                    using JsonDocument forgeDoc = JsonDocument.Parse(
                        await File.ReadAllTextAsync(forge, cts.Token));
                    minecraft.Verified++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    minecraft.Add("Повреждено описание Forge.");
                }
            }
            cts.Token.ThrowIfCancellationRequested();
            ShowAuditStep(65, "Сверяем SHA-256 модовой сборки...");
            FileAudit mods = await CheckManagedModsAsync(65, 93, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ShowAuditStep(95, "Проверяем Java 25 и Java 17...");
            bool vanillaJava = await Task.Run(() =>
                !string.IsNullOrWhiteSpace(FindJavaForMode(vanillaRoot, 25)), cts.Token);
            bool moddedJava = await Task.Run(() =>
                !string.IsNullOrWhiteSpace(FindJavaForMode(_gameDir, 17)), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!vanillaJava) vanilla.Add("Java 25 не установлена или не запускается.");
            if (!moddedJava) minecraft.Add("Java 17 не установлена или не запускается.");

            // Report broken installations through the existing explicit Update
            // buttons. The verifier itself never changes installed game files.
            SetVanillaState(vanilla.Healthy ? ServerClientState.Ready :
                ServerClientState.UpdateRequired,
                vanilla.Healthy ? "Файлы и Java 25 проверены" : "Нужно восстановить Vanilla");
            bool moddedOk = minecraft.Healthy && mods.Healthy;
            SetModdedState(moddedOk ? ServerClientState.Ready :
                ServerClientState.UpdateRequired,
                moddedOk ? "Minecraft, Forge, Java и моды проверены" :
                    "Нужно восстановить Modded");

            string summary = vanilla.Summary + "\n" + minecraft.Summary +
                "\n" + mods.Summary + "\n" +
                "Java 25: " + (vanillaJava ? "ОК" : "не найдена") +
                " · Java 17: " + (moddedJava ? "ОК" : "не найдена");
            var allProblems = vanilla.Errors.Concat(minecraft.Errors).Concat(mods.Errors).ToList();
            StatusText.Text = allProblems.Count == 0 ?
                "Проверка завершена: файлы Minecraft, моды и Java в порядке." :
                "Проверка завершена: найдено " + allProblems.Count +
                " проблем. Нажмите «Обновить» для нужного мира.";
            Progress.Value = 100;
            string details = allProblems.Count == 0 ? "\nОшибок не найдено." :
                "\nПервые проблемы:\n• " +
                string.Join("\n• ", allProblems.Take(7)) +
                (allProblems.Count > 7 ? "\n... и другие." : "");
            MessageBox.Show(this, summary + details +
                "\n\nПроверка ничего не удаляет. Для восстановления нажмите «Обновить» на карточке нужного мира.",
                "Solaris — проверка файлов", MessageBoxButton.OK,
                allProblems.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Проверка файлов отменена. Игровые файлы не изменены.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка проверки файлов. Игровые файлы не изменены.";
            MessageBox.Show(this, "Не удалось завершить проверку:\n" + ex.Message,
                "Solaris — диагностика", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _fileCheckRunning = false;
            _fileCheckCancellation = null;
            Progress.Value = 0;
            SetIgnitionDetail(null);
            FileCheckButton.Content = "◉ Проверить файлы";
            RefreshServerActionButtons();
        }
    }
}
