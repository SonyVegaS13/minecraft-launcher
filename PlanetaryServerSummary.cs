using System.Globalization;

namespace SolarisLauncher;

/// <summary>
/// Projects the existing launcher's status text into the planetary UI.
/// It never pings a server, changes its configuration, or invents player counts.
/// </summary>
public static class PlanetaryServerSummary
{
    public static string Presence(string? status)
    {
        if (status?.Contains("Сервер онлайн", StringComparison.OrdinalIgnoreCase) == true)
            return "Сервер онлайн";
        if (status?.Contains("недоступен", StringComparison.OrdinalIgnoreCase) == true)
            return "Сервер недоступен";
        return "Проверяем сервер…";
    }

    public static string Players(string? status, int configuredSlots = 200)
    {
        string unknown = $"Игроки: — / {configuredSlots}";
        if (status is null || !status.Contains("Сервер онлайн", StringComparison.OrdinalIgnoreCase))
            return unknown;
        const string marker = "игроков:";
        int markerIndex = status.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return unknown;
        string[] parts = status[(markerIndex + marker.Length)..].Trim().Split('/');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int players) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int capacity) ||
            players < 0 || capacity != configuredSlots)
            return unknown;
        return $"Игроки: {players} / {configuredSlots}";
    }
}
