using System.IO;
using System.Text.Json;

namespace GpdUiSnap;

internal sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GpdUiSnap",
        "settings.json");

    public int ActivationVk { get; set; } = NativeMethods.VK_F15;
    public ActivationSource ActivationSource { get; set; } = ActivationSource.KeyboardOnly;
    public ushort ActivationMouseDownFlag { get; set; } = 0x0001;
    public ushort ActivationMouseUpFlag { get; set; } = 0x0002;
    public bool ClickOnRelease { get; set; } = true;
    public NavigationMode NavigationMode { get; set; } = NavigationMode.OverlayThenKeyboard;
    public KeyboardFallback KeyboardFallback { get; set; } = KeyboardFallback.TabOrder;
    public int MouseVectorThreshold { get; set; } = 3;
    public int NavigateRepeatMs { get; set; } = 65;
    public int KeyboardRepeatMs { get; set; } = 110;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    if (settings.ActivationVk is NativeMethods.VK_F9 or NativeMethods.VK_SCROLL)
                        settings.ActivationVk = NativeMethods.VK_F13;
                    settings.MouseVectorThreshold = Math.Min(settings.MouseVectorThreshold, 3);
                    settings.NavigateRepeatMs = Math.Min(settings.NavigateRepeatMs, 65);
                    settings.KeyboardRepeatMs = Math.Min(settings.KeyboardRepeatMs, 110);
                    settings.ClickOnRelease = true;
                    return settings;
                }
            }
        }
        catch
        {
            // Bad settings should not stop the navigation layer from starting.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings persistence is optional.
        }
    }
}

internal enum NavigationMode
{
    OverlayThenKeyboard,
    KeyboardTab,
    KeyboardArrows,
}

internal enum KeyboardFallback
{
    TabOrder,
    Arrows,
}

internal enum ActivationSource
{
    KeyboardOnly,
    KeyboardOrGpdMouseButton,
}
