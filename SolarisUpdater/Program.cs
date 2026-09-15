using System.Diagnostics;

if (args.Length < 2)
    return;

string launcherPath = Path.GetFullPath(args[0]);
string newLauncherPath = Path.GetFullPath(args[1]);

try
{
    await Task.Delay(1500);

    bool released = false;

    for (int i = 0; i < 30; i++)
    {
        try
        {
            using FileStream stream = new(
                launcherPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            released = true;
            break;
        }
        catch
        {
            await Task.Delay(500);
        }
    }

    if (!released)
        throw new IOException("Не удалось дождаться освобождения SolarisLauncher.exe.");

    File.Copy(newLauncherPath, launcherPath, true);
    TryDeleteFile(newLauncherPath);

    Process.Start(new ProcessStartInfo
    {
        FileName = launcherPath,
        WorkingDirectory = Path.GetDirectoryName(launcherPath)!,
        UseShellExecute = true
    });
}
catch
{
    // Updater exits silently if replacement fails.
}

static void TryDeleteFile(string path)
{
    try
    {
        if (File.Exists(path))
            File.Delete(path);
    }
    catch
    {
    }
}
