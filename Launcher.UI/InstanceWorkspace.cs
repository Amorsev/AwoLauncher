using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.UI;

public sealed partial class MainViewModel
{
    private readonly ObservableCollection<InstanceContent> workspaceContent = [];
    private int workspaceRevision;
    private Guid? workspaceInstanceId;
    private CancellationTokenSource? workspaceMetadataRequest;
    private readonly Dictionary<string, Project> workspaceProjects = new(StringComparer.Ordinal);
    private int workspacePage;
    private string contentQuery = "";
    private string contentKind = "";

    public ICollectionView WorkspaceContent { get; private set; } = null!;
    public ObservableCollection<WorkspaceFolder> WorkspaceWorlds { get; } = [];
    public ObservableCollection<WorkspaceFolder> WorkspaceFolders { get; } = [];
    public Choice[] ContentKinds => [new(T("AllTypes"), ""), new(T("Mods"), "mods"),
        new(T("ResourcePacks"), "resourcepacks"), new(T("Shaders"), "shaderpacks")];
    public string WorkspaceLog { get; private set; } = "";
    public int WorkspacePage
    {
        get => workspacePage;
        set { if (workspacePage != value) { workspacePage = value; Notify(); } }
    }
    public string ContentQuery
    {
        get => contentQuery;
        set { contentQuery = value ?? ""; WorkspaceContent.Refresh(); }
    }
    public string ContentKind
    {
        get => contentKind;
        set { if (value is not null) { contentKind = value; WorkspaceContent.Refresh(); } }
    }
    public string WorkspacePlayTime => SelectedInstance is { } instance
        ? string.Format(T("WorkspaceHours"), instancesService.Preferences(instance.Id).PlaySeconds / 3600d) : "";
    public string WorkspaceLastPlayed => SelectedInstance is { } instance &&
        instancesService.Preferences(instance.Id).LastPlayed is { } last
        ? string.Format(T("LastPlayed"), last.ToLocalTime().ToString("g")) : T("NeverPlayed");
    public string? WorkspaceCover => SelectedInstance is { } instance ? instancesService.Preferences(instance.Id).Cover : null;

    public ICommand OpenInstance { get; private set; } = null!;
    public ICommand ReloadWorkspace { get; private set; } = null!;
    public ICommand AddWorkspaceFiles { get; private set; } = null!;
    public ICommand FindWorkspaceContent { get; private set; } = null!;
    public ICommand ToggleContent { get; private set; } = null!;
    public ICommand DeleteContent { get; private set; } = null!;
    public ICommand OpenWorkspaceFolder { get; private set; } = null!;
    public ICommand BackToInstances { get; private set; } = null!;
    public ICommand ViewWorkspaceProject { get; private set; } = null!;

    private void InitializeWorkspace()
    {
        WorkspaceContent = CollectionViewSource.GetDefaultView(workspaceContent);
        WorkspaceContent.Filter = value => value is InstanceContent item && (ContentKind.Length == 0 || item.Kind == ContentKind) &&
            (item.Title.Contains(ContentQuery.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
             item.Name.Contains(ContentQuery.Trim(), StringComparison.CurrentCultureIgnoreCase));
        OpenInstance = new ActionCommand(() => OpenInstanceWorkspace(0), () => SelectedInstance is not null);
        BackToInstances = new ActionCommand(() => Section = 2, () => true);
        ViewWorkspaceProject = new ParameterCommand<InstanceContent>(item =>
        {
            Section = 1;
            if (catalogRequest is not null)
            {
                catalogRequest.Cancel();
                catalogRequest = null;
                ++catalogRevision;
                CatalogBusy = false;
                NotifyCatalog();
            }
            _ = OpenProjectPageAsync(item.ProjectId, null);
        }, item => item.ProjectId.Length > 0);
        FindWorkspaceContent = new ActionCommand(() =>
        {
            Query = "";
            SearchGame = SelectedInstance?.GameVersion ?? "";
            LoaderFilter = SelectedInstance is { Loader: not Loader.Vanilla } instance
                ? instance.Loader.ToString().ToLowerInvariant() : "";
            TypeFilter = contentKind switch { "mods" => "mod", "resourcepacks" => "resourcepack", "shaderpacks" => "shader", _ => "" };
            if (TypeFilter is "resourcepack" or "shader") LoaderFilter = "";
            Section = 1;
        }, () => SelectedInstance is not null);
        ReloadWorkspace = new ActionCommand(() => _ = RefreshWorkspaceAsync(), () => SelectedInstance is not null);
        AddWorkspaceFiles = new ActionCommand(() => _ = PickWorkspaceFilesAsync(), () => SelectedInstance is not null && !Busy);
        ToggleContent = new ParameterCommand<InstanceContent>(item => _ = ChangeContentAsync(item, false),
            item => !Busy && SelectedInstance is not null && !item.IsDirectory);
        DeleteContent = new ParameterCommand<InstanceContent>(item => _ = ChangeContentAsync(item, true),
            _ => !Busy && SelectedInstance is not null);
        OpenWorkspaceFolder = new ParameterCommand<WorkspaceFolder>(folder =>
        {
            if (SelectedInstance?.Id == folder.InstanceId)
                TryLocal(() => { Directory.CreateDirectory(folder.Path); OpenFile(folder.Path); });
        }, folder => SelectedInstance?.Id == folder.InstanceId);
    }

    private void OpenInstanceWorkspace(int page)
    {
        WorkspacePage = page;
        Section = 5;
        _ = RefreshWorkspaceAsync();
    }

    private async Task PickWorkspaceFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true, CheckFileExists = true,
            Filter = "Minecraft (*.jar;*.mrpack)|*.jar;*.mrpack"
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        await DropAsync(dialog.FileNames);
        await RefreshWorkspaceAsync();
    }

    private Task ChangeContentAsync(InstanceContent item, bool remove) => RunAsync(async token =>
    {
        var instance = RequireInstance();
        if (instance.Id != workspaceInstanceId || !workspaceContent.Contains(item)) return;
        if (!item.IsDirectory || remove)
        {
            var requiredBy = (item.Enabled && item.Kind == "mods" || remove) ? content.Dependents(instance.Id, item.File) : [];
            var warning = requiredBy.Length == 0 ? "" : "\n" + string.Format(T("Dependents"), string.Join(", ", requiredBy));
            if (remove && !ConfirmContentRemoval(item.Name, warning) ||
                !remove && warning.Length > 0 && !Confirm(T("DisableContent") + "?" + warning)) return;
        }
        await Task.Run(() =>
        {
            if (remove) content.Remove(instance.Id, item);
            else content.Toggle(instance.Id, item);
        }, token);
        RefreshMods();
        _ = RefreshCardsAsync();
        await RefreshWorkspaceAsync();
    });

    private async Task RefreshWorkspaceAsync()
    {
        var target = SelectedInstance;
        workspaceMetadataRequest?.Cancel();
        var revision = ++workspaceRevision;
        workspaceInstanceId = null;
        Notify(nameof(WorkspaceCover));
        Notify(nameof(WorkspacePlayTime));
        Notify(nameof(WorkspaceLastPlayed));
        workspaceContent.Clear();
        WorkspaceWorlds.Clear();
        WorkspaceFolders.Clear();
        WorkspaceLog = "";
        Notify(nameof(WorkspaceLog));
        if (target is null) return;
        try
        {
            var snapshot = await Task.Run(async () =>
            {
                var gameRoot = data.Game(target.Id);
                var allContent = content.GetContent(target.Id);
                var folders = new[] { new WorkspaceFolder(target.Id, ".minecraft", gameRoot) }.Concat(
                    new[] { "mods", "resourcepacks", "shaderpacks", "config", "saves", "screenshots", "logs" }
                        .Select(folder => new WorkspaceFolder(target.Id, folder, LocalData.SafePath(gameRoot, folder)))).ToArray();
                var saves = LocalData.SafePath(gameRoot, "saves");
                var worlds = Directory.Exists(saves)
                    ? new DirectoryInfo(saves).EnumerateDirectories()
                        .Where(folder => !folder.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                                         (File.Exists(Path.Combine(folder.FullName, "level.dat")) || File.Exists(Path.Combine(folder.FullName, "level.dat_old"))))
                        .Select(folder => new WorkspaceFolder(target.Id, folder.Name, folder.FullName)).OrderBy(folder => folder.Name).ToArray()
                    : [];
                var log = LocalData.SafePath(gameRoot, "logs/latest.log");
                return (allContent, folders, worlds, await ReadWorkspaceLogAsync(log));
            });
            if (revision != workspaceRevision || SelectedInstance?.Id != target.Id) return;
            workspaceInstanceId = target.Id;
            foreach (var item in snapshot.allContent) workspaceContent.Add(item);
            foreach (var folder in snapshot.folders) WorkspaceFolders.Add(folder);
            foreach (var world in snapshot.worlds) WorkspaceWorlds.Add(world);
            WorkspaceLog = snapshot.Item4;
            Notify(nameof(WorkspaceLog));
            _ = EnrichWorkspaceAsync(target.Id, snapshot.allContent, revision);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (revision == workspaceRevision) Error(e);
        }
    }

    private async Task EnrichWorkspaceAsync(Guid instance, InstanceContent[] items, int revision)
    {
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        workspaceMetadataRequest = request;
        var token = request.Token;
        try
        {
            var gamePath = data.Game(instance);
            var unknown = await Task.Run(async () =>
            {
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items.Where(item => item.ProjectId.Length == 0 && !item.IsDirectory))
                {
                    token.ThrowIfCancellationRequested();
                    var path = LocalData.SafePath(gamePath, item.DiskPath);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 65536, true);
                    hashes[item.DiskPath] = Convert.ToHexString(await SHA512.HashDataAsync(input, token)).ToLowerInvariant();
                }
                return hashes;
            }, token);
            var recognized = new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in unknown.Values.Distinct().Chunk(100))
                foreach (var (hash, version) in await api.VersionsFromHashesAsync(batch, token))
                    recognized[hash] = version;
            var rows = items.Select(item =>
            {
                if (!unknown.TryGetValue(item.DiskPath, out var hash) || !recognized.TryGetValue(hash, out var version)) return item;
                return item with { File = item.File with { ProjectId = version.ProjectId, VersionId = version.Id, VersionName = version.Number } };
            }).ToArray();
            foreach (var batch in rows.Select(item => item.ProjectId).Where(id => id.Length > 0 && !workspaceProjects.ContainsKey(id)).Distinct().Chunk(50))
                foreach (var project in await api.ProjectsAsync(batch, token)) workspaceProjects[project.Id] = project;
            if (token.IsCancellationRequested || revision != workspaceRevision || SelectedInstance?.Id != instance) return;
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                if (workspaceProjects.TryGetValue(row.ProjectId, out var project))
                    row = row with { IconUrl = project.IconUrl, File = row.File with { Title = project.Title } };
                if (row != items[index]) workspaceContent[index] = row;
            }
            CommandManager.InvalidateRequerySuggested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Net.Http.HttpRequestException or
                                      UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (revision == workspaceRevision) Append("[WARN] Modrinth · " + error.Message);
        }
        finally { if (workspaceMetadataRequest == request) workspaceMetadataRequest = null; }
    }

    private static async Task<string> ReadWorkspaceLogAsync(string path)
    {
        if (!File.Exists(path)) return "";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, true);
        stream.Seek(Math.Max(0, stream.Length - 64 * 1024), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var buffer = new char[64 * 1024];
        var size = await reader.ReadBlockAsync(buffer.AsMemory());
        return new string(buffer, 0, size);
    }

    private bool ConfirmContentRemoval(string name, string warning) =>
        new RemoveContentDialog(string.Format(T("RemoveModConfirm"), name), warning.Trim())
        {
            Owner = Application.Current.MainWindow
        }.ShowDialog() == true;
}

public sealed record WorkspaceFolder(Guid InstanceId, string Name, string Path);
