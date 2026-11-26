using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.UI;

public sealed partial class MainViewModel
{
    private readonly LocalizationService localization = new();
    private readonly ThemeService theme = new();
    private readonly ToastService toasts = new();
    private readonly JavaDetector javaDetector = new();
    private readonly CrashAnalyzer crashAnalyzer = new();
    private InstanceService instancesService = null!;
    private PackUpdates packUpdates = null!;
    private SettingsService settingsService = null!;
    private CancellationTokenSource? cardsScan;
    private CancellationTokenSource? packScan;
    private ModVersion? availablePack;
    private int section;
    private string logLevel = "All";
    private bool catalogBusy;
    private string? playServer;
    private int playPort = 25565;

    public ICollectionView FilteredLog { get; private set; } = null!;
    public string[] LogLevels { get; } = ["All", "Info", "Warn", "Error"];
    public string LogLevel { get => logLevel; set { logLevel = value; FilteredLog.Refresh(); } }
    public bool AutoScroll { get; set; } = true;
    public ObservableCollection<InstanceCard> Cards { get; } = [];
    public InstanceCard[] RecentInstances => Cards.Where(card => card.Preferences.LastPlayed.HasValue)
        .OrderByDescending(card => card.Preferences.LastPlayed).Take(10).ToArray();
    public bool HasRecentInstances => RecentInstances.Length > 0;
    public ICollectionView LibraryCards { get; private set; } = null!;
    private string instanceQuery = "";
    public string InstanceQuery
    {
        get => instanceQuery;
        set { instanceQuery = value ?? ""; LibraryCards.Refresh(); Notify(nameof(LibraryEmpty)); Notify(nameof(SelectedCard)); }
    }
    public bool LibraryEmpty => LibraryCards.IsEmpty;
    private bool showCreation;
    public bool ShowCreation { get => showCreation; private set { showCreation = value; Notify(); } }
    public ObservableCollection<SavedServer> Servers { get; } = [];
    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];
    public ObservableCollection<ScreenshotItem> Screenshots { get; } = [];
    public string[] Themes { get; } = ["System", "Light", "Dark"];
    public string[] Languages { get; } = ["ru", "en"];
    public InstanceCard? SelectedCard
    {
        get => Cards.FirstOrDefault(card => card.Instance.Id == SelectedInstance?.Id);
        set { if (value is not null && Instances.FirstOrDefault(instance => instance.Id == value.Instance.Id) is { } current) SelectedInstance = current; }
    }
    public int Section
    {
        get => section;
        set
        {
            if (section == value) return;
            section = value;
            Notify();
            Notify(nameof(PageTitle));
            if (value == 1 && !catalogLoaded && !CatalogBusy) QueueCatalog();
        }
    }
    public string PageTitle => T(Section switch
    {
        1 => "Catalog", 2 => "Instances", 3 => "Accounts", 4 => "Settings", 5 => "InstanceSettings",
        6 => "Skins", 7 => "Screenshots", _ => "Play"
    });
    public bool HasNoInstance => SelectedInstance is null;
    public string WindowTitle => SelectedInstance is { } instance ? $"AwoLauncher · {instance.Name}" : "AwoLauncher";
    public string ActiveMetadata => SelectedInstance is { } instance
        ? $"{instance.GameVersion} · {instance.Loader} · {instance.Ram} MB" : "";
    public string AccountLabel => Account?.Nick ?? T("NoAccount");
    public string AccountInitial => Account?.Nick[..1].ToUpperInvariant() ?? "?";
    public string? AvatarUrl => settingsService.Current.ExternalAvatars && Account is { } profile
        ? $"https://mc-heads.net/avatar/{Uri.EscapeDataString(profile.Nick)}/64" : null;
    public string? SkinUrl => settingsService.Current.ExternalAvatars && Account is { } profile
        ? $"https://mc-heads.net/body/{Uri.EscapeDataString(profile.Nick)}/160" : null;
    public string? Toast => toasts.Text;
    public bool HasToast => Toast is not null;
    public bool CatalogBusy { get => catalogBusy; private set { catalogBusy = value; Notify(); } }
    public bool PackUpdateAvailable => availablePack is not null;
    public string PackUpdateLabel => availablePack is { } version ? $"{T("UpdateAvailable")} · {version.Name}" : "";
    public string PackName => SelectedInstance is { } instance ? packUpdates.Origin(instance.Id)?.VersionName ?? "" : "";
    public string TotalPlayTime => $"{Cards.Sum(card => card.Preferences.PlaySeconds) / 3600d:0.0} h";
    public string JavaHint => SelectedInstance is { } instance && JavaDetector.SuggestedMajor(instance.GameVersion) is { } major
        ? string.Format(T("JavaHint"), major) : T("JavaAutomatic");
    public double InstanceRam { get; set; } = 4096;
    public string? JavaPath { get; set; }
    public string? InstanceCover { get; private set; }
    public string PendingTheme { get; set; } = "System";
    public string PendingAccent { get; set; } = "#6944D2";
    public string PendingLanguage { get; set; } = "ru";
    public string? PendingBackground { get; private set; }
    public string PendingDirectory { get; set; } = "";
    public bool PendingDiscord { get; set; }
    public bool PendingAvatars { get; set; }
    public string PendingDiscordId { get; set; } = "";
    public string PendingRetention { get; set; } = "30";
    public string ServerName { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public SavedServer? SelectedServer { get; set; }
    public string ServerStatus { get; private set; } = "";
    public string Breadcrumb => Details is null ? T("Catalog") : $"{Details.Type}  ›  {Details.Title}" +
        (Version is null ? "" : $"  ›  {Version.Number}");
    public string CurrentVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    public string UpdateStatus { get; private set; } = "";
    private LauncherRelease? launcherRelease;
    private UpdateService launcherUpdates = null!;

    public ICommand CycleInstance { get; private set; } = null!;
    public ICommand OpenInstances { get; private set; } = null!;
    public ICommand NewInstance { get; private set; } = null!;
    public ICommand CloseCreation { get; private set; } = null!;
    public ICommand SelectRecent { get; private set; } = null!;
    public ICommand LaunchRecent { get; private set; } = null!;
    public ICommand OpenInstanceSettings { get; private set; } = null!;
    public ICommand SaveInstance { get; private set; } = null!;
    public ICommand DuplicateInstance { get; private set; } = null!;
    public ICommand ExportInstance { get; private set; } = null!;
    public ICommand BrowseCover { get; private set; } = null!;
    public ICommand FindJava { get; private set; } = null!;
    public ICommand InstallJava { get; private set; } = null!;
    public ICommand OpenScreenshots { get; private set; } = null!;
    public ICommand SavePreferences { get; private set; } = null!;
    public ICommand BrowseBackground { get; private set; } = null!;
    public ICommand ClearBackground { get; private set; } = null!;
    public ICommand ChangeNick { get; private set; } = null!;
    public ICommand DeleteAccount { get; private set; } = null!;
    public ICommand CheckPack { get; private set; } = null!;
    public ICommand UpdatePack { get; private set; } = null!;
    public ICommand AddServer { get; private set; } = null!;
    public ICommand RemoveServer { get; private set; } = null!;
    public ICommand PingServer { get; private set; } = null!;
    public ICommand JoinServer { get; private set; } = null!;
    public ICommand Recommendations { get; private set; } = null!;
    public ICommand CheckLauncher { get; private set; } = null!;
    public ICommand InstallLauncher { get; private set; } = null!;

    private string T(string key) => localization.Text(key);

    private void InitializeProduct(Settings defaults, SettingsService preferences, System.Net.Http.HttpClient http)
    {
        settingsService = preferences;
        instancesService = new(data);
        InitializeAccountAppearance();
        packUpdates = new(data, api, game, defaults);
        launcherUpdates = new(http, defaults.UpdateFeed);
        FilteredLog = CollectionViewSource.GetDefaultView(Log);
        FilteredLog.Filter = item => LogLevel == "All" || item is string line && Severity(line) == LogLevel;
        toasts.Changed += (_, _) => { Notify(nameof(Toast)); Notify(nameof(HasToast)); };
        LoadPreferences(preferences.Current);
        preferences.Changed += (_, _) => LoadPreferences(preferences.Current);
        CycleInstance = new ActionCommand(() =>
        {
            var index = SelectedInstance is null ? -1 : Instances.IndexOf(SelectedInstance);
            SelectedInstance = Instances[(index + 1) % Instances.Count];
        }, () => Instances.Count > 0);
        OpenInstances = new ActionCommand(() => Section = 2, () => true);
        LibraryCards = CollectionViewSource.GetDefaultView(Cards);
        LibraryCards.Filter = item => item is InstanceCard card &&
            (card.Name.Contains(InstanceQuery.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
             card.Version.Contains(InstanceQuery.Trim(), StringComparison.CurrentCultureIgnoreCase));
        NewInstance = new ActionCommand(() => { ShowCreation = true; Section = 2; }, () => true);
        CloseCreation = new ActionCommand(() => ShowCreation = false, () => true);
        SelectRecent = new ParameterCommand<InstanceCard>(card =>
        {
            if (Instances.FirstOrDefault(instance => instance.Id == card.Instance.Id) is not { } instance) return;
            SelectedInstance = instance;
            OpenInstanceWorkspace(0);
        }, card => Instances.Any(instance => instance.Id == card.Instance.Id));
        LaunchRecent = new ParameterCommand<InstanceCard>(card => RunCardAction(card, "Play"),
            card => !Busy && Instances.Any(instance => instance.Id == card.Instance.Id));
        OpenInstanceSettings = new ActionCommand(() => OpenInstanceWorkspace(4), () => SelectedInstance is not null);
        InitializeWorkspace();
        SaveInstance = Command(_ =>
        {
            var instance = RequireInstance();
            var current = instancesService.Preferences(instance.Id);
            var java = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath.Trim();
            if (java is not null && (!Path.IsPathFullyQualified(java) || !File.Exists(java) ||
                !new[] { "java.exe", "javaw.exe" }.Contains(Path.GetFileName(java), StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException(T("InvalidJava"));
            instancesService.Save(instance with { Ram = (int)InstanceRam }, current with { Java = java, Cover = InstanceCover });
            RefreshInstances(instance.Id);
            RefreshSelection();
            return Task.CompletedTask;
        }, () => SelectedInstance is not null);
        DuplicateInstance = Command(async token =>
        {
            var source = RequireInstance();
            var duplicate = await Task.Run(() => instancesService.DuplicateAsync(source, token), token);
            RefreshInstances(duplicate.Id);
        }, () => SelectedInstance is not null);
        ExportInstance = Command(async token =>
        {
            var instance = RequireInstance();
            var dialog = new SaveFileDialog { Filter = "ZIP (*.zip)|*.zip", FileName = "minecraft-instance.zip", AddExtension = true };
            if (dialog.ShowDialog() == true)
                await Task.Run(() => instancesService.ExportAsync(instance, dialog.FileName, token), token);
        }, () => SelectedInstance is not null);
        BrowseCover = new ActionCommand(() => { if (PickImage() is { } file) { InstanceCover = file; Notify(nameof(InstanceCover)); } }, () => !Busy);
        FindJava = Command(async token =>
        {
            var list = await javaDetector.ScanAsync(data.Game(RequireInstance().Id), token, data.Runtime);
            JavaInstallations.Clear();
            foreach (var java in list) JavaInstallations.Add(java);
        }, () => SelectedInstance is not null);
        InstallJava = Command(async token =>
        {
            var instance = RequireInstance();
            var path = await game.InstallJavaAsync(instance, data.Game(instance.Id), Progress(), token);
            var list = await javaDetector.ScanAsync(data.Game(instance.Id), token, data.Runtime);
            if (SelectedInstance?.Id != instance.Id) return;
            JavaInstallations.Clear();
            foreach (var java in list) JavaInstallations.Add(java);
            Append($"Java · {path}");
            toasts.Show("✓ " + T("Installed"));
        }, () => SelectedInstance is not null);
        OpenScreenshots = Command(_ =>
        {
            var folder = Path.Combine(data.Game(RequireInstance().Id), "screenshots");
            Directory.CreateDirectory(folder);
            OpenFile(folder);
            return Task.CompletedTask;
        }, () => SelectedInstance is not null);
        BrowseBackground = Command(async token =>
        {
            if (PickImage() is not { } file) return;
            var preview = await Task.Run(() => DecodeSelected(file, false), token);
            token.ThrowIfCancellationRequested();
            PendingBackground = file;
            PendingBackgroundImage = preview;
            Notify(nameof(PendingBackground));
            Notify(nameof(PendingBackgroundImage));
        });
        ClearBackground = new ActionCommand(() =>
        {
            PendingBackground = null;
            PendingBackgroundImage = null;
            PendingBackgroundFit = "Fill";
            PendingBackgroundBlur = 10;
            PendingBackgroundShade = .36;
            PendingBackgroundWindow = false;
            PendingInterfaceTransparency = 0;
            Notify(nameof(PendingBackground));
            Notify(nameof(PendingBackgroundImage));
            Notify(nameof(PendingBackgroundWindow));
            Notify(nameof(PendingInterfaceTransparency));
        }, () => !Busy);
        SavePreferences = Command(async token =>
        {
            ParseAccent(PendingAccent);
            if (!int.TryParse(PendingRetention, out var days) || days is < 0 or > 3650)
                throw new InvalidOperationException(T("InvalidRetention"));
            var directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(PendingDirectory.Trim()));
            var directoryChanged = !directory.Equals(data.Root, StringComparison.OrdinalIgnoreCase);
            var background = PendingBackground;
            if (background != settingsService.Current.Background && PendingBackgroundImage is { } image)
                background = await Task.Run(() => AppearanceImages.SaveImage(data, image, false), token);
            token.ThrowIfCancellationRequested();
            settingsService.Save(new(PendingTheme, PendingAccent, PendingLanguage, background, PendingDiscord,
                PendingDiscordId.Trim(), days, directory, PendingAvatars, PendingBackgroundFit,
                PendingBackgroundBlur, PendingBackgroundShade, PendingBackgroundWindow, PendingInterfaceTransparency));
            if (directoryChanged) toasts.Show(T("RestartDirectory"));
        });
        ChangeNick = Command(_ =>
        {
            var current = Account ?? throw new InvalidOperationException(T("NoAccount"));
            var nickname = RenameAccountInput.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(nickname, @"\A[A-Za-z0-9_]{3,16}\z"))
                throw new InvalidOperationException(T("InvalidNick"));
            if (Profiles.Any(profile => profile != current && profile.Nick.Equals(nickname, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(T("DuplicateNick"));
            var state = new ProfileState(Profiles.Select(profile => profile == current ? profile with { Nick = nickname } : profile).ToArray(), nickname);
            data.SaveProfiles(state);
            RefreshAccounts(state);
            return Task.CompletedTask;
        }, () => Account is not null);
        DeleteAccount = Command(_ =>
        {
            if (Account is not { } current || !Confirm(string.Format(T("DeleteAccountConfirm"), current.Nick),
                T("DeleteAccount"), T("Remove"), true))
                return Task.CompletedTask;
            var remaining = Profiles.Where(profile => profile != current).ToArray();
            var state = new ProfileState(remaining, remaining.FirstOrDefault()?.Nick);
            data.SaveProfiles(state);
            RefreshAccounts(state);
            return Task.CompletedTask;
        }, () => Account is not null);
        CheckPack = Command(async token =>
        {
            var target = RequireInstance();
            var origin = packUpdates.Origin(target.Id) ?? throw new InvalidOperationException(T("NoPackOrigin"));
            var next = await packUpdates.CheckAsync(origin, token);
            if (SelectedInstance?.Id != target.Id) return;
            availablePack = next;
            NotifyPack();
            toasts.Show(next is null ? T("UpToDate") : PackUpdateLabel);
        }, () => SelectedInstance is not null);
        UpdatePack = Command(async token =>
        {
            var target = RequireInstance();
            var origin = packUpdates.Origin(target.Id) ?? throw new InvalidOperationException(T("NoPackOrigin"));
            if (availablePack is not { } next) return;
            var result = await packUpdates.ApplyAsync(target, origin, next, files => Task.FromResult(ChooseFiles(files)),
                changes => Task.FromResult(ShowChanges(changes, next.Name)), Progress(), token);
            if (result is not null)
            {
                RefreshInstances(result.Id);
                RefreshSelection();
                toasts.Show("✓ " + T("Installed"));
            }
        }, () => SelectedInstance is not null && availablePack is not null);
        foreach (var server in LocalData.Read(ServerFile, Array.Empty<SavedServer>())) Servers.Add(server);
        AddServer = Command(_ =>
        {
            if (!Uri.TryCreate("tcp://" + ServerAddress.Trim(), UriKind.Absolute, out var address) ||
                address.Host.Length == 0 || address.AbsolutePath != "/" || address.UserInfo.Length != 0 ||
                address.Query.Length != 0 || address.Fragment.Length != 0 || address.Port == 0)
                throw new InvalidOperationException(T("InvalidServer"));
            var host = address.IdnHost.Trim('[', ']');
            Servers.Add(new(string.IsNullOrWhiteSpace(ServerName) ? host : ServerName.Trim(), host, address.Port < 0 ? 25565 : address.Port));
            SaveServers();
            return Task.CompletedTask;
        });
        RemoveServer = Command(_ => { if (SelectedServer is { } server) { Servers.Remove(server); SaveServers(); } return Task.CompletedTask; });
        PingServer = Command(async token =>
        {
            if (SelectedServer is not { } server) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var milliseconds = await ServerPing.PingAsync(server.Host, server.Port, timeout.Token);
                ServerStatus = $"{milliseconds} ms";
            }
            catch (Exception e) when (!token.IsCancellationRequested &&
                                      e is SocketException or OperationCanceledException or IOException)
            {
                ServerStatus = T("ServerUnavailable");
            }
            Notify(nameof(ServerStatus));
        });
        JoinServer = Command(async token =>
        {
            if (SelectedServer is not { } server) return;
            playServer = server.Host;
            playPort = server.Port;
            try { await PlayAsync(token); }
            finally { playServer = null; playPort = 25565; }
        }, () => SelectedInstance is not null);
        Recommendations = Command(async token =>
        {
            var target = RequireInstance();
            if (target.Loader == Loader.Vanilla) throw new InvalidOperationException(T("NeedsLoader"));
            if (!Confirm(T("InstallPerformanceConfirm"))) return;
            foreach (var id in new[] { "sodium", "lithium", "ferrite-core", "iris" })
            {
                var version = (await api.VersionsAsync(id, token)).Where(item => Modrinth.Compatible(item, target, "mod"))
                    .OrderBy(item => item.Type == "release" ? 0 : 1).ThenByDescending(item => item.Published).FirstOrDefault();
                if (version is null) continue;
                try { await content.InstallAsync(target, await api.ProjectAsync(id, token), version, Progress(), token); }
                catch (InvalidOperationException error) { Append("[WARN] " + error.Message); }
            }
            if (SelectedInstance?.Id == target.Id) RefreshMods();
            _ = RefreshCardsAsync();
        }, () => SelectedInstance is not null);
        CheckLauncher = Command(async token =>
        {
            if (!launcherUpdates.Configured) { UpdateStatus = T("NoUpdateFeed"); Notify(nameof(UpdateStatus)); return; }
            launcherRelease = await launcherUpdates.CheckAsync(System.Version.Parse(CurrentVersion), token);
            UpdateStatus = launcherRelease is null ? T("UpToDate") : $"{T("UpdateAvailable")} · {launcherRelease.Version}";
            Notify(nameof(UpdateStatus));
            toasts.Show(UpdateStatus);
        });
        InstallLauncher = Command(async token =>
        {
            if (launcherRelease is not { } release || !Confirm(T("InstallLauncherConfirm"))) return;
            await launcherUpdates.InstallAsync(release, data.Root, Progress(), token);
            Application.Current.Shutdown();
        }, () => launcherRelease is not null);
        _ = Task.Run(CleanOldLogs);
        if (launcherUpdates.Configured) _ = CheckConfiguredUpdateAsync();
    }

    private async Task CheckConfiguredUpdateAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            launcherRelease = await launcherUpdates.CheckAsync(System.Version.Parse(CurrentVersion), timeout.Token);
            if (launcherRelease is null) return;
            UpdateStatus = $"{T("UpdateAvailable")} · {launcherRelease.Version}";
            Notify(nameof(UpdateStatus));
            CommandManager.InvalidateRequerySuggested();
            toasts.Show(UpdateStatus);
        }
        catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or
                                   OperationCanceledException or System.Text.Json.JsonException)
        {
            WriteLog("[WARN] " + e.Message);
        }
    }

    internal void RunCardAction(InstanceCard card, string action)
    {
        if (Busy) return;
        if (Instances.FirstOrDefault(instance => instance.Id == card.Instance.Id) is not { } current) return;
        SelectedInstance = current;
        var command = action switch
        {
            "Play" => Launch,
            "Settings" => OpenInstanceSettings,
            "Inspect" => OpenInstance,
            "Duplicate" => DuplicateInstance,
            "Delete" => Delete,
            _ => null
        };
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    private async Task PlayAsync(CancellationToken token)
    {
        var target = RequireInstance();
        var profile = Account ?? throw new InvalidOperationException(T("NoAccount"));
        var preferences = instancesService.Preferences(target.Id);
        DateTimeOffset? started = null;
        Stopwatch? playTime = null;
        await using var discord = new DiscordRpcService();
        Task? presence = null;
        using var discordToken = new CancellationTokenSource();
        try
        {
            try { await skinDeployment.SyncIfEnabledAsync(target, profile, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Append($"[WARN] CustomSkinLoader: {error.Message}");
            }
            var exit = await game.LaunchAsync(target, data.Game(target.Id), profile, Progress(), GameLog, token, () =>
            {
                started = DateTimeOffset.UtcNow;
                try { instancesService.SavePreferences(target.Id, preferences with { LastPlayed = started }); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { WriteLog(error.Message); }
                var recentIndex = Cards.ToList().FindIndex(card => card.Instance.Id == target.Id);
                if (recentIndex >= 0)
                    Cards[recentIndex] = Cards[recentIndex] with { Preferences = preferences with { LastPlayed = started } };
                Notify(nameof(RecentInstances));
                Notify(nameof(HasRecentInstances));
                Notify(nameof(SelectedCard));
                playTime = Stopwatch.StartNew();
                canCancel = false;
                CommandManager.InvalidateRequerySuggested();
                SetStatus(T("GameStarted"), 100);
                toasts.Show(T("GameStarted"));
                if (settingsService.Current.DiscordEnabled)
                    presence = PublishPresenceAsync(discord, target, discordToken.Token);
            }, preferences.Java, playServer, playPort, profile.SkinPath is { } skin
                ? slim => ReadImage(skin, true) is { } image ? AppearanceImages.GameSkin(image, slim) : null
                : null);
            Append($"Minecraft · exit {exit}");
            if (exit != 0 && started is { } time)
            {
                var crash = await Task.Run(() => crashAnalyzer.AnalyzeAsync(data.Game(target.Id), time, logPath, CancellationToken.None));
                ShowCrash(crash);
                throw new InvalidOperationException($"{T("GameCrash")} ({exit})");
            }
        }
        finally
        {
            discordToken.Cancel();
            if (presence is not null) await presence;
            if (playTime is not null)
            {
                instancesService.SavePreferences(target.Id, preferences with
                {
                    PlaySeconds = preferences.PlaySeconds + (long)playTime.Elapsed.TotalSeconds,
                    LastPlayed = started
                });
                Notify(nameof(WorkspaceLastPlayed));
                Notify(nameof(WorkspacePlayTime));
                _ = RefreshCardsAsync();
                if (SelectedInstance?.Id == target.Id) _ = LoadScreenshotsAsync(target.Id);
                if (Section == 5 && SelectedInstance?.Id == target.Id) _ = RefreshWorkspaceAsync();
            }
        }
    }

    private async Task PublishPresenceAsync(DiscordRpcService discord, Instance instance, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { await discord.SetAsync(settingsService.Current.DiscordApplicationId, instance, timeout.Token); }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            WriteLog("[WARN] Discord: " + e.Message);
        }
    }

    public async Task DropAsync(string[] paths)
    {
        if (Busy || paths.Length == 0) return;
        await RunAsync(async token =>
        {
            var packsDropped = paths.Where(path => Path.GetExtension(path).Equals(".mrpack", StringComparison.OrdinalIgnoreCase)).ToArray();
            var jars = paths.Where(path => Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (packsDropped.Length + jars.Length == 0) throw new InvalidOperationException(T("InvalidDrop"));
            var target = jars.Length > 0 ? RequireInstance() : null;
            if (target is not null)
            {
                var notes = await Task.Run(() => ModInspection.Inspect(jars, data.Game(target.Id), target, token), token);
                var warning = notes.Length == 0 ? "" : "\n\n" + string.Join("\n", notes.Select(note => $"{Path.GetFileName(note.Path)} · {T(note.Key)}"));
                if (Confirm(string.Format(T("ConfirmJar"), jars.Length, target.Name) + warning))
                {
                    await ModInspection.ImportAsync(jars, data.Game(target.Id), data.Stage(), token);
                    toasts.Show("✓ " + T("Installed"));
                }
            }
            foreach (var pack in packsDropped) await InstallPack(pack, null, token);
            RefreshMods();
            _ = RefreshCardsAsync();
            if (Section == 5) await RefreshWorkspaceAsync();
        });
    }

    private void RefreshSelection()
    {
        packScan?.Cancel();
        availablePack = null;
        if (Section == 5) _ = RefreshWorkspaceAsync();
        NotifyPack();
        JavaInstallations.Clear();
        Screenshots.Clear();
        if (SelectedInstance is { } instance)
        {
            var values = instancesService.Preferences(instance.Id);
            JavaPath = values.Java;
            InstanceCover = values.Cover;
            InstanceRam = instance.Ram;
            _ = LoadScreenshotsAsync(instance.Id);
            if (packUpdates.Origin(instance.Id) is { } origin) _ = CheckActivePackAsync(instance.Id, origin);
        }
        else
        {
            JavaPath = null;
            InstanceCover = null;
        }
        foreach (var property in new[] { nameof(WindowTitle), nameof(ActiveMetadata), nameof(HasNoInstance), nameof(SelectedCard),
                     nameof(InstanceRam), nameof(JavaPath), nameof(InstanceCover), nameof(JavaHint), nameof(PackName) }) Notify(property);
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task CheckActivePackAsync(Guid id, PackOrigin origin)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        packScan = cancellation;
        try
        {
            var update = await packUpdates.CheckAsync(origin, cancellation.Token);
            if (SelectedInstance?.Id != id || cancellation.IsCancellationRequested) return;
            availablePack = update;
            NotifyPack();
            if (update is not null) toasts.Show(PackUpdateLabel);
        }
        catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or
                                   OperationCanceledException or System.Text.Json.JsonException)
        {
            if (!cancellation.IsCancellationRequested) WriteLog("[WARN] " + e.Message);
        }
        finally { if (packScan == cancellation) packScan = null; }
    }

    private async Task LoadScreenshotsAsync(Guid id)
    {
        try
        {
            var images = await Task.Run(() => instancesService.Screenshots(id).Select(path => new ScreenshotItem(path, LoadThumbnail(path))).ToArray());
            if (SelectedInstance?.Id != id) return;
            Screenshots.Clear();
            foreach (var image in images) Screenshots.Add(image);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { WriteLog(e.Message); }
    }

    private async Task RefreshCardsAsync()
    {
        cardsScan?.Cancel();
        using var cancellation = new CancellationTokenSource();
        cardsScan = cancellation;
        try
        {
            var cards = await instancesService.CardsAsync(Instances.ToArray(), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            Cards.Clear();
            foreach (var card in cards) Cards.Add(card);
            Notify(nameof(SelectedCard));
            Notify(nameof(TotalPlayTime));
            Notify(nameof(RecentInstances));
            Notify(nameof(HasRecentInstances));
            Notify(nameof(LibraryEmpty));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { WriteLog(e.Message); }
        finally { if (cardsScan == cancellation) cardsScan = null; }
    }

    private void LoadPreferences(UserSettings settings)
    {
        PendingTheme = settings.Theme;
        PendingAccent = settings.Accent;
        PendingLanguage = settings.Language;
        PendingDirectory = settings.DataDirectory ?? data.Root;
        PendingDiscord = settings.DiscordEnabled;
        PendingAvatars = settings.ExternalAvatars;
        PendingDiscordId = settings.DiscordApplicationId;
        PendingRetention = settings.LogRetentionDays.ToString();
        localization.Apply(settings.Language);
        LoadBackground(settings);
        theme.Apply(settings.Theme, settings.Accent, HasWindowBackground ? PendingInterfaceTransparency : 0);
        LocalizeChoices();
        if (!Busy) SetStatus(T("Ready"), 0);
        Notify(nameof(SkinState));
        Notify(nameof(AvatarUrl));
        Notify(nameof(SkinUrl));
        Notify(nameof(AccountLabel));
        Notify(nameof(PageTitle));
        Notify(nameof(JavaHint));
        Notify(nameof(Page));
        Notify(nameof(Breadcrumb));
        Notify(nameof(ContentKinds));
        Notify(nameof(LoaderVersionStatus));
        Notify(nameof(WorkspacePlayTime));
        Notify(nameof(WorkspaceLastPlayed));
    }

    private void NotifyPack()
    {
        Notify(nameof(PackUpdateAvailable));
        Notify(nameof(PackUpdateLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    private void LocalizeChoices()
    {
        Types = [new(T("AllTypes"), ""), new(T("Mods"), "mod"), new(T("Modpacks"), "modpack"),
            new(T("ResourcePacks"), "resourcepack"), new(T("Shaders"), "shader")];
        Filters = [new(T("AllLoaders"), ""), new("Fabric", "fabric"), new("Forge", "forge"), new("Quilt", "quilt"), new("NeoForge", "neoforge")];
        Sorts = [new(T("Relevance"), "relevance"), new(T("Downloads"), "downloads"), new(T("Follows"), "follows"),
            new(T("Updated"), "updated"), new(T("Newest"), "newest")];
        Notify(nameof(Types)); Notify(nameof(Filters)); Notify(nameof(Sorts));
    }

    private void ShowCrash(CrashReport report)
    {
        var decision = ShowTextDialog(T("GameCrash"), T(report.ReasonKey), report.Excerpt, T("OpenLog"), T("Copy"));
        if (decision == MessageBoxResult.Yes && File.Exists(report.Path)) OpenFile(report.Path);
        if (decision == MessageBoxResult.No && report.Excerpt.Length > 0) Clipboard.SetText(report.Excerpt);
    }

    private bool ShowChanges(PackChange[] changes, string version) =>
        ShowTextDialog(T("UpdatePack"), version, changes.Length == 0 ? T("NoFileChanges") :
            string.Join(Environment.NewLine, changes.Select(change => $"{change.Kind} {change.Path}")), T("UpdatePack"), null)
        == MessageBoxResult.Yes;

    private static MessageBoxResult ShowTextDialog(string title, string summary, string text, string primary, string? secondary)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, Height = 245,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        return ActionDialog.Ask(title, summary, primary, secondary, details: box);
    }

    private void CleanOldLogs()
    {
        var retention = settingsService.Current.LogRetentionDays;
        if (retention <= 0) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-retention);
            foreach (var instance in instancesService.List())
                foreach (var file in InstanceServiceFiles(Path.Combine(data.Game(instance.Id), "logs")))
                    if (file.Name != "latest.log" && file.LastWriteTimeUtc < cutoff) file.Delete();
            lock (logLock)
                if (File.Exists(logPath) && File.GetLastWriteTimeUtc(logPath) < cutoff) File.Delete(logPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { WriteLog(e.Message); }
    }

    private static IEnumerable<FileInfo> InstanceServiceFiles(string path) =>
        Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            ? new DirectoryInfo(path).EnumerateFiles("*.log*")
                .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint)) : [];

    private static BitmapImage? LoadThumbnail(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 240;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FormatException) { return null; }
    }
    private static string Severity(string line) =>
        line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ? "Error" :
        line.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? "Warn" : "Info";
    private static string? PickImage()
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private static System.Windows.Media.Color ParseAccent(string color) =>
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
    internal static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private string ServerFile => Path.Combine(data.Root, "servers.json");
    private void SaveServers() => LocalData.Write(ServerFile, Servers.ToArray());
}

public sealed record ScreenshotItem(string Path, BitmapImage? Thumbnail)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
public sealed record SavedServer(string Name, string Host, int Port)
{
    public string Label => $"{Name} · {Host}:{Port}";
}
