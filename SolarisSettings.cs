using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SolarisLauncher;

public partial class MainWindow
{
    private int _vanillaRamMb = 3072;
    private int _moddedRamMb = 4096;
    private bool _loadingRamSettings;
    private bool _ramSettingsReady;
    private string RamSettingsPath => Path.Combine(_stateDir, "settings.json");

    private sealed class LocalDeviceSettings
    {
        public int VanillaRamMb { get; set; } = 3072;
        public int ModdedRamMb { get; set; } = 4096;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong ExtendedAvailableVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memoryStatus);

    private static int AvailableRamLimit()
    {
        // GC memory is not the size of installed system RAM. Query Windows
        // physical RAM instead; always reserve at least 2 GiB for Windows.
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        long physicalMb = GlobalMemoryStatusEx(ref memory) ?
            (long)(memory.TotalPhysical / (1024UL * 1024UL)) : 8192;
        long reserveMb = Math.Max(2048, physicalMb / 4);
        return (int)Math.Clamp(physicalMb - reserveMb, 2048, 16384);
    }

    private void LoadRamSettings()
    {
        _loadingRamSettings = true;
        try
        {
            LocalDeviceSettings settings = File.Exists(RamSettingsPath)
                ? JsonSerializer.Deserialize<LocalDeviceSettings>(File.ReadAllText(RamSettingsPath))
                    ?? new LocalDeviceSettings()
                : new LocalDeviceSettings();
            int ceiling = AvailableRamLimit();
            VanillaRamSlider.Maximum = ceiling;
            ModdedRamSlider.Maximum = ceiling;
            _vanillaRamMb = Math.Clamp(settings.VanillaRamMb, 2048, ceiling);
            _moddedRamMb = Math.Clamp(settings.ModdedRamMb, 2048, ceiling);
            VanillaRamSlider.Value = _vanillaRamMb;
            ModdedRamSlider.Value = _moddedRamMb;
            RefreshMemoryLabels();
        }
        catch
        {
            // A corrupt settings file must never prevent the launcher from starting.
            VanillaRamSlider.Value = _vanillaRamMb = 3072;
            ModdedRamSlider.Value = _moddedRamMb = 4096;
            RefreshMemoryLabels();
        }
        finally { _loadingRamSettings = false; _ramSettingsReady = true; }
    }

    private void RefreshMemoryLabels()
    {
        if (VanillaRamText is null || ModdedRamText is null || RamSafetyText is null) return;
        VanillaRamText.Text = $"{_vanillaRamMb} МБ";
        ModdedRamText.Text = $"{_moddedRamMb} МБ";
        RamSafetyText.Text = _moddedRamMb < 4096
            ? "Для модовой сборки желательно 4 ГБ и более, если компьютер позволяет."
            : $"Доступный безопасный лимит: {AvailableRamLimit()} МБ. Остальная память остаётся Windows.";
    }

    private void SaveRamSettings()
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            var data = JsonSerializer.Serialize(new LocalDeviceSettings
            { VanillaRamMb = _vanillaRamMb, ModdedRamMb = _moddedRamMb });
            string tmp = RamSettingsPath + ".tmp";
            File.WriteAllText(tmp, data);
            File.Move(tmp, RamSettingsPath, overwrite: true);
        }
        catch (Exception)
        {
            RamSafetyText.Text = "Не удалось сохранить настройки памяти. Проверьте доступ к папке Solaris.";
        }
    }

    private int GetRamForMode(string mode) =>
        mode == "modded" ? _moddedRamMb : _vanillaRamMb;

    private void VanillaRamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ramSettingsReady) return;
        _vanillaRamMb = (int)e.NewValue;
        RefreshMemoryLabels();
        if (!_loadingRamSettings) SaveRamSettings();
    }

    private void ModdedRamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ramSettingsReady) return;
        _moddedRamMb = (int)e.NewValue;
        RefreshMemoryLabels();
        if (!_loadingRamSettings) SaveRamSettings();
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        NewsView.Visibility = Visibility.Collapsed;
        ProfileView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }

    private void ProfileNav_Click(object sender, RoutedEventArgs e)
    {
        NewsView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        ProfileView.Visibility = Visibility.Visible;
        _ = RefreshActivityAndProfileAsync(WelcomeText.Text);
        _ = PrepareSkinPreviewAsync(WelcomeText.Text);
    }

    private void ProfileAvatar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ProfileNav_Click(sender, new RoutedEventArgs());
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e)
    {
        NewsView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        ProfileView.Visibility = Visibility.Collapsed;
        ControlView.Visibility = Visibility.Collapsed;
        if (AuthView.Visibility != Visibility.Visible)
            MainView.Visibility = Visibility.Visible;
    }
}
