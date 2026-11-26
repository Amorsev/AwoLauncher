namespace Launcher.Core;

public sealed record UserSettings(string Theme = "System", string Accent = "#6944D2", string Language = "ru",
    string? Background = null, bool DiscordEnabled = false, string DiscordApplicationId = "",
    int LogRetentionDays = 30, string? DataDirectory = null, bool ExternalAvatars = false,
    string BackgroundFit = "Fill", double BackgroundBlur = 10, double BackgroundShade = .36, bool BackgroundWindow = false,
    double InterfaceTransparency = 0);

public sealed class SettingsService
{
    private readonly string path;
    public UserSettings Current { get; private set; }
    public event EventHandler? Changed;

    public SettingsService(Settings defaults)
    {
        path = Path.Combine(new LocalData(defaults).Root, "settings.json");
        Current = LocalData.Read(path, new UserSettings());
    }

    public void Save(UserSettings settings)
    {
        LocalData.Write(path, settings);
        Current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
