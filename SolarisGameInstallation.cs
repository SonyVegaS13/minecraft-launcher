using System;
using CmlLib.Core;

namespace SolarisLauncher;

public partial class MainWindow
{
    private void WireMinecraftProgress(MinecraftLauncher launcher, string phase,
        double percentFrom, double percentTo)
    {
        // CmlLib download notifications include bytes downloaded and total bytes.
        // Update only at most 4 times per second to keep WPF responsive.
        DateTime lastUiUpdate = DateTime.MinValue;
        launcher.ByteProgressChanged += (_, e) =>
        {
            if ((DateTime.UtcNow - lastUiUpdate).TotalMilliseconds < 250)
                return;
            lastUiUpdate = DateTime.UtcNow;
            if (e.TotalBytes <= 0) return;
            double fraction = Math.Clamp((double)e.ProgressedBytes / e.TotalBytes, 0, 1);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Progress.Value = percentFrom + (percentTo - percentFrom) * fraction;
                IgnitionPercent.Text = $"{fraction * 100:0}%";
                StatusText.Text = fraction < 1
                    ? $"{phase}: загрузка Minecraft, библиотек и Java — " +
                        $"{e.ProgressedBytes / 1048576} / {e.TotalBytes / 1048576} МБ"
                    : $"{phase}: файлы проверены, готовим запуск...";
            }));
        };
    }
}
