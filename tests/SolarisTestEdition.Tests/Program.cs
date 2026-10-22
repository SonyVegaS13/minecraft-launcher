using System.Diagnostics;
using SolarisLauncher;

// Tests link the actual edition helper. They never load WPF, contact an API,
// touch a real Solaris profile, or claim to validate native Windows rendering.
int checks = 0;
int failures = 0;
void Check(bool condition, string name)
{
    checks++;
    if (!condition) { failures++; Console.WriteLine("FAIL " + name); }
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); Check(false, name); }
    catch (T) { Check(true, name); }
    catch (Exception e) { Check(false, name + " unexpected " + e.GetType().Name); }
}
bool Enabled() => SolarisTestEdition.Enabled;
string initialDirectory = Directory.GetCurrentDirectory();
string? initialTemp = Environment.GetEnvironmentVariable("TEMP");
string? initialTmp = Environment.GetEnvironmentVariable("TMP");
string? initialTmpDir = Environment.GetEnvironmentVariable("TMPDIR");
string fixture = Path.Combine(Path.GetTempPath(), "solaris-edition-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var links = new List<string>();
void RemoveEmptyUnitRuntimeDirectories()
{
    // Repeated runs may retain only empty helper directories under this test
    // console's bin folder. Never remove files or recursively delete test state.
    foreach (string path in new[] { SolarisTestEdition.StateDirectory, SolarisTestEdition.UiDirectory,
        SolarisTestEdition.TempDirectory, SolarisTestEdition.LogDirectory,
        Path.GetDirectoryName(SolarisTestEdition.SmokeReportPath)!, SolarisTestEdition.DataRoot })
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
try
{
    if (Enabled()) RemoveEmptyUnitRuntimeDirectories();
#if SOLARIS_ID_TEST_BUILD
    Check(Enabled(), "TEST constant enables only the compiled test edition");
#else
    Check(!Enabled(), "normal compile does not enable the test edition");
#endif
    Check(SolarisTestEdition.ValidateArguments([]), "normal test launch has no arguments");
    Check(SolarisTestEdition.ValidateArguments(["--test-build-smoke"]), "bounded smoke is the only admitted option");
    foreach (string[] arguments in new[] {
        new[] { "--self-update", "installed.exe", "new.exe", "2.2.99", "attempt" },
        new[] { "--apply-update", "installed.exe", "new.exe", "2.2.99", "attempt" },
        new[] { "--updated", "attempt" }, new[] { "--update-recovery" },
        new[] { "--dev-test" }, new[] { "--dev-channel" },
        new[] { "--test-build-smoke", "arbitrary-output.json" },
        new[] { "--test-build-smoke", "--dev-channel" }, new[] { "--unknown" },
        new[] { "--TEST-BUILD-SMOKE" } })
    {
        Check(!SolarisTestEdition.ValidateArguments(arguments), "unsafe arguments denied: " + arguments[0]);
        Throws<InvalidOperationException>(() => SolarisTestEdition.Initialize(arguments),
            "unsafe entry cannot initialize: " + arguments[0]);
    }
    Check(!SolarisTestEdition.Initialized, "invalid arguments did not initialize directories or process state");

    string protectedRoot = Path.Combine(fixture, "existing-solaris");
    string ordinary = Path.Combine(fixture, "new-test-copy");
    Directory.CreateDirectory(protectedRoot);
    string sentinel = Path.Combine(protectedRoot, "settings.json");
    File.WriteAllText(sentinel, "protected-fixture-sentinel");
    foreach (string candidate in new[] { protectedRoot, Path.Combine(protectedRoot, "nested"), protectedRoot.ToUpperInvariant() })
        Throws<InvalidOperationException>(() => SolarisTestEdition.ValidateIndependentLocation(candidate, [protectedRoot]),
            "protected folder or child rejected");
    SolarisTestEdition.ValidateIndependentLocation(ordinary, [protectedRoot]);
    Check(!Directory.Exists(ordinary), "location validation itself never creates directories");
    Check(!SolarisTestEdition.IsWithin(protectedRoot + "-sibling", protectedRoot), "prefix-only sibling is not a child");
    Check(SolarisTestEdition.IsWithin(Path.Combine(protectedRoot, "child", "..", "file"), protectedRoot), "normalized child is recognized");
    Check(!SolarisTestEdition.IsWithin(Path.Combine(protectedRoot, "..", "outside"), protectedRoot), "normalized escape is rejected");

    string linked = Path.Combine(fixture, "linked-solaris");
    if (OperatingSystem.IsWindows())
    {
        // NTFS junctions do not require enabling Developer Mode. Both paths are
        // newly owned fixtures; this never links to the real user AppData.
        static string PsLiteral(string path) => "'" + path.Replace("'", "''") + "'";
        string script = "New-Item -ItemType Junction -Path " + PsLiteral(linked) +
            " -Target " + PsLiteral(protectedRoot) + " -ErrorAction Stop | Out-Null";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        Check(process.ExitCode == 0 && Directory.Exists(linked), "own NTFS junction fixture created");
    }
    else
    {
        Directory.CreateSymbolicLink(linked, protectedRoot);
        Check(Directory.Exists(linked), "own symlink fixture created");
    }
    if (Directory.Exists(linked))
    {
        links.Add(linked);
        Throws<IOException>(() => SolarisTestEdition.RejectReparseAncestors(Path.Combine(linked, "not-yet-created")), "reparse ancestor is rejected before creation");
        Throws<IOException>(() => SolarisTestEdition.ValidateIndependentLocation(linked, []), "linked installation root is rejected");
        Throws<IOException>(() => SolarisTestEdition.ValidateExistingTree(fixture), "nested reparse entry is rejected without traversal");
        Check(File.ReadAllText(sentinel) == "protected-fixture-sentinel", "link checks preserve target data");
    }

    foreach (string path in new[] { SolarisTestEdition.StateDirectory, SolarisTestEdition.UiDirectory,
        SolarisTestEdition.TempDirectory, SolarisTestEdition.LogDirectory, SolarisTestEdition.SmokeReportPath })
        Check(SolarisTestEdition.IsWithin(path, SolarisTestEdition.DataRoot), "state/cache/log/temp/diagnostics stay in test-data");
    Check(SolarisTestEdition.IsWithin(Path.Combine(SolarisTestEdition.StateDirectory, "game"), SolarisTestEdition.DataRoot), "modded client is in isolated root");
    Check(SolarisTestEdition.IsWithin(Path.Combine(SolarisTestEdition.StateDirectory, "vanilla"), SolarisTestEdition.DataRoot), "vanilla client is in isolated root");
    Check(SolarisTestEdition.IsWithin(Path.Combine(SolarisTestEdition.StateDirectory, "solaris-id-clients", "vanilla-1.20.1"), SolarisTestEdition.DataRoot), "optional client is in isolated root");
    Check(SolarisTestEdition.IsWithin(SolarisTestEdition.ConfigFile, Path.Combine(SolarisTestEdition.InstallRoot, "config")), "configuration is independent from installed launcher");
    Check(SolarisTestEdition.ConfigFile.EndsWith("solaris-id.test.json", StringComparison.Ordinal), "explicit test configuration filename");
    Check(SolarisTestEdition.MutexName.StartsWith(@"Local\SolarisLauncher.IdTest.", StringComparison.Ordinal), "test mutex cannot collide with production mutex");
    Check(!new[] { @"Local\SolarisLauncher.Main", @"Local\SolarisLauncher.DevTest", @"Local\SolarisLauncher.LegacyMigration" }.Contains(SolarisTestEdition.MutexName), "test instance lock is separate from every existing channel");

    if (Enabled())
    {
        // A blocking file must fail rather than redirect state into AppData.
        // Do not replace a prior test run's directory or any existing data.
        if (!Path.Exists(SolarisTestEdition.DataRoot))
        {
            File.WriteAllText(SolarisTestEdition.DataRoot, "owned-blocking-file");
            try
            {
                Throws<IOException>(() => SolarisTestEdition.Initialize([]), "blocked state root fails without fallback");
                Check(!SolarisTestEdition.Initialized, "blocked root cannot mark initialization successful");
            }
            finally { File.Delete(SolarisTestEdition.DataRoot); }
        }
        SolarisTestEdition.Initialize(["--test-build-smoke"]);
        Check(SolarisTestEdition.Initialized && SolarisTestEdition.SmokeRequested, "valid bounded smoke initializes the actual helper");
        foreach (string path in new[] { SolarisTestEdition.StateDirectory, SolarisTestEdition.UiDirectory, SolarisTestEdition.TempDirectory, SolarisTestEdition.LogDirectory })
            Check(Directory.Exists(path), "isolated runtime directory exists");
        Check(Directory.GetCurrentDirectory() == SolarisTestEdition.DataRoot, "relative SDK work cannot start in production directory");
        Check(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) == SolarisTestEdition.TempDirectory.TrimEnd(Path.DirectorySeparatorChar), "process temp points into test-data");
        Check(Environment.GetEnvironmentVariable("TEMP") == SolarisTestEdition.TempDirectory && Environment.GetEnvironmentVariable("TMP") == SolarisTestEdition.TempDirectory,
            "TEMP and TMP overrides are process-local isolated paths");
    }
    else
    {
        Throws<InvalidOperationException>(() => SolarisTestEdition.Initialize([]), "runtime arguments cannot enable test edition in a normal compile");
        Check(!SolarisTestEdition.Initialized, "normal compile never initializes the test environment");
    }
    Check(File.ReadAllText(sentinel) == "protected-fixture-sentinel", "all checks preserve protected fixture contents");
}
catch (Exception error)
{
    failures++;
    Console.WriteLine("FAIL unexpected " + error.GetType().Name + ": " + error.Message);
}
finally
{
    Directory.SetCurrentDirectory(initialDirectory);
    Environment.SetEnvironmentVariable("TEMP", initialTemp);
    Environment.SetEnvironmentVariable("TMP", initialTmp);
    Environment.SetEnvironmentVariable("TMPDIR", initialTmpDir);
    if (Enabled()) RemoveEmptyUnitRuntimeDirectories();
    foreach (string link in links) if (Directory.Exists(link)) Directory.Delete(link);
    if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
}
Console.WriteLine($"SOLARIS_TEST_EDITION_UNIT: {checks} checks, {failures} failures, enabled={Enabled()}");
return failures == 0 ? 0 : 1;
