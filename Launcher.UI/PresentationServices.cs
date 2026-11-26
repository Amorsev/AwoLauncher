using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Launcher.UI;

public sealed class LocalizationService
{
    private readonly ResourceManager resources = new("Launcher.UI.Resources.Strings", typeof(LocalizationService).Assembly);
    private CultureInfo culture = CultureInfo.GetCultureInfo("ru");
    public string Text(string key) => resources.GetString(key, culture) ?? key;
    public void Apply(string language)
    {
        culture = CultureInfo.GetCultureInfo(language == "en" ? "en" : "ru");
        var dictionary = new ResourceDictionary();
        foreach (System.Collections.DictionaryEntry item in resources.GetResourceSet(
                     CultureInfo.InvariantCulture, true, true)!)
            dictionary["L_" + item.Key] = Text((string)item.Key);
        if (current is not null) Application.Current.Resources.MergedDictionaries.Remove(current);
        Application.Current.Resources.MergedDictionaries.Add(dictionary);
        current = dictionary;
    }
    private ResourceDictionary? current;
}

public sealed class ThemeService : IDisposable
{
    private ResourceDictionary? current;
    private string mode = "System";
    private Brush accent = Brushes.MediumSlateBlue;

    public ThemeService() => SystemEvents.UserPreferenceChanged += PreferenceChanged;

    public void Apply(string theme, string accentColor)
    {
        mode = theme;
        accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentColor));
        Apply();
    }

    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (mode == "System" && Application.Current is { } app)
            _ = app.Dispatcher.InvokeAsync(Apply);
    }

    private void Apply()
    {
        var light = mode == "Light" || mode == "System" &&
            (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) as int? ?? 1) != 0;
        var resources = Application.Current.Resources;
        if (current is not null) resources.MergedDictionaries.Remove(current);
        var variant = new ResourceDictionary
        {
            Source = new Uri($"/Launcher.UI;component/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative)
        };
        variant["Accent"] = accent;
        variant["Primary"] = accent;
        var color = ((SolidColorBrush)accent).Color;
        var luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        variant["AccentText"] = luminance > .179 ? Brushes.Black : Brushes.White;
        resources.MergedDictionaries.Add(variant);
        current = variant;
    }

    private static double Linear(byte value)
    {
        var channel = value / 255d;
        return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= PreferenceChanged;
}

public sealed class ToastService
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(4) };
    public string? Text { get; private set; }
    public event EventHandler? Changed;

    public ToastService() => timer.Tick += (_, _) => Set(null);

    public void Show(string text) => Set(text);

    private void Set(string? text)
    {
        timer.Stop();
        Text = text;
        Changed?.Invoke(this, EventArgs.Empty);
        if (text is not null) timer.Start();
    }
}
