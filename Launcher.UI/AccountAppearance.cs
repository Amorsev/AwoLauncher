using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.UI;

public sealed record SavedSkin(string Name, string Path);
public sealed record SkinTile(SavedSkin Skin, ImageSource Preview, bool Active);

public sealed partial class MainViewModel
{
    private SkinDeployment skinDeployment = null!;
    private ImageSource? localSkinHead;
    private ImageSource? localSkinBody;
    private string pendingBackgroundFit = "Fill";
    private double pendingBackgroundBlur = 10;
    private double pendingBackgroundShade = .36;

    public ICommand ImportSkin { get; private set; } = null!;
    public ICommand ClearSkin { get; private set; } = null!;
    public ICommand ApplySkin { get; private set; } = null!;
    public ICommand RemoveAppliedSkin { get; private set; } = null!;
    public ICommand SelectSkin { get; private set; } = null!;
    public ICommand OpenSkins { get; private set; } = null!;
    public ICommand OpenGallery { get; private set; } = null!;
    public ICommand OpenAccounts { get; private set; } = null!;
    public ICommand OpenScreenshot { get; private set; } = null!;
    public int SkinSection { get; set; }
    public ObservableCollection<SkinTile> SkinTiles { get; } = [];
    private readonly Dictionary<string, ImageSource> skinPreviews = new(StringComparer.OrdinalIgnoreCase);
    private string SkinLibraryPath => Path.Combine(data.Root, "appearance", "library.json");
    public string RenameAccountInput { get; set; } = "";
    public bool HasAccount => Account is not null;
    public ImageSource? LocalSkinHead => localSkinHead;
    public ImageSource? LocalSkinBody => localSkinBody;
    public bool HasLocalSkin => localSkinBody is not null;
    public string SkinState => T(HasLocalSkin ? "SkinImported" : "SkinDefault");
    public bool HasAppliedSkin => Account is { } profile && SelectedInstance is { } instance &&
        skinDeployment.IsApplied(instance, profile);
    public ImageSource? BackgroundImage { get; private set; }
    private BitmapSource? pendingBackgroundImage;
    public BitmapSource? PendingBackgroundImage
    {
        get => pendingBackgroundImage;
        private set { pendingBackgroundImage = value; Notify(); Notify(nameof(CanSetInterfaceTransparency)); }
    }
    private bool pendingBackgroundWindow;
    public bool PendingBackgroundWindow
    {
        get => pendingBackgroundWindow;
        set { pendingBackgroundWindow = value; Notify(); Notify(nameof(CanSetInterfaceTransparency)); }
    }
    public bool CanSetInterfaceTransparency => PendingBackgroundWindow && PendingBackgroundImage is not null;
    public double PendingInterfaceTransparency { get; set; }
    public Choice[] BackgroundFits => [new(T("FillBackground"), "Fill"), new(T("FitBackground"), "Fit")];
    public bool HasWindowBackground => settingsService.Current.BackgroundWindow && BackgroundImage is not null;
    public Stretch BackgroundStretch => ToStretch(settingsService.Current.BackgroundFit);
    public double BackgroundBlur => Math.Clamp(settingsService.Current.BackgroundBlur, 0, 24);
    public double BackgroundShade => Math.Clamp(settingsService.Current.BackgroundShade, 0, .8);
    public Stretch PendingBackgroundStretch => ToStretch(PendingBackgroundFit);
    public string PendingBackgroundFit
    {
        get => pendingBackgroundFit;
        set
        {
            if (value is not ("Fill" or "Fit")) return;
            pendingBackgroundFit = value;
            Notify();
            Notify(nameof(PendingBackgroundStretch));
        }
    }
    public double PendingBackgroundBlur
    {
        get => pendingBackgroundBlur;
        set { pendingBackgroundBlur = value; Notify(); }
    }
    public double PendingBackgroundShade
    {
        get => pendingBackgroundShade;
        set { pendingBackgroundShade = value; Notify(); }
    }

    private static Stretch ToStretch(string mode) => mode == "Fit" ? Stretch.Uniform : Stretch.UniformToFill;

    private void InitializeAccountAppearance()
    {
        skinDeployment = new(data, api, content);
        OpenSkins = new ActionCommand(() => Section = 6, () => true);
        OpenGallery = new ActionCommand(() => Section = 7, () => true);
        OpenAccounts = new ActionCommand(() => Section = 3, () => true);
        OpenScreenshot = new ParameterCommand<ScreenshotItem>(item => TryLocal(() => OpenFile(item.Path)),
            item => Screenshots.Contains(item) && File.Exists(item.Path));
        ImportSkin = Command(token =>
        {
            var picker = new OpenFileDialog { Filter = "Minecraft PNG (*.png)|*.png", CheckFileExists = true };
            return picker.ShowDialog() == true ? ImportSkinFileAsync(picker.FileName, token) : Task.CompletedTask;
        }, () => HasAccount);
        SelectSkin = new ParameterCommand<SkinTile>(tile =>
        {
            if (Account is { } profile && tile is not null)
                _ = RunAsync(_ =>
                {
                    ReplaceProfile(profile, profile with { SkinPath = tile.Skin.Path });
                    toasts.Show(T("SkinImported"));
                    return Task.CompletedTask;
                });
        }, tile => !Busy && HasAccount && tile is { Active: false });
        ClearSkin = Command(_ =>
        {
            if (Account is { } profile) ReplaceProfile(profile, profile with { SkinPath = null });
            return Task.CompletedTask;
        }, () => HasLocalSkin);
        ApplySkin = Command(async token =>
        {
            var target = RequireInstance();
            var profile = Account ?? throw new InvalidOperationException(T("NoAccount"));
            var message = string.Format(T("ApplySkinConfirm"), target.Name);
            if (skinDeployment.IsApplied(target, profile)) message += "\n\n" + T("ReplaceGameSkin");
            if (!Confirm(message)) return;
            await skinDeployment.InstallAndSyncAsync(target, profile, Progress(), token);
            Notify(nameof(HasAppliedSkin));
            CommandManager.InvalidateRequerySuggested();
            RefreshMods();
            _ = RefreshCardsAsync();
            toasts.Show(T("SkinApplied"));
        }, () => HasLocalSkin && SelectedInstance is { Loader: not Loader.Vanilla });
        RemoveAppliedSkin = Command(_ =>
        {
            if (Account is { } profile && SelectedInstance is { } target && Confirm(T("RemoveGameSkinConfirm")))
            {
                skinDeployment.Remove(target, profile);
                Notify(nameof(HasAppliedSkin));
                toasts.Show(T("GameSkinRemoved"));
            }
            return Task.CompletedTask;
        }, () => HasAppliedSkin);
    }

    public Task DropSkinAsync(string path) => HasAccount && !Busy
        ? RunAsync(token => ImportSkinFileAsync(path, token)) : Task.CompletedTask;

    private async Task ImportSkinFileAsync(string file, CancellationToken token)
    {
        if (Account is not { } profile) return;
        var path = await Task.Run(() =>
        {
            var skin = DecodeSelected(file, true);
            token.ThrowIfCancellationRequested();
            return AppearanceImages.SaveImage(data, skin, true);
        }, token);
        token.ThrowIfCancellationRequested();
        var library = LocalData.Read(SkinLibraryPath, Array.Empty<SavedSkin>());
        if (!library.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            LocalData.Write(SkinLibraryPath, (SavedSkin[])[.. library, new(Path.GetFileNameWithoutExtension(file), path)]);
        ReplaceProfile(profile, profile with { SkinPath = path });
        toasts.Show(T("SkinImported"));
    }

    private void RefreshSkinLibrary()
    {
        SkinTiles.Clear();
        SavedSkin[] library;
        try { library = LocalData.Read(SkinLibraryPath, Array.Empty<SavedSkin>()); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            library = [];
        }
        foreach (var item in library.Concat(data.Profiles.Profiles.Where(profile => profile.SkinPath is not null)
            .Select(profile => new SavedSkin(profile.Nick, profile.SkinPath!))).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!skinPreviews.TryGetValue(item.Path, out var preview))
            {
                if (ReadImage(item.Path, true) is not { } bitmap) continue;
                preview = skinPreviews[item.Path] = AppearanceImages.SkinPreview(bitmap).Body;
            }
            SkinTiles.Add(new(item, preview, string.Equals(Account?.SkinPath, item.Path, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private BitmapSource DecodeSelected(string path, bool skin)
    {
        try { return AppearanceImages.Decode(path, skin); }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException or FormatException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(T(skin ? "InvalidSkinPng" : "InvalidBackground"), error);
        }
    }

    private void ReplaceProfile(Profile before, Profile after)
    {
        if (before.SkinPath is { } old && old != after.SkinPath)
        {
            var library = LocalData.Read(SkinLibraryPath, Array.Empty<SavedSkin>());
            if (!library.Any(item => item.Path.Equals(old, StringComparison.OrdinalIgnoreCase)))
                LocalData.Write(SkinLibraryPath, (SavedSkin[])[.. library, new(before.Nick, old)]);
        }
        var active = Account == before ? after.Nick : Account?.Nick;
        var state = new ProfileState(Profiles.Select(profile => profile == before ? after : profile).ToArray(), active);
        data.SaveProfiles(state);
        RefreshAccounts(state);
    }

    private void RefreshAccountAppearance()
    {
        RenameAccountInput = Account?.Nick ?? "";
        localSkinHead = localSkinBody = null;
        if (Account?.SkinPath is { } path && ReadImage(path, true) is { } bitmap)
            (localSkinHead, localSkinBody) = AppearanceImages.SkinPreview(bitmap);
        RefreshSkinLibrary();
        foreach (var property in new[] { nameof(RenameAccountInput), nameof(LocalSkinHead), nameof(LocalSkinBody),
                     nameof(HasAccount), nameof(HasLocalSkin), nameof(SkinState), nameof(HasAppliedSkin) }) Notify(property);
        CommandManager.InvalidateRequerySuggested();
    }

    private static BitmapSource? ReadImage(string? path, bool skin)
    {
        if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        try { return AppearanceImages.Decode(path, skin); }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException or FormatException or UnauthorizedAccessException)
        { return null; }
    }

    private void LoadBackground(UserSettings settings)
    {
        BackgroundImage = PendingBackgroundImage = ReadImage(settings.Background, false);
        PendingBackground = settings.Background;
        PendingBackgroundFit = settings.BackgroundFit == "Fit" ? "Fit" : "Fill";
        PendingBackgroundBlur = Math.Clamp(settings.BackgroundBlur, 0, 24);
        PendingBackgroundShade = Math.Clamp(settings.BackgroundShade, 0, .8);
        PendingBackgroundWindow = settings.BackgroundWindow;
        PendingInterfaceTransparency = Math.Clamp(settings.InterfaceTransparency, 0, .6);
        foreach (var property in new[] { nameof(BackgroundFits), nameof(BackgroundImage), nameof(PendingBackgroundImage),
            nameof(PendingBackground), nameof(PendingBackgroundWindow), nameof(HasWindowBackground), nameof(BackgroundStretch),
            nameof(BackgroundBlur), nameof(BackgroundShade), nameof(PendingInterfaceTransparency) }) Notify(property);
    }
}
