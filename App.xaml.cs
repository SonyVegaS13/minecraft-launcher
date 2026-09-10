using System;
using System.Windows;

namespace SolarisLauncher;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(
                e.Exception.ToString(),
                "SolarisLauncher — ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
