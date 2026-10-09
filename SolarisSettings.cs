using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SolarisLauncher;

public partial class MainWindow
{
    private int _vanillaRamMb = 3072;
    private int _moddedRamMb = 4096;
    private bool _loadingRamSettings;
    private string RamSettingsPath => Path.Combine(_stateDir, "settings.json");

    private sealed class LocalDeviceSettings
    {
        public int VanillaRamMb { get; set; } = 3072;
        public int ModdedRamMb { get; set; } = 4096;
    }

    private static int AvailableRamLimit()
    {
        // Use the total memory visible to the .NET process as a conservative cap;
        // always leave at least 2 GiB for the OS and other processes.
        long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long totalMb = bytes > 0 ? bytes / (1024 * 1024) : 8192;
        return (int)Math.Clamp(totalMb - 2048, 2048, 16384);
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
        finally { _loadingRamSettings = false; }
    }

    private void RefreshMemoryLabels()
    {
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
        if (VanillaRamText is null) return;
        _vanillaRamMb = (int)e.NewValue;
        RefreshMemoryLabels();
        if (!_loadingRamSettings) SaveRamSettings();
    }

    private void ModdedRamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ModdedRamText is null) return;
        _moddedRamMb = (int)e.NewValue;
        RefreshMemoryLabels();
        if (!_loadingRamSettings) SaveRamSettings();
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        ProfileView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }

    private void ProfileNav_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        ProfileView.Visibility = Visibility.Visible;
        _ = RefreshActivityAndProfileAsync(WelcomeText.Text);
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        ProfileView.Visibility = Visibility.Collapsed;
    }
}
