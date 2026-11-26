using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.UI;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly LocalData data;
    private readonly Modrinth api;
    private readonly GameLauncher game;
    private readonly PackInstaller packs;
    private readonly ContentInstaller content;
    private readonly string logPath;
    private readonly object logLock = new();
    private CancellationTokenSource? operation;
    private bool canCancel;
    private Instance? selectedInstance;
    private Profile? account;
    private bool refreshingProfiles;
    private int offset;
    private int total;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<Instance> Instances { get; } = [];
    public ObservableCollection<InstalledFile> Mods { get; } = [];
    public ObservableCollection<ProjectHit> Hits { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    public Loader[] Loaders { get; } = Enum.GetValues<Loader>();
    public sealed record Choice(string Label, string Value);
    public Choice[] Types { get; private set; } = [];
    public Choice[] Filters { get; private set; } = [];
    public Choice[] Sorts { get; private set; } = [];
    public string Nick { get; set; } = "";
    public string InstanceName { get; set; } = "";
    private string gameVersion = "1.21.1";
    private Loader newLoader;
    public string GameVersion
    {
        get => gameVersion;
        set { if (gameVersion != value) { gameVersion = value; Notify(); LoadCreationVersions(debounce: true); } }
    }
    public Loader NewLoader
    {
        get => newLoader;
        set { if (newLoader != value) { newLoader = value; Notify(); LoadCreationVersions(); } }
    }
    private string? loaderVersion;
    public string? LoaderVersion
    {
        get => loaderVersion;
        set
        {
            if (loaderVersion == value) return;
            loaderVersion = value;
            Notify();
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string Ram { get; set; }
    public string RenameTo { get; set; } = "";
    private string query = "", searchGame = "", typeFilter = "", loaderFilter = "", sort = "downloads";
    public string Query { get => query; set => SetCatalogFilter(ref query, value); }
    public string SearchGame { get => searchGame; set => SetCatalogFilter(ref searchGame, value); }
    public string TypeFilter { get => typeFilter; set { if (value is not null) SetCatalogFilter(ref typeFilter, value); } }
    public string LoaderFilter { get => loaderFilter; set { if (value is not null) SetCatalogFilter(ref loaderFilter, value); } }
    public string Sort { get => sort; set { if (value is not null) SetCatalogFilter(ref sort, value); } }
    public ProjectHit? Hit { get; set; }
    private ModVersion? selectedVersion;
    public ModVersion? Version
    {
        get => selectedVersion;
        set
        {
            if (selectedVersion == value) return;
            selectedVersion = value;
            Notify();
            Notify(nameof(Breadcrumb));
            Notify(nameof(ShowVersionMismatch));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public InstalledFile? Mod { get; set; }
    public Project? Details { get; private set; }
    public string[] LoaderVersions { get; private set; } = [];
    private CancellationTokenSource? loaderRequest;
    public bool LoaderVersionsBusy { get; private set; }
    public string? LoaderVersionsError { get; private set; }
    public bool CanRetryLoaders => LoaderVersionsError is not null;
    public string LoaderVersionStatus => LoaderVersionsError ?? (LoaderVersionsBusy ? T("LoadingLoaderVersions")
        : LoaderVersions.Length > 0 ? string.Format(T("ChooseLoaderVersion"), LoaderVersions.Length) : "");
    public ModVersion[] Versions { get; private set; } = [];
    public string Status { get; private set; } = "Готово";
    public double? Percent { get; private set; } = 0;
    public double ProgressValue => Percent ?? 0;
    public bool Indeterminate => Percent is null;
    public bool Busy => operation is not null;
    public string Page => total == 0 ? T("NoResults") : string.Format(T("PageFormat"), offset + 1, Math.Min(total, offset + Hits.Count), total);

    public Profile? Account
    {
        get => account;
        set
        {
            if (account == value) return;
            account = value;
            Notify();
            Notify(nameof(AccountLabel)); Notify(nameof(AccountInitial)); Notify(nameof(AvatarUrl)); Notify(nameof(SkinUrl));
            RefreshAccountAppearance();
            if (!refreshingProfiles && value is not null)
                TryLocal(() => data.SaveProfiles(new(Profiles.ToArray(), value.Nick)));
        }
    }

    public Instance? SelectedInstance
    {
        get => selectedInstance;
        set
        {
            if (selectedInstance == value) return;
            selectedInstance = value;
            Notify(nameof(ShowVersionMismatch));
            Notify(nameof(HasAppliedSkin));
            RenameTo = value?.Name ?? "";
            Notify();
            Notify(nameof(RenameTo));
            TryLocal(() =>
            {
                if (value is not null) instancesService.Active = value.Id;
                RefreshMods();
                RefreshSelection();
            });
        }
    }

    public ICommand AddProfile { get; }
    public ICommand CreateInstance { get; }
    public ICommand FindLoaders { get; }
    public ICommand Rename { get; }
    public ICommand Delete { get; }
    public ICommand Launch { get; }
    public ICommand RemoveMod { get; }
    public ICommand RetryCatalog { get; }
    public ICommand CloseProject { get; }
    public ICommand OpenProjectSite { get; }
    public ICommand OpenProjectImage { get; }
    public ICommand ViewProject { get; }
    public ICommand InstallFromCatalog { get; }
    public ICommand Previous { get; }
    public ICommand Next { get; }
    public ICommand Install { get; }
    public ICommand ImportPack { get; }
    public ICommand Cancel { get; }

    public MainViewModel(Settings settings, SettingsService? preferences = null)
    {
        preferences ??= new SettingsService(settings);
        settings = settings with { DataDirectory = preferences.Current.DataDirectory ?? settings.DataDirectory };
        data = new(settings);
        logPath = Path.GetFullPath(Path.Combine(data.Root, Environment.ExpandEnvironmentVariables(settings.LogFile)));
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);
        api = new(http);
        game = new(http, data.Runtime);
        packs = new(api, data, game, settings);
        content = new(api, data);
        InitializeProduct(settings, preferences, http);
        Ram = settings.DefaultRamMb.ToString(System.Globalization.CultureInfo.InvariantCulture);

        AddProfile = Command(_ =>
        {
            RefreshAccounts(data.AddProfile(Nick));
            Nick = "";
            Notify(nameof(Nick));
            return Task.CompletedTask;
        });
        CreateInstance = Command(_ =>
        {
            if (!int.TryParse(Ram, out var ram))
                throw new InvalidOperationException(T("RamInvalid"));
            var instance = LocalData.Create(InstanceName, GameVersion, NewLoader, LoaderVersion ?? "", ram);
            var stage = data.Stage();
            try { data.Publish(stage, instance); }
            finally { LocalData.Cleanup(stage); }
            RefreshInstances(instance.Id);
            ShowCreation = false;
            return Task.CompletedTask;
        }, () => NewLoader == Loader.Vanilla || !LoaderVersionsBusy && LoaderVersions.Contains(LoaderVersion));
        FindLoaders = new ActionCommand(() => LoadCreationVersions(), () => CanRetryLoaders && !LoaderVersionsBusy);
        Rename = Command(_ =>
        {
            var instance = RequireInstance();
            instancesService.Rename(instance, RenameTo);
            RefreshInstances(instance.Id);
            return Task.CompletedTask;
        }, () => SelectedInstance is not null);
        Delete = Command(_ =>
        {
            var instance = RequireInstance();
            if (Confirm(string.Format(T("DeleteInstanceConfirm"), instance.Name), T("DeleteInstance"), T("Remove"), true))
            {
                instancesService.Delete(instance);
                RefreshInstances(null);
            }
            return Task.CompletedTask;
        }, () => SelectedInstance is not null);
        RemoveMod = Command(token =>
        {
            var instance = RequireInstance();
            if (Mod is not { } mod) return Task.CompletedTask;
            var dependents = content.Dependents(instance.Id, mod);
            var warning = dependents.Length == 0 ? "" : "\n" + string.Format(T("Dependents"), string.Join(", ", dependents));
            if (ConfirmContentRemoval(mod.Name, warning))
            {
                content.Remove(instance.Id, mod);
                RefreshMods();
                _ = RefreshCardsAsync();
            }
            return Task.CompletedTask;
        }, () => SelectedInstance is not null);
        Launch = Command(PlayAsync, () => SelectedInstance is not null);
        RetryCatalog = new ActionCommand(() => QueueCatalog(), () => !CatalogBusy);
        Previous = new ActionCommand(() => QueueCatalog(offset - Modrinth.PageSize), () => !CatalogBusy && CatalogError is null && offset > 0);
        Next = new ActionCommand(() => QueueCatalog(offset + Modrinth.PageSize), () => !CatalogBusy && CatalogError is null && offset + Modrinth.PageSize < total);
        CloseProject = new ActionCommand(() =>
        {
            ClearDetails();
            if (!catalogLoaded && !CatalogBusy) QueueCatalog();
        }, () => true);
        OpenProjectImage = new ParameterCommand<ProjectImage>(image =>
        {
            if (Details is not { Gallery: { } gallery } project) return;
            var index = Array.IndexOf(gallery, image);
            if (index < 0) return;
            new ProjectGalleryWindow(project.Title, gallery, index)
            {
                Owner = Application.Current.MainWindow
            }.ShowDialog();
        }, image => image.OriginalImage is not null);
        OpenProjectSite = new ActionCommand(() =>
        {
            if (Details is { Type: "mod" or "modpack" or "resourcepack" or "shader" } project)
                TryLocal(() => OpenFile($"https://modrinth.com/{project.Type}/{Uri.EscapeDataString(project.Id)}"));
        }, () => Details is { Type: "mod" or "modpack" or "resourcepack" or "shader" });
        ViewProject = new ParameterCommand<ProjectHit>(hit =>
        {
            Hit = hit;
            _ = OpenProjectPageAsync(hit.Id, hit.Author);
        }, _ => !CatalogBusy && !ProjectLoading);
        InstallFromCatalog = new ParameterCommand<ProjectHit>(hit => _ = RunAsync(async token =>
        {
            var target = SelectedInstance;
            var project = await api.ProjectAsync(hit.Id, token);
            var choices = await api.VersionsAsync(hit.Id, token);
            var version = PreferredVersion(choices, project, target)
                ?? throw new InvalidOperationException(T("NoCompatibleProjectVersion"));
            await InstallProjectAsync(project, version, target, token);
        }), hit => !Busy && !CatalogBusy && (hit.Type == "modpack" || SelectedInstance is not null));
        Install = Command(async token =>
        {
            var project = Details ?? throw new InvalidOperationException(T("ChooseProject"));
            var version = Version ?? throw new InvalidOperationException(T("ChooseVersion"));
            await InstallProjectAsync(project, version, SelectedInstance, token);
        }, () => Details is not null && Version is not null && !ShowVersionMismatch &&
                 (Details.Type == "modpack" || SelectedInstance is not null));
        ImportPack = Command(async token =>
        {
            var dialog = new OpenFileDialog { Filter = "Модпаки Modrinth (*.mrpack)|*.mrpack", CheckFileExists = true };
            if (dialog.ShowDialog(Application.Current.MainWindow) == true) await InstallPack(dialog.FileName, null, token);
        });
        Cancel = new ActionCommand(() => operation?.Cancel(), () => canCancel);
        TryLocal(() =>
        {
            RefreshAccounts(data.Profiles);
            RefreshInstances(instancesService.Active);
        });
    }

    private CancellationTokenSource? catalogRequest;
    private int catalogRevision;
    private bool catalogLoaded;
    public string? CatalogError { get; private set; }
    public bool HasCatalogError => CatalogError is not null;
    public bool CatalogEmpty => catalogLoaded && !CatalogBusy && !HasCatalogError && Hits.Count == 0;
    public bool HasDetails => Details is not null;
    public bool ShowVersionMismatch => Details is { Type: not "modpack" } project &&
        Version is { } version && SelectedInstance is { } instance && !Modrinth.Compatible(version, instance, project.Type);
    private CancellationTokenSource? projectRequest;
    public bool ProjectLoading => projectRequest is not null;
    public string? ProjectAuthor { get; private set; }

    private async Task OpenProjectPageAsync(string id, string? author)
    {
        ClearDetails();
        using var request = new CancellationTokenSource();
        projectRequest = request;
        Notify(nameof(ProjectLoading));
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var project = api.ProjectAsync(id, request.Token);
            var versions = api.VersionsAsync(id, request.Token);
            await Task.WhenAll(project, versions);
            if (projectRequest != request) return;
            Details = await project;
            ProjectAuthor = author;
            Versions = (await versions).OrderByDescending(item => item.Published).ToArray();
            Notify(nameof(Details));
            Notify(nameof(ProjectAuthor));
            Notify(nameof(Versions));
            Version = RankVersions(Versions).FirstOrDefault();
            Notify(nameof(Breadcrumb));
            Notify(nameof(HasDetails));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (projectRequest == request)
            {
                if (Busy)
                {
                    Append("[ERROR] Modrinth · " + error.Message);
                    toasts.Show(T("CatalogUnavailable"));
                }
                else Error(error);
            }
        }
        finally
        {
            if (projectRequest == request)
            {
                projectRequest = null;
                Notify(nameof(ProjectLoading));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private static ModVersion? PreferredVersion(IEnumerable<ModVersion> versions, Project project, Instance? target)
    {
        var ranked = RankVersions(versions);
        var matching = target is null ? null : ranked.FirstOrDefault(item => Modrinth.Compatible(item, target, project.Type) &&
            (project.Type != "modpack" || item.Loaders.Contains(target.Loader.ToString(), StringComparer.OrdinalIgnoreCase)));
        return matching ?? (project.Type == "modpack" ? ranked.FirstOrDefault() : null);
    }

    private static IOrderedEnumerable<ModVersion> RankVersions(IEnumerable<ModVersion> versions) =>
        versions.OrderBy(item => item.Type == "release" ? 0 : 1).ThenByDescending(item => item.Published);

    private async Task InstallProjectAsync(Project project, ModVersion version, Instance? target, CancellationToken token)
    {
        if (project.Type == "modpack")
        {
            await InstallPack(null, version, token);
            return;
        }
        if (target is null) throw new InvalidOperationException(T("ChooseInstance"));
        await content.InstallAsync(target, project, version, Progress(), token);
        _ = RefreshCardsAsync();
        if (SelectedInstance?.Id == target.Id)
        {
            RefreshMods();
            if (Section == 5) await RefreshWorkspaceAsync();
        }
        toasts.Show("✓ " + T("Installed"));
    }

    private void SetCatalogFilter(ref string field, string? value, [CallerMemberName] string property = "")
    {
        value ??= "";
        if (field == value) return;
        field = value;
        Notify(property);
        if (Section == 1) QueueCatalog(debounce: true);
        else catalogLoaded = false;
    }

    private async void QueueCatalog(int requestedOffset = 0, bool debounce = false)
    {
        catalogRequest?.Cancel();
        using var request = new CancellationTokenSource();
        catalogRequest = request;
        var revision = ++catalogRevision;
        var token = request.Token;
        CatalogError = null;
        CatalogBusy = true;
        NotifyCatalog();
        try
        {
            if (debounce) await Task.Delay(350, token);
            var page = await api.SearchAsync(Query.Trim(), TypeFilter, SearchGame.Trim(), LoaderFilter, Sort, requestedOffset, token);
            token.ThrowIfCancellationRequested();
            if (revision != catalogRevision) return;
            offset = requestedOffset;
            total = page.Total;
            Hits.Clear();
            foreach (var hit in page.Hits) Hits.Add(hit);
            ClearDetails();
            catalogLoaded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (revision != catalogRevision) return;
            WriteLog("[ERROR] " + exception.Message);
            CatalogError = exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
                ? T("RateLimit") : T("CatalogUnavailable");
        }
        finally
        {
            if (revision == catalogRevision)
            {
                catalogRequest = null;
                CatalogBusy = false;
                NotifyCatalog();
            }
        }
    }

    private void NotifyCatalog()
    {
        Notify(nameof(Page));
        Notify(nameof(CatalogError));
        Notify(nameof(HasCatalogError));
        Notify(nameof(CatalogEmpty));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task InstallPack(string? path, ModVersion? version, CancellationToken token)
    {
        Instance? target = null;
        if (SelectedInstance is { } selected)
        {
            if (data.IsEmpty(selected.Id))
            {
                var choice = ActionDialog.Ask(T("Install"), string.Format(T("PackInPlace"), selected.Name),
                    T("UseSelectedInstance"), T("NewInstance"));
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes) target = selected;
            }
            else if (!Confirm(T("PackInNew"))) return;
        }
        var instance = await packs.InstallAsync(path, version, target,
            files => Task.FromResult(ChooseFiles(files)), Progress(), token);
        RefreshInstances(instance.Id);
        toasts.Show("✓ " + T("Installed"));
    }

    private async void LoadCreationVersions(bool debounce = false)
    {
        loaderRequest?.Cancel();
        loaderRequest = null;
        var loader = NewLoader;
        var version = GameVersion.Trim();
        LoaderVersions = [];
        LoaderVersion = null;
        LoaderVersionsError = null;
        LoaderVersionsBusy = loader != Loader.Vanilla && version.Length > 0;
        NotifyCreationVersions();
        if (!LoaderVersionsBusy) return;

        using var request = new CancellationTokenSource();
        loaderRequest = request;
        try
        {
            if (debounce) await Task.Delay(350, request.Token);
            var versions = await game.LoadersAsync(loader, version, request.Token);
            if (loaderRequest != request) return;
            LoaderVersions = versions.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().ToArray();
            if (LoaderVersions.Length == 0) LoaderVersionsError = T("NoLoaders");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            if (loaderRequest == request) LoaderVersionsError = T("NoLoaders");
        }
        catch (Exception error)
        {
            if (loaderRequest == request)
            {
                WriteLog("[ERROR] " + error.Message);
                LoaderVersionsError = T("LoaderFetchError");
            }
        }
        finally
        {
            if (loaderRequest == request)
            {
                loaderRequest = null;
                LoaderVersionsBusy = false;
                NotifyCreationVersions();
            }
        }
    }

    private void NotifyCreationVersions()
    {
        Notify(nameof(LoaderVersions));
        Notify(nameof(LoaderVersion));
        Notify(nameof(LoaderVersionsBusy));
        Notify(nameof(LoaderVersionsError));
        Notify(nameof(CanRetryLoaders));
        Notify(nameof(LoaderVersionStatus));
        CommandManager.InvalidateRequerySuggested();
    }

    private HashSet<string>? ChooseFiles(PackFile[] files)
    {
        var list = new System.Windows.Controls.ListBox
        {
            SelectionMode = System.Windows.Controls.SelectionMode.Multiple, ItemsSource = files.Select(file => file.Path),
            MaxHeight = 260
        };
        return ActionDialog.Ask(T("ChooseOptional"), T("SelectOptionalFiles"), T("Continue"), details: list) == MessageBoxResult.Yes
            ? list.SelectedItems.Cast<string>().ToHashSet() : null;
    }

    private void RefreshAccounts(ProfileState state)
    {
        refreshingProfiles = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in state.Profiles) Profiles.Add(profile);
            Account = Profiles.FirstOrDefault(p => p.Nick == state.Selected) ?? Profiles.FirstOrDefault();
        }
        finally { refreshingProfiles = false; }
    }

    private void RefreshInstances(Guid? id)
    {
        Instances.Clear();
        foreach (var instance in instancesService.List()) Instances.Add(instance);
        SelectedInstance = Instances.FirstOrDefault(instance => instance.Id == id) ?? Instances.FirstOrDefault();
        _ = RefreshCardsAsync();
    }

    private void RefreshMods()
    {
        Mods.Clear();
        if (SelectedInstance is not { } instance) return;
        foreach (var mod in content.GetMods(instance.Id)) Mods.Add(mod);
    }

    private void ClearDetails()
    {
        projectRequest?.Cancel();
        projectRequest = null;
        Notify(nameof(ProjectLoading));
        Details = null;
        Versions = [];
        Version = null;
        Notify(nameof(Version));
        Notify(nameof(HasDetails));
        Notify(nameof(Details)); Notify(nameof(Breadcrumb));
        Notify(nameof(Versions));
    }

    private Instance RequireInstance() => SelectedInstance ?? throw new InvalidOperationException(T("ChooseInstance"));
    private bool Confirm(string text, string? title = null, string? primary = null, bool destructive = false, string? warning = null) =>
        ActionDialog.Ask(title ?? T("ConfirmAction"), text, primary ?? T("Continue"), destructive: destructive, warning: warning)
        == MessageBoxResult.Yes;

    private ICommand Command(Func<CancellationToken, Task> work, Func<bool>? enabled = null) =>
        new AsyncCommand(() => RunAsync(work), () => !Busy && (enabled?.Invoke() ?? true));

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (Busy) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        canCancel = true;
        Notify(nameof(Busy));
        CommandManager.InvalidateRequerySuggested();
        SetStatus(T("Working"), null);
        try
        {
            await work(cancellation.Token);
            SetStatus(T("Ready"), 100);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetStatus(T("Cancelled"), 0);
        }
        catch (TaskCanceledException exception) { Error(exception, T("Timeout")); }
        catch (OperationCanceledException) { SetStatus(T("Cancelled"), 0); }
        catch (HttpRequestException exception)
        {
            Error(exception, exception.StatusCode == HttpStatusCode.TooManyRequests
                ? T("RateLimit")
                : T("Network") + " " + exception.Message);
        }
        catch (Exception exception) { Error(exception); }
        finally
        {
            canCancel = false;
            operation = null;
            Notify(nameof(Busy));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private IProgress<Activity> Progress()
    {
        var current = operation;
        return new Progress<Activity>(activity =>
        {
            if (operation == current && canCancel) SetStatus(activity.Text, activity.Percent);
        });
    }

    private void SetStatus(string text, double? percent)
    {
        Status = text;
        Percent = percent;
        Notify(nameof(Status));
        Notify(nameof(ProgressValue));
        Notify(nameof(Indeterminate));
    }

    private void TryLocal(Action action)
    {
        try { action(); }
        catch (Exception exception) { Error(exception); }
    }

    private void Error(Exception exception, string? text = null)
    {
        text ??= exception is JsonException ? T("JsonError") : exception.Message;
        SetStatus(text, 0);
        WriteLog(exception.ToString());
        Append("[ERROR] " + text);
        toasts.Show(text);
    }

    private void GameLog(string line)
    {
        WriteLog(line);
        _ = Application.Current.Dispatcher.InvokeAsync(() => AddLog(line));
    }

    private void Append(string line)
    {
        WriteLog(line);
        AddLog(line);
    }

    private void AddLog(string line)
    {
        Log.Add(line);
        if (Log.Count > 500) Log.RemoveAt(0);
    }

    private void WriteLog(string line)
    {
        try
        {
            lock (logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
            }
        }
        catch (IOException exception) { System.Diagnostics.Trace.TraceError(exception.ToString()); }
        catch (UnauthorizedAccessException exception) { System.Diagnostics.Trace.TraceError(exception.ToString()); }
    }

    private void Notify([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
}

internal sealed class AsyncCommand(Func<Task> run, Func<bool> enabled) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => enabled();
    public async void Execute(object? parameter)
    {
        if (enabled()) await run();
    }
}

internal sealed class ActionCommand(Action run, Func<bool> enabled) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => enabled();
    public void Execute(object? parameter)
    {
        if (enabled()) run();
    }
}
