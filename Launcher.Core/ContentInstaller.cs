

namespace Launcher.Core;

public sealed record InstalledFile(string Path, string ProjectId, string VersionId, string Sha512, string[] Requires,
    string? Title = null, string? VersionName = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record InstanceContent(InstalledFile File, bool Enabled, bool IsDirectory = false, string? IconUrl = null)
{
    public Uri? Icon => Modrinth.CdnImage(IconUrl);
    public string ProjectId => File.ProjectId;
    public string Title => File.Title ?? System.IO.Path.GetFileNameWithoutExtension(File.Name);
    public string Name => File.Name;
    public string? Version => File.VersionName;
    public string Kind => File.Path.Split('/')[0];
    public string DiskPath => File.Path + (Enabled ? "" : ".disabled");
}

public sealed partial class ContentInstaller(Modrinth api, LocalData data)
{
    private string Registry(Guid id) => Path.Combine(data.Folder(id), "installed.json");

    public InstalledFile[] GetMods(Guid id)
    {
        var folders = data.Game(id);
        var known = Read(id).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var mods = LocalData.SafePath(folders, "mods");
        if (!Directory.Exists(mods)) return [];
        return Directory.EnumerateFiles(mods)
            .Where(file => file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .Select(file =>
            {
                var path = "mods/" + Path.GetFileName(file);
                return known.TryGetValue(path, out var installed) ? installed : new(path, "", "", "", []);
            }).OrderBy(file => file.Name).ToArray();
    }

    private InstalledFile[] Read(Guid id) => LocalData.Read(Registry(id), Array.Empty<InstalledFile>());

    public InstanceContent[] GetContent(Guid id)
    {
        var game = data.Game(id);
        var known = Read(id).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var result = new List<InstanceContent>();
        foreach (var folder in new[] { "mods", "resourcepacks", "shaderpacks" })
        {
            var root = LocalData.SafePath(game, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var enabled = !item.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var name = enabled ? item.Name : item.Name[..^9];
                var extension = Path.GetExtension(name);
                if (folder == "mods" ? item is DirectoryInfo || !extension.Equals(".jar", StringComparison.OrdinalIgnoreCase)
                    : item is not DirectoryInfo && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var path = folder + "/" + name;
                result.Add(new(known.GetValueOrDefault(path) ?? new(path, "", "", "", []), enabled, item is DirectoryInfo));
            }
        }
        return result.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public void Toggle(Guid id, InstanceContent item)
    {
        if (item.IsDirectory) throw new InvalidOperationException("A folder pack cannot be disabled by renaming it.");
        var root = data.Game(id);
        var source = LocalData.SafePath(root, item.DiskPath);
        var target = LocalData.SafePath(root, (item with { Enabled = !item.Enabled }).DiskPath);
        File.Move(source, target);
    }

    public void Remove(Guid id, InstanceContent item) => Remove(id, item.File, item.DiskPath);

    public string[] Dependents(Guid id, InstalledFile file) => file.ProjectId.Length == 0 ? [] :
        Read(id).Where(item => item.Requires.Contains(file.ProjectId) &&
            File.Exists(LocalData.SafePath(data.Game(id), item.Path))).Select(item => item.Name).ToArray();

    public void Remove(Guid id, InstalledFile installed) => Remove(id, installed, installed.Path);

    private void Remove(Guid id, InstalledFile installed, string diskPath)
    {
        var path = LocalData.SafePath(data.Game(id), diskPath);
        var backup = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var directory = Directory.Exists(path);
        void Move(string from, string to)
        {
            if (directory) Directory.Move(from, to);
            else File.Move(from, to);
        }
        Move(path, backup);
        try
        {
            var other = LocalData.SafePath(data.Game(id), diskPath.Equals(installed.Path, StringComparison.OrdinalIgnoreCase)
                ? installed.Path + ".disabled" : installed.Path);
            if (!File.Exists(other) && !Directory.Exists(other))
                LocalData.Write(Registry(id), Read(id).Where(file => !file.Path.Equals(installed.Path, StringComparison.OrdinalIgnoreCase)).ToArray());
        }
        catch
        {
            Move(backup, path);
            throw;
        }
        if (directory) LocalData.Cleanup(backup);
        else File.Delete(backup);
    }
}
