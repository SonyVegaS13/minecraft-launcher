using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SolarisLauncher;

public partial class MainWindow
{
    // Temurin runtime is provisioned only if Mojang's runtime / the bundled runtime
    // is missing. The Adoptium REST metadata contains the exact SHA256 of the
    // release ZIP, which is verified before extracting executable files.
    private async Task<string> EnsureJavaForModeAsync(string gameRoot, int major,
        double percentFrom, double percentTo, CancellationToken token)
    {
        if (major != 17 && major != 25)
            throw new ArgumentOutOfRangeException(nameof(major));

        string found = FindJavaForMode(gameRoot, major);
        if (File.Exists(found))
        {
            StatusText.Text = $"Java {major} уже установлена — проверено.";
            return found;
        }
        StatusText.Text = $"Java {major} отсутствует — скачиваем проверенную сборку...";
        Progress.Value = percentFrom;

        string runtimeRoot = Path.Combine(gameRoot, "runtime");
        Directory.CreateDirectory(runtimeRoot);
        string destination = Path.Combine(runtimeRoot, $"solaris-java-{major}");
        string zip = Path.Combine(runtimeRoot, $"solaris-java-{major}-{Guid.NewGuid():N}.zip");
        string stage = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string? backup = null;
        try
        {
            string metadataUrl =
                $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                "?architecture=x64&heap_size=normal&image_type=jre&jvm_impl=hotspot&os=windows&vendor=eclipse";
            using HttpResponseMessage infoResponse = await _http.GetAsync(metadataUrl, token);
            infoResponse.EnsureSuccessStatusCode();
            await using Stream data = await infoResponse.Content.ReadAsStreamAsync(token);
            using JsonDocument metadata = await JsonDocument.ParseAsync(data, cancellationToken: token);
            JsonElement first = metadata.RootElement.EnumerateArray().First();
            JsonElement package = first.GetProperty("binary").GetProperty("package");
            string url = package.GetProperty("link").GetString() ?? "";
            string expected = package.GetProperty("checksum").GetString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !(downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || downloadUri.Host.EndsWith(".adoptium.net", StringComparison.OrdinalIgnoreCase)) ||
                expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new InvalidDataException("Источник Java или контрольная сумма недействительны.");

            using HttpResponseMessage response = await _http.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            if (total is > 600_000_000)
                throw new InvalidDataException("Архив Java превышает допустимый размер.");
            await using (Stream input = await response.Content.ReadAsStreamAsync(token))
            await using (FileStream output = new(zip, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, useAsync: true))
            using (IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[131072];
                long current = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, token)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    current += n;
                    if (current > 600_000_000) throw new InvalidDataException("Архив Java слишком большой.");
                    digest.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), token);
                    if (total is > 0)
                    {
                        double fraction = Math.Clamp((double)current / total.Value, 0, 1);
                        Progress.Value = percentFrom + (percentTo - percentFrom) * fraction;
                        IgnitionPercent.Text = $"{fraction * 100:0}%";
                        StatusText.Text = $"Скачиваем Java {major}: {current / 1048576} / {total.Value / 1048576} МБ...";
                    }
                }
                string actual = Convert.ToHexString(digest.GetHashAndReset());
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 Java {major} не совпал — установка отменена.");
            }

            StatusText.Text = $"Java {major}: архив проверен, распаковываем...";
            Directory.CreateDirectory(stage);
            string normalizedStage = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Count > 30000)
                    throw new InvalidDataException("Архив Java содержит слишком много файлов.");
                long uncompressed = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    uncompressed += entry.Length;
                    if (uncompressed > 1_200_000_000)
                        throw new InvalidDataException("Распакованный архив Java слишком большой.");
                    string target = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                    if (!target.StartsWith(normalizedStage, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("В архиве Java обнаружен недопустимый путь.");
                    if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: false);
                    }
                }
            }

            // Upstream archives contain a single jdk-X.y directory.
            string[] executables = Directory.GetFiles(stage, "java.exe", SearchOption.AllDirectories)
                .Where(p => p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (executables.Length != 1)
                throw new InvalidDataException("В архиве Java отсутствует ожидаемый исполняемый файл.");

            string extractedRoot = Directory.GetParent(Path.GetDirectoryName(executables[0])!)!.FullName;
            if (Directory.Exists(destination))
            {
                backup = destination + "." + Guid.NewGuid().ToString("N") + ".old";
                Directory.Move(destination, backup);
            }
            try
            {
                Directory.Move(extractedRoot, destination);
                string installedJava = FindJavaForMode(gameRoot, major);
                if (!File.Exists(installedJava))
                    throw new InvalidDataException("После установки не удалось проверить версию Java.");
            }
            catch
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                if (backup is not null && Directory.Exists(backup))
                    Directory.Move(backup, destination);
                throw;
            }

            if (backup is not null) TryDeleteDirectory(backup);
            Progress.Value = percentTo;
            StatusText.Text = $"Java {major} установлена и проверена.";
            return FindJavaForMode(gameRoot, major);
        }
        finally
        {
            TryDeleteFile(zip);
            TryDeleteDirectory(stage);
        }
    }
}
