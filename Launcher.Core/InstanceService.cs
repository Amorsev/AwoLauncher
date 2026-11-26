using System.IO.Compression;

namespace Launcher.Core;

public sealed record InstancePreferences(string? Java = null, string? Cover = null, long PlaySeconds = 0,
    DateTimeOffset? LastPlayed = null);
public sealed record InstanceCard(Instance Instance, int Mods, long Bytes, InstancePreferences Preferences)
{
    public string Name => Instance.Name;
    public string Initials => Name.Length > 2 ? Name[..2].ToUpperInvariant() : Name.ToUpperInvariant();
    public string Version => $"{Instance.GameVersion} · {Instance.Loader}";
    public string Size => $"{Bytes / (1024d * 1024):0.0} MB";
    public string PlayTime => $"{TimeSpan.FromSeconds(Preferences.PlaySeconds).TotalHours:0.0} h";
    public string? Cover => Preferences.Cover;
}

public sealed class InstanceService(LocalData data)
{
    public Guid? Active
    {
        get => LocalData.Read<Guid?>(Path.Combine(data.Root, "active.json"), null);
        set => LocalData.Write(Path.Combine(data.Root, "active.json"), value);
    }

    public Instance[] List() => data.List();
    public void Rename(Instance instance, string name) => data.Rename(instance, name);
    public void Delete(Instance instance) => data.Delete(instance.Id);
    public InstancePreferences Preferences(Guid id) =>
        LocalData.Read(Path.Combine(data.Folder(id), "preferences.json"), new InstancePreferences());

    public void Save(Instance instance, InstancePreferences preferences)
    {
        var validated = LocalData.Create(instance.Name, instance.GameVersion, instance.Loader, instance.LoaderVersion, instance.Ram);
        LocalData.Write(Path.Combine(data.Folder(instance.Id), "instance.json"), validated with { Id = instance.Id });
        SavePreferences(instance.Id, preferences);
    }

    public void SavePreferences(Guid id, InstancePreferences preferences) =>
        LocalData.Write(Path.Combine(data.Folder(id), "preferences.json"), preferences);

    public Task<InstanceCard[]> CardsAsync(IEnumerable<Instance> instances, CancellationToken token) => Task.Run(() =>
        instances.Select(instance =>
        {
            var root = data.Game(instance.Id);
            long size = 0;
            int mods = 0;
            foreach (var file in Files(root))
            {
                token.ThrowIfCancellationRequested();
                if (file.DirectoryName?.Equals(Path.Combine(root, "mods"), StringComparison.OrdinalIgnoreCase) == true &&
                    file.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase)) mods++;
                size += file.Length;
            }
            return new InstanceCard(instance, mods, size, Preferences(instance.Id));
        }).ToArray(), token);

    internal static IEnumerable<FileInfo> Files(string path) => Directory.Exists(path)
        ? new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        }) : [];

    public async Task<Instance> DuplicateAsync(Instance source, CancellationToken token)
    {
        var duplicate = source with { Id = Guid.NewGuid(), Name = source.Name[..Math.Min(source.Name.Length, 90)] + " · copy" };
        var stage = data.Stage();
        try
        {
            await CopyAsync(data.Folder(source.Id), stage, token);
            LocalData.Write(Path.Combine(stage, "preferences.json"), Preferences(source.Id) with { PlaySeconds = 0, LastPlayed = null });
            token.ThrowIfCancellationRequested();
            data.Publish(stage, duplicate);
            return duplicate;
        }
        finally { LocalData.Cleanup(stage); }
    }

    internal static async Task CopyAsync(string from, string to, CancellationToken token)
    {
        var pending = new Stack<(string Source, string Target)>();
        pending.Push((from, to));
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            if (File.GetAttributes(folder.Source).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"A linked folder cannot be copied safely: {folder.Source}");
            Directory.CreateDirectory(folder.Target);
            foreach (var entry in new DirectoryInfo(folder.Source).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"A linked entry cannot be copied safely: {entry.FullName}");
                var destination = Path.Combine(folder.Target, entry.Name);
                if (entry is DirectoryInfo)
                {
                    pending.Push((entry.FullName, destination));
                    continue;
                }
                await using var input = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                await input.CopyToAsync(output, token);
            }
        }
    }

    public async Task ExportAsync(Instance instance, string destination, CancellationToken token)
    {
        var source = data.Folder(instance.Id);
        if (Path.GetFullPath(destination).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an export location outside the instance.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in Files(source))
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source, file.FullName).Replace('\\', '/');
                    if (relative == "loader-version.json" || relative.StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase) &&
                        relative.Split('/')[1] is "assets" or "libraries" or "runtime" or "versions" or "natives" or "logs")
                        continue;
                    var entry = archive.CreateEntry(relative);
                    await using var input = file.OpenRead();
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, token);
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(temporary); }
    }

    public string[] Screenshots(Guid id) => Files(Path.Combine(data.Game(id), "screenshots"))
        .Where(file => file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(file => file.LastWriteTimeUtc).Take(100).Select(file => file.FullName).ToArray();
}
