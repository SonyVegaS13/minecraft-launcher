#if SOLARIS_ID_TEST_BUILD
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SolarisLauncher;

public partial class MainWindow
{
    private bool _testWindowLoaded;
    private bool _testWindowRendered;
    private bool _testSmokeFinished;

    private void InitializeTestEditionUi()
    {
        Title = "Solaris Launcher — TEST BUILD — Solaris ID";
        NeonBuildTag.Text = "TEST BUILD · SOLARIS ID · ИЗОЛИРОВАННАЯ КОПИЯ";
        Loaded += (_, _) => _testWindowLoaded = true;
        if (!SolarisTestEdition.SmokeRequested) return;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timeout.Tick += (_, _) => { timeout.Stop(); FinishTestSmoke(false); };
        timeout.Start();
        ContentRendered += async (_, _) => {
            _testWindowRendered = true;
            await Task.Delay(350);
            timeout.Stop();
            FinishTestSmoke(true);
        };
    }

    private void FinishTestSmoke(bool rendered)
    {
        if (_testSmokeFinished) return;
        _testSmokeFinished = true;
        try
        {
            bool previousProbe = CheckTestDpapiStorage();
            var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(m => new[] { "coreclr.dll", "clrjit.dll", "wpfgfx_cor3.dll", "PresentationNative_cor3.dll" }
                    .Contains(m.ModuleName, StringComparer.OrdinalIgnoreCase))
                .Select(m => m.FileName).ToArray();
            // Windows .NET8 singlefilehost statically links CoreCLR/JIT. WPF's
            // native renderer must be loaded from the adjacent private DLLs.
            bool nativeLocal = native.Any(p => Path.GetFileName(p).Equals("wpfgfx_cor3.dll",StringComparison.OrdinalIgnoreCase))
                && native.All(p => SolarisTestEdition.IsWithin(p, SolarisTestEdition.InstallRoot));
            bool ok = rendered && _testWindowLoaded && _testWindowRendered &&
                new WindowInteropHelper(this).Handle != IntPtr.Zero && nativeLocal;
            string exe = Environment.ProcessPath!;
            var report = new {
                testBuild = true, windowLoaded = _testWindowLoaded,
                contentRendered = _testWindowRendered, smokePassed = ok,
                stateDirectory = SolarisTestEdition.StateDirectory,
                testRoot = SolarisTestEdition.DataRoot, configFile = SolarisTestEdition.ConfigFile,
                productionUpdaterEnabled = false, productionServerConnectionsEnabled = false,
                nativeExtractionEnabled = false, nativeModulesInTestDirectory = nativeLocal,
                dpapiRoundTrip = true, previousDpapiProbeRestored = previousProbe,
                dpapiStorageProbeOnly = true, actualSolarisIdLoginTested = false,
                executableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant(),
                interactiveUserClicksTested = false
            };
            File.WriteAllText(SolarisTestEdition.SmokeReportPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(ok ? 0 : 4);
        }
        catch (Exception error)
        {
            // Preserve the failure category without raw credential-bearing data.
            try { File.WriteAllText(SolarisTestEdition.SmokeReportPath,
                JsonSerializer.Serialize(new { testBuild = true, smokePassed = false,
                    errorStage = "native-wpf-dpapi-smoke", errorType = error.GetType().Name })); } catch { }
            Application.Current.Shutdown(5);
        }
    }

    private static bool CheckTestDpapiStorage()
    {
        // Synthetic random storage probe, never an account or fake API response.
        string blob = Path.Combine(SolarisTestEdition.StateDirectory, "smoke-dpapi.dat");
        string hash = blob + ".sha256";
        byte[] entropy = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Solaris.ID.TestBuild.StorageProbe"));
        bool restored = false;
        if (File.Exists(blob) && File.Exists(hash))
        {
            byte[] previous = ProtectedData.Unprotect(File.ReadAllBytes(blob), entropy, DataProtectionScope.CurrentUser);
            restored = Convert.ToHexString(SHA256.HashData(previous)) == File.ReadAllText(hash);
            if (!restored) throw new InvalidDataException("Storage probe mismatch");
        }
        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        byte[] encrypted = ProtectedData.Protect(nonce, entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(blob, encrypted);
        File.WriteAllText(hash, Convert.ToHexString(SHA256.HashData(nonce)));
        if (!nonce.SequenceEqual(ProtectedData.Unprotect(File.ReadAllBytes(blob), entropy, DataProtectionScope.CurrentUser)))
            throw new InvalidDataException("Storage probe failed");
        return restored;
    }
}
#endif
