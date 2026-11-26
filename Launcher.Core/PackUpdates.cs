using System.Security.Cryptography;

namespace Launcher.Core;

public sealed record PackChange(string Path, string Kind);
public sealed class PackUpdates(LocalData data, Modrinth api, GameLauncher game, Settings defaults)
{
    public PackOrigin? Origin(Guid id) => LocalData.Read<PackOrigin?>(Path.Combine(data.Folder(id), "pack.json"), null);

    public async Task<ModVersion?> CheckAsync(PackOrigin origin, CancellationToken token)
    {
        var versions = await api.VersionsAsync(origin.ProjectId, token);
        var installed = versions.FirstOrDefault(version => version.Id == origin.VersionId);
        if (installed is null) return null;
        return versions.Where(version => version.Published > installed.Published &&
                (installed.Type != "release" || version.Type == "release"))
            .OrderByDescending(version => version.Published).FirstOrDefault();
    }

    public async Task<Instance?> ApplyAsync(Instance target, PackOrigin old, ModVersion version,
        Func<PackFile[], Task<HashSet<string>?>> chooseOptional, Func<PackChange[], Task<bool>> confirm,
        IProgress<Activity> progress, CancellationToken token)
    {
        var work = data.Stage();
        var scratch = new LocalData(defaults with { DataDirectory = Path.Combine(work, "candidate") });
        var stage = Path.Combine(work, "merge");
        var backup = Path.Combine(work, "backup");
        var preserveBackup = false;
        try
        {
            var prepared = await new PackInstaller(api, scratch, game, defaults)
                .InstallAsync(null, version, null, chooseOptional, progress, token, prepareRuntime: false);
            var updated = LocalData.Read<PackOrigin?>(Path.Combine(scratch.Folder(prepared.Id), "pack.json"), null)
                ?? throw new InvalidDataException("Missing pack manifest.");
            var before = new Dictionary<string, string>(old.Files, StringComparer.OrdinalIgnoreCase);
            var after = new Dictionary<string, string>(updated.Files, StringComparer.OrdinalIgnoreCase);
            var fileChanges = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
                .Select(path => new PackChange(path, !before.ContainsKey(path) ? "+" : !after.ContainsKey(path) ? "−" : "~"))
                .OrderBy(change => change.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(change => change.Path).ToArray();
            var runtimeChange = target.GameVersion != prepared.GameVersion || target.Loader != prepared.Loader ||
                                target.LoaderVersion != prepared.LoaderVersion;
            PackChange[] changes = runtimeChange
                ? [new($"{target.GameVersion} · {target.Loader} {target.LoaderVersion} → {prepared.GameVersion} · {prepared.Loader} {prepared.LoaderVersion}", "~"), .. fileChanges]
                : fileChanges;
            token.ThrowIfCancellationRequested();
            if (!await confirm(changes)) return null;

            await Task.Run(() => InstanceService.CopyAsync(data.Folder(target.Id), stage, token), token);
            var stageGame = Path.Combine(stage, ".minecraft");
            foreach (var change in fileChanges)
            {
                token.ThrowIfCancellationRequested();
                var path = LocalData.SafePath(stageGame, change.Path);
                var disabled = LocalData.SafePath(stageGame, change.Path + ".disabled");
                if (File.Exists(disabled) || Directory.Exists(disabled))
                    throw new InvalidOperationException($"Disabled file conflicts with the update: {change.Path}");
                if (File.Exists(path))
                {
                    await using var input = File.OpenRead(path);
                    var hash = Convert.ToHexString(await SHA512.HashDataAsync(input, token));
                    if (hash != before.GetValueOrDefault(change.Path) && hash != after.GetValueOrDefault(change.Path))
                        throw new InvalidOperationException($"Modified file conflicts with the update: {change.Path}");
                }
                if (change.Kind == "−") File.Delete(path);
                else
                {
                    var source = LocalData.SafePath(scratch.Game(prepared.Id), change.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Copy(source, path, true);
                }
            }
            var result = prepared with { Id = target.Id, Name = target.Name, Ram = target.Ram };
            if (runtimeChange)
                File.Delete(Path.Combine(stage, "loader-version.json"));
            LocalData.Write(Path.Combine(stage, "instance.json"), result);
            LocalData.Write(Path.Combine(stage, "pack.json"), updated);
            LocalData.Write(Path.Combine(stage, "pack-files.json"), updated.Files.Keys.ToArray());
            token.ThrowIfCancellationRequested();
            var destination = data.Folder(target.Id);
            Directory.Move(destination, backup);
            try { Directory.Move(stage, destination); }
            catch
            {
                preserveBackup = true;
                Directory.Move(backup, destination);
                preserveBackup = false;
                throw;
            }
            return result;
        }
        finally { if (!preserveBackup) LocalData.Cleanup(work); }
    }
}
