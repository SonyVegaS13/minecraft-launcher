using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SolarisLauncher;

// Operator-supplied, non-secret per-install configuration. Never supplied by a
// downloaded skin, a Minecraft server, an updater response or a saved session.
internal sealed class SolarisIdConfiguration
{
    public string ApiOrigin { get; set; } = "";
    public string VanillaServerId { get; set; } = "";
    public string ModdedServerId { get; set; } = "";

    internal static Uri? ValidateOrigin(string? origin)
    {
        if (origin?.Contains('\\') == true || !Uri.TryCreate(origin?.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    internal static bool IsServerId(string? value) =>
        value is not null && Regex.IsMatch(value, "\\A[a-z0-9][a-z0-9_-]{1,63}\\z", RegexOptions.CultureInvariant);

    internal string? ServerFor(string mode)
    {
        string value = mode == "vanilla" ? VanillaServerId : mode == "modded" ? ModdedServerId : "";
        return IsServerId(value) ? value : null;
    }

    internal static SolarisIdConfiguration Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return new();
            return JsonSerializer.Deserialize<SolarisIdConfiguration>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
        catch (JsonException) { return new(); }
    }
}
