using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SolarisLauncher;

public partial class MainWindow
{
    private sealed class ManagedPackManifest
    {
        public string ReleaseTag { get; set; } = "";
        public List<ManagedPackFile> Files { get; set; } = new();
    }
    private sealed class ManagedPackFile
    {
        public string RelativePath { get; set; } = "";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private string ManagedPackManifestPath => Path.Combine(_stateDir, "client-pack-files.json");

    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private async Task<bool> ValidateManagedPackManifestAsync(string releaseTag,
        CancellationToken token)
    {
        string path = ManagedPackManifestPath;
        if (!File.Exists(path)) return false;
        ManagedPackManifest? manifest;
        try
        {
            await using var file = File.OpenRead(path);
            manifest = await JsonSerializer.DeserializeAsync<ManagedPackManifest>(
                file, cancellationToken: token);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }

        if (manifest is null || manifest.Files.Count == 0 ||
            !string.Equals(manifest.ReleaseTag, releaseTag, StringComparison.OrdinalIgnoreCase))
            return false;

        string modsBase = Path.GetFullPath(Path.Combine(_gameDir, "mods")) +
            Path.DirectorySeparatorChar;
        foreach (ManagedPackFile entry in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            string absolute = Path.GetFullPath(Path.Combine(_gameDir, entry.RelativePath));
            if (!absolute.StartsWith(modsBase, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(absolute) || new FileInfo(absolute).Length != entry.Length)
                return false;
            string sha = await FileHashAsync(absolute, token);
            if (!sha.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private async Task SaveManagedPackManifestAsync(string sourceRoot,
        string releaseTag, CancellationToken token)
    {
        string modsSource = Path.Combine(sourceRoot, "mods");
        if (!Directory.Exists(modsSource))
            throw new InvalidDataException("Сборка SolarisClient.zip не содержит папку mods.");
        var manifest = new ManagedPackManifest { ReleaseTag = releaseTag };
        foreach (string path in Directory.EnumerateFiles(
            modsSource, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceRoot, path);
            manifest.Files.Add(new ManagedPackFile
            {
                RelativePath = relative,
                Length = new FileInfo(path).Length,
                Sha256 = await FileHashAsync(path, token)
            });
        }
        if (manifest.Files.Count == 0)
            throw new InvalidDataException("В папке mods нет файлов.");
        string destination = ManagedPackManifestPath;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest), token);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
