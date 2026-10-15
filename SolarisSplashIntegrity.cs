using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SolarisLauncher;

// Launcher-managed visual add-ons must not be confused with the exact
// server-managed modpack. Only the verified, pinned Forge splash JAR is exempt.
public partial class MainWindow
{
    private string SplashTrustMarkerPath => Path.Combine(_stateDir, "solaris-splash-sha512.txt");

    private bool IsOfficialSplashPath(string filePath) =>
        string.Equals(Path.GetFullPath(filePath),
            Path.GetFullPath(Path.Combine(_gameDir, "mods", SplashJarName)),
            StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSha512(string? digest) =>
        !string.IsNullOrWhiteSpace(digest) &&
        Regex.IsMatch(digest.Trim(), @"\A[0-9a-fA-F]{128}\z");

    private string? CachedSplashSha512()
    {
        try
        {
            if (!File.Exists(SplashTrustMarkerPath)) return null;
            string sha = File.ReadAllText(SplashTrustMarkerPath).Trim();
            return LooksLikeSha512(sha) ? sha : null;
        }
        catch { return null; }
    }

    private bool IsVerifiedSplashJarCached(string installed)
    {
        if (!IsOfficialSplashPath(installed) || !File.Exists(installed))
            return false;

        string? expected = CachedSplashSha512();
        if (expected is null) return false;

        try
        {
            var size = new FileInfo(installed).Length;
            if (size < 1_000 || size > 4_000_000) return false;
            using var file = File.OpenRead(installed);
            string actual = Convert.ToHexString(SHA512.HashData(file));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Migration for 2.2.11/2.2.12: users already have the publisher-verified JAR,
    // but older launchers did not record a separate local trust marker.
    private async Task<bool> IsVerifiedSplashJarAsync(
        string installed, CancellationToken token)
    {
        if (!IsOfficialSplashPath(installed) || !File.Exists(installed))
            return false;

        if (IsVerifiedSplashJarCached(installed)) return true;

        // A cached digest mismatch is treated as a real integrity violation,
        // not as a reason to silently bless modified executable bytes.
        if (CachedSplashSha512() is not null) return false;

        try
        {
            using HttpResponseMessage response =
                await _http.GetAsync("https://api.modrinth.com/v2/version/Un816pXM", token);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(token));

            JsonElement version = document.RootElement;
            if (!string.Equals(version.GetProperty("id").GetString(), "Un816pXM",
                    StringComparison.Ordinal) ||
                !string.Equals(version.GetProperty("version_number").GetString(),
                    "2.2-forge", StringComparison.OrdinalIgnoreCase))
                return false;

            JsonElement official = version.GetProperty("files").EnumerateArray()
                .FirstOrDefault(item => string.Equals(
                    item.GetProperty("filename").GetString(), SplashJarName,
                    StringComparison.OrdinalIgnoreCase));
            if (official.ValueKind != JsonValueKind.Object) return false;

            string? expectedSha = official.GetProperty("hashes")
                .GetProperty("sha512").GetString();
            if (!LooksLikeSha512(expectedSha)) return false;

            var size = new FileInfo(installed).Length;
            if (size < 1_000 || size > 4_000_000) return false;
            await using var input = new FileStream(installed, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, useAsync: true);
            string actualSha = Convert.ToHexString(
                await SHA512.HashDataAsync(input, token));

            if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                return false;

            Directory.CreateDirectory(_stateDir);
            await File.WriteAllTextAsync(SplashTrustMarkerPath, expectedSha!, token);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            TraceStartup("Could not migrate pinned Solaris splash integrity: " + ex.Message);
            return false;
        }
    }
}
