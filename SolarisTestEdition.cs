using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SolarisLauncher;

// Build-time edition, never an environment switch for the installed launcher.
internal static class SolarisTestEdition
{
#if SOLARIS_ID_TEST_BUILD
    internal const bool Enabled = true;
#else
    internal const bool Enabled = false;
#endif
    internal static bool SmokeRequested { get; private set; }
    internal static bool Initialized { get; private set; }
    internal static string InstallRoot => Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
    internal static string DataRoot => Path.Combine(InstallRoot, "test-data");
    internal static string StateDirectory => Path.Combine(DataRoot, "state");
    internal static string UiDirectory => Path.Combine(DataRoot, "ui");
    internal static string TempDirectory => Path.Combine(DataRoot, "temp");
    internal static string LogDirectory => Path.Combine(DataRoot, "logs");
    internal static string ArtworkLogPath => Path.Combine(LogDirectory, "artwork.log");
    internal static string ConfigFile => Path.Combine(InstallRoot, "config", "solaris-id.test.json");
    internal static string SmokeReportPath => Path.Combine(DataRoot, "diagnostics", "smoke.json");
    internal static string MutexName => @"Local\SolarisLauncher.IdTest." + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(InstallRoot.ToUpperInvariant())))[..16];

    internal static bool ValidateArguments(string[] args) => args.Length == 0 ||
        (args.Length == 1 && args[0] == "--test-build-smoke");

    internal static bool IsWithin(string path, string root)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateIndependentLocation(string installRoot, IEnumerable<string> protectedRoots)
    {
        foreach (string root in protectedRoots.Where(p => !string.IsNullOrWhiteSpace(p)))
            if (IsWithin(installRoot, root))
                throw new InvalidOperationException("TEST BUILD нельзя запускать из каталога установленного Solaris. Распакуйте ZIP в отдельную обычную папку.");
        RejectReparseAncestors(installRoot);
    }

    internal static void RejectReparseAncestors(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Для TEST BUILD запрещены junction/symlink пути. Выберите отдельную обычную папку.");
            current = Path.GetDirectoryName(current);
        }
    }

    internal static void ValidateExistingTree(string root)
    {
        RejectReparseAncestors(root);
        if (!Directory.Exists(root)) return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("В test-data обнаружена ссылка/junction. Запуск остановлен для защиты установленных данных.");
            if (Directory.Exists(entry)) ValidateExistingTree(entry);
        }
    }

    internal static void Initialize(string[] args)
    {
        if (!Enabled || !ValidateArguments(args))
            throw new InvalidOperationException("TEST BUILD не поддерживает параметры установки, обновления или переноса. Запустите EXE без аргументов.");
        SmokeRequested = args.Length == 1;
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ValidateIndependentLocation(InstallRoot, new[] {
            Path.Combine(roaming,"Solaris"), Path.Combine(roaming,"Solaris-Neon-Dev"),
            Path.Combine(local,"Solaris"), Path.Combine(local,"Solaris-Neon-Dev"),
            Path.Combine(local,"Programs","Solaris Launcher"),
            Path.Combine(local,"Programs","Solaris Launcher DEV"),
            Path.Combine(local,"Programs","SolarisLauncher"),
            Path.Combine(local,"Programs","SolarisLauncher.exe") });
        // An active operator config must not be a link into another installation.
        RejectReparseAncestors(ConfigFile);
        ValidateExistingTree(DataRoot);
        foreach (string path in new[] { StateDirectory, UiDirectory, TempDirectory, LogDirectory,
            Path.GetDirectoryName(SmokeReportPath)! }) Directory.CreateDirectory(path);
        // Process-only overrides: no registry/user environment changes. Native
        // .NET DLLs are shipped beside the EXE, not extracted into shared .net.
        Environment.SetEnvironmentVariable("TEMP", TempDirectory);
        Environment.SetEnvironmentVariable("TMP", TempDirectory);
        Environment.SetEnvironmentVariable("TMPDIR", TempDirectory);
        Directory.SetCurrentDirectory(DataRoot);
        Initialized = true;
    }
}
