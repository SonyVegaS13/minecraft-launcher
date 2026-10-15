using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

public partial class MainWindow
{
    private const string SplashJarName = "SimpleCustomEarlyLoading-2.2-forge.jar";

    // Cosmetic integration must never prevent the Modded client from launching.
    private async Task PrepareModdedLoadingScreenAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            string configDir = Path.Combine(_gameDir, "config");
            string imageDir = Path.Combine(configDir, "simple-custom-early-loading");
            Directory.CreateDirectory(imageDir);

            // The exact image selected by the owner can be placed into this
            // stable user-data location without recompiling the launcher.
            string selectedArt = Path.Combine(_stateDir, "solaris-loading.png");
            string imageName;
            if (File.Exists(selectedArt))
            {
                imageName = "solaris-loading.png";
                File.Copy(selectedArt, Path.Combine(imageDir, imageName), true);
            }
            else
            {
                // The current approved cosmic Solaris background is bundled in EXE.
                imageName = "solaris-cosmic.jpg";
                string installedArt = Path.Combine(imageDir, imageName);
                if (!File.Exists(installedArt))
                {
                    var uri = new Uri("pack://application:,,,/Assets/SolarisCosmicHome.jpg", UriKind.Absolute);
                    var resource = Application.GetResourceStream(uri)
                        ?? throw new InvalidOperationException("Solaris cosmic resource not found.");
                    using (resource.Stream)
                    using (FileStream destination = File.Create(installedArt))
                        resource.Stream.CopyTo(destination);
                }
            }

            // Schema and placement follow Simple Custom Early Loading's official
            // configuration file; preserve the player's own customized settings.
            string configPath = Path.Combine(configDir, "simple-custom-early-loading.json");
            if (!File.Exists(configPath))
            {
                var config = new
                {
                    elements = new[] {
                        new {
                            image = imageName,
                            position = new {
                                position_anchor = "TOP_LEFT",
                                position_unit = "PIXELS",
                                x = 0, y = 0,
                                size_unit = "PERCENTAGE",
                                width = 100, height = 100,
                                keep_aspect_ratio = false
                            }
                        }
                    },
                    progress_bar = new {
                        bar_count = 3,
                        position = new {
                            position_anchor = "TOP_CENTER",
                            position_unit = "PERCENTAGE",
                            x = 50, y = 88
                        }
                    },
                    performance_bar = false,
                    fox = false,
                    log_messages = false,
                    forge_version = false
                };
                await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config,
                    new JsonSerializerOptions { WriteIndented = true }), token);
            }
            else if (File.Exists(selectedArt))
            {
                // If the first launch used Solaris's bundled fallback, switch
                // just that known image reference after the approved art arrives.
                // Leave custom user JSON and all other elements untouched.
                string currentConfig = await File.ReadAllTextAsync(configPath, token);
                const string fallbackName = "\"image\": \"solaris-cosmic.jpg\"";
                const string approvedName = "\"image\": \"solaris-loading.png\"";
                if (currentConfig.Contains(fallbackName, StringComparison.Ordinal))
                    await File.WriteAllTextAsync(configPath,
                        currentConfig.Replace(fallbackName, approvedName, StringComparison.Ordinal), token);
            }

            // The late Forge override can briefly show Mojang's original red
            // screen; make that fallback dark without touching Vanilla options.
            string optionsPath = Path.Combine(_gameDir, "options.txt");
            if (File.Exists(optionsPath))
            {
                string options = await File.ReadAllTextAsync(optionsPath, token);
                const string pattern = @"(?m)^darkMojangStudiosBackground:[^\r\n]*$";
                string revised = Regex.IsMatch(options, pattern)
                    ? Regex.Replace(options, pattern, "darkMojangStudiosBackground:true")
                    : options.TrimEnd('\r', '\n') + Environment.NewLine +
                      "darkMojangStudiosBackground:true" + Environment.NewLine;
                if (revised != options)
                    await File.WriteAllTextAsync(optionsPath, revised, token);
            }

            // On Forge 1.20.1, the upstream mod specifically expects the default
            // earlyWindowProvider=fmlearlywindow. Do not change fml.toml.
            await EnsureSplashJarAsync(token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TraceStartup("Cosmetic Modded splash could not initialize: " + ex.Message);
        }
    }

    private async Task EnsureSplashJarAsync(CancellationToken token)
    {
        string modsDir = Path.Combine(_gameDir, "mods");
        Directory.CreateDirectory(modsDir);
        string destination = Path.Combine(modsDir, SplashJarName);
        if (File.Exists(destination)) return;

        if (Directory.EnumerateFiles(modsDir, "*CustomEarlyLoading*.jar").Any())
        {
            TraceStartup("Modded splash: existing early loading mod retained.");
            return;
        }

        // The publisher's immutable Forge 1.20.1 release ID (v2.2-forge).
        // Never choose an untested latest release or a NeoForge package.
        const string url = "https://api.modrinth.com/v2/version/Un816pXM";
        using HttpResponseMessage response = await _http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        using JsonDocument version = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(token));

        JsonElement matchingVersion = version.RootElement;
        if (!string.Equals(matchingVersion.GetProperty("id").GetString(), "Un816pXM",
                StringComparison.Ordinal) ||
            !string.Equals(matchingVersion.GetProperty("version_number").GetString(),
                "2.2-forge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected Forge splash version.");

        JsonElement jarFile = matchingVersion.GetProperty("files").EnumerateArray()
            .FirstOrDefault(f => string.Equals(
                f.GetProperty("filename").GetString(), SplashJarName,
                StringComparison.OrdinalIgnoreCase));
        if (jarFile.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Published Forge splash file was not found.");

        string? fileUrl = jarFile.GetProperty("url").GetString();
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected splash JAR download URL.");

        string? sha512 = jarFile.GetProperty("hashes").GetProperty("sha512").GetString();
        if (string.IsNullOrEmpty(sha512))
            throw new InvalidOperationException("Published splash checksum missing.");

        using HttpResponseMessage download = await _http.GetAsync(uri, token);
        download.EnsureSuccessStatusCode();
        byte[] data = await download.Content.ReadAsByteArrayAsync(token);
        if (data.Length < 1000 || data.Length > 4_000_000 ||
            !string.Equals(Convert.ToHexString(SHA512.HashData(data)),
                sha512, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Downloaded splash JAR failed SHA512 verification.");

        string temporary = destination + ".download";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, token);
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
