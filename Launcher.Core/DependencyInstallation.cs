using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed partial class ContentInstaller
{
    public async Task InstallAsync(Instance target, Project project, ModVersion version, IProgress<Activity> progress,
        CancellationToken token)
    {
        var folder = project.Type switch
        {
            "mod" => "mods",
            "resourcepack" => "resourcepacks",
            "shader" => "shaderpacks",
            _ => throw new InvalidOperationException("Этот тип проекта нельзя установить отдельным файлом.")
        };
        if (version.ProjectId != project.Id || !Modrinth.Compatible(version, target, project.Type))
            throw new InvalidOperationException($"Версия {version.Label} несовместима с экземпляром.");

        var game = data.Game(target.Id);
        var protectedFiles = new HashSet<string>(LocalData.Read(Path.Combine(data.Folder(target.Id), "pack-files.json"),
            Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
        var pack = LocalData.Read<PackOrigin?>(Path.Combine(data.Folder(target.Id), "pack.json"), null);
        if (pack is not null) protectedFiles.UnionWith(pack.Files.Keys);
        var inventory = GetContent(target.Id);
        var installed = new Dictionary<string, List<InstanceContent>>(StringComparer.Ordinal);
        var versionCache = new Dictionary<string, ModVersion>(StringComparer.Ordinal) { [version.Id] = version };
        var projectCache = new Dictionary<string, ModVersion[]>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Identify manually imported files and files installed by .mrpack through their content.
        var unknown = new List<(InstanceContent Item, string Hash)>();
        foreach (var item in inventory.Where(item => !item.IsDirectory && (item.Kind == "mods" || item.Kind == folder)))
        {
            token.ThrowIfCancellationRequested();
            var hash = await HashAsync(LocalData.SafePath(game, item.DiskPath), token);
            hashes[item.DiskPath] = hash;
            if (item.File.ProjectId.Length > 0 && item.File.Sha512.Equals(hash, StringComparison.OrdinalIgnoreCase))
                Register(item);
            else
                unknown.Add((item, hash));
        }
        foreach (var batch in unknown.Select(item => item.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            var matches = new Dictionary<string, ModVersion>(await api.VersionsFromHashesAsync(batch, token), StringComparer.OrdinalIgnoreCase);
            foreach (var (item, hash) in unknown.Where(item => matches.ContainsKey(item.Hash)))
            {
                var found = matches[hash];
                versionCache[found.Id] = found;
                Register(item with { File = new(item.File.Path, found.ProjectId, found.Id, hash,
                    found.Dependencies.Where(dep => dep.Type == "required" && dep.ProjectId is not null)
                        .Select(dep => dep.ProjectId!).Distinct().ToArray(), found.Name, found.Number) });
            }
        }

        var choices = new Dictionary<string, ModVersion>(StringComparer.Ordinal) { [project.Id] = version };
        if (Local(project.Id) is { Enabled: false } disabled)
            throw new InvalidOperationException($"Файл «{disabled.Name}» отключён. Включите или удалите его вручную.");
        var budget = 1024;
        string? problem = null;
        if (!await ResolveAsync())
            throw new InvalidOperationException(problem ?? "Не удалось подобрать совместимые версии зависимостей.");

        var candidates = new List<(InstalledFile Entry, ModFile File, InstanceContent? Previous)>();
        var adopted = new List<InstalledFile>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chosen in choices.Values)
        {
            token.ThrowIfCancellationRequested();
            var previous = Local(chosen.ProjectId);
            var dependencies = await RequirementsAsync(chosen);
            if (previous?.File.VersionId == chosen.Id)
            {
                adopted.Add(previous.File with { Requires = dependencies.Select(dep => dep.Id).Distinct().ToArray() });
                continue;
            }
            var file = Modrinth.Primary(chosen);
            var path = (chosen.ProjectId == project.Id ? folder : "mods") + "/" + LocalData.Segment(file.Filename);
            if (protectedFiles.Contains(path) || previous is not null && protectedFiles.Contains(previous.File.Path))
                throw new InvalidOperationException($"Файл сборки «{previous?.Name ?? file.Filename}» меняется только вручную.");
            if (!reserved.Add(path) || File.Exists(LocalData.SafePath(game, path + ".disabled")))
                throw new InvalidOperationException($"Файл «{file.Filename}» уже существует или отключён.");
            var destination = LocalData.SafePath(game, path);
            var entry = new InstalledFile(path, chosen.ProjectId, chosen.Id, file.Hashes.GetValueOrDefault("sha512", ""),
                dependencies.Select(dep => dep.Id).Distinct().ToArray(), chosen.ProjectId == project.Id ? project.Title : chosen.Name, chosen.Number);
            if (File.Exists(destination) && !(previous is not null && previous.File.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                if (previous is not null || !entry.Sha512.Equals(await HashAsync(destination, token), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Файл «{file.Filename}» уже занят другим содержимым.");
                adopted.Add(entry);
            }
            else candidates.Add((entry, file, previous));
        }

        var stage = data.Stage();
        var backups = new List<(string Original, string Copy)>();
        var committed = new List<string>();
        var keepStage = false;
        try
        {
            foreach (var (entry, file, _) in candidates)
                await api.DownloadAsync(file.Url, LocalData.SafePath(stage, "download/" + entry.Path), entry.Sha512, progress, token);
            var registered = installed.Values.SelectMany(items => items).Select(item => item.DiskPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var manualMods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (item, _) in unknown.Where(entry => entry.Item.Enabled && entry.Item.Kind == "mods" &&
                         !registered.Contains(entry.Item.DiskPath)))
            {
                token.ThrowIfCancellationRequested();
                foreach (var id in ModInspection.Metadata(LocalData.SafePath(game, item.DiskPath)).Ids)
                    manualMods.TryAdd(id, item.Name);
            }
            foreach (var (entry, _, _) in candidates.Where(candidate => candidate.Entry.Path.StartsWith("mods/", StringComparison.Ordinal)))
            {
                token.ThrowIfCancellationRequested();
                var ids = ModInspection.Metadata(LocalData.SafePath(stage, "download/" + entry.Path)).Ids;
                var duplicate = ids.FirstOrDefault(manualMods.ContainsKey);
                if (duplicate is not null)
                    throw new InvalidOperationException($"Файл «{manualMods[duplicate]}» уже содержит мод {duplicate}, но его версия неизвестна. Проверьте файл вручную.");
            }
            foreach (var (_, _, previous) in candidates)
            {
                if (previous is null) continue;
                var original = LocalData.SafePath(game, previous.DiskPath);
                if (!hashes[previous.DiskPath].Equals(await HashAsync(original, token), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Файл «{previous.Name}» был изменён во время установки.");
            }
            var changedPaths = new HashSet<string>(adopted.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, _, previous) in candidates)
            {
                changedPaths.Add(entry.Path);
                if (previous is not null) changedPaths.Add(previous.File.Path);
            }
            var registry = Read(target.Id).Where(file => !changedPaths.Contains(file.Path))
                .Concat(adopted).Concat(candidates.Select(item => item.Entry)).ToArray();
            token.ThrowIfCancellationRequested();
            foreach (var (_, _, previous) in candidates)
            {
                if (previous is null) continue;
                var original = LocalData.SafePath(game, previous.DiskPath);
                var backup = LocalData.SafePath(stage, "backup/" + previous.DiskPath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(original, backup);
                backups.Add((original, backup));
            }
            foreach (var (entry, _, _) in candidates)
            {
                var destination = LocalData.SafePath(game, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(LocalData.SafePath(stage, "download/" + entry.Path), destination);
                committed.Add(destination);
            }
            LocalData.Write(Registry(target.Id), registry);
        }
        catch
        {
            keepStage = true;
            foreach (var file in committed) File.Delete(file);
            foreach (var backup in backups.AsEnumerable().Reverse()) File.Move(backup.Copy, backup.Original);
            keepStage = false;
            throw;
        }
        finally { if (!keepStage) LocalData.Cleanup(stage); }

        void Register(InstanceContent item)
        {
            if (!installed.TryGetValue(item.File.ProjectId, out var list)) installed[item.File.ProjectId] = list = [];
            list.Add(item);
        }

        InstanceContent? Local(string id)
        {
            if (!installed.TryGetValue(id, out var items)) return null;
            if (items.Count != 1) throw new InvalidOperationException($"В экземпляре несколько файлов проекта {id}. Устраните дубликаты вручную.");
            return items[0];
        }

        async Task<ModVersion> VersionAsync(string id)
        {
            if (!versionCache.TryGetValue(id, out var result)) versionCache[id] = result = await api.VersionAsync(id, token);
            return result;
        }

        async Task<(string Id, ModVersion? Minimum)[]> RequirementsAsync(ModVersion chosen, bool validateExternal = true)
        {
            var result = new List<(string, ModVersion?)>();
            foreach (var dependency in chosen.Dependencies.Where(dep => dep.Type == "required"))
            {
                if (dependency.VersionId is { Length: > 0 } exact)
                {
                    var floor = await VersionAsync(exact);
                    result.Add((floor.ProjectId, floor));
                }
                else if (dependency.ProjectId is { Length: > 0 } id)
                    result.Add((id, null));
                else if (validateExternal &&
                    (dependency.FileName is not { Length: > 0 } name || inventory.All(entry => !entry.Enabled || entry.Name != name)))
                    throw new InvalidOperationException($"У {chosen.Label} есть зависимость без версии Modrinth: {dependency.FileName ?? "не указана"}. Установите её вручную.");
            }
            return result.ToArray();
        }

        async Task<bool> ResolveAsync()
        {
            token.ThrowIfCancellationRequested();
            if (--budget < 0) return false;
            var required = new Dictionary<string, List<ModVersion?>>(StringComparer.Ordinal);
            foreach (var chosen in choices.Values)
            {
                if (chosen.Dependencies.Where(dep => dep.Type == "incompatible").Any(dep =>
                    choices.Values.Any(other => Matches(other.ProjectId, other.Id, dep)) ||
                    installed.Values.SelectMany(items => items).Any(item => item.Enabled &&
                        !choices.ContainsKey(item.File.ProjectId) && Matches(item.File.ProjectId, item.File.VersionId, dep))))
                    return false;
                (string Id, ModVersion? Minimum)[] requirements;
                try { requirements = await RequirementsAsync(chosen); }
                catch (InvalidOperationException error)
                {
                    problem = error.Message;
                    return false;
                }
                foreach (var (id, floor) in requirements)
                {
                    if (!required.TryGetValue(id, out var floors)) required[id] = floors = [];
                    floors.Add(floor);
                }
            }
            foreach (var (id, floors) in required)
                if (choices.TryGetValue(id, out var chosen) && floors.Any(floor => !Satisfies(chosen, floor)))
                    return false;
            var missing = required.FirstOrDefault(pair => !choices.ContainsKey(pair.Key));
            if (missing.Key is null) return await PreservesInstalledAsync();

            var local = Local(missing.Key);
            ModVersion? localVersion = local is null ? null : await VersionAsync(local.File.VersionId);
            bool Eligible(ModVersion item) => Modrinth.Compatible(item, target, "mod") &&
                missing.Value.All(floor => Satisfies(item, floor));
            if (local is not null && !local.Enabled)
            {
                problem = $"Необходимый файл «{local.Name}» отключён. Включите или удалите его вручную.";
                return false;
            }
            if (localVersion is not null && Eligible(localVersion))
            {
                choices.Add(missing.Key, localVersion);
                if (await ResolveAsync()) return true;
                choices.Remove(missing.Key);
            }
            if (local is not null && protectedFiles.Contains(local.File.Path))
            {
                problem = $"Необходимый файл «{local.Name}» входит в сборку. Замените его вручную.";
                return false;
            }
            if (!projectCache.TryGetValue(missing.Key, out var available))
                projectCache[missing.Key] = available = await api.VersionsAsync(missing.Key, token);
            var options = available.Concat(missing.Value.OfType<ModVersion>()).DistinctBy(item => item.Id)
                .Where(item => item.Id != localVersion?.Id && Eligible(item) &&
                    (localVersion is null || Satisfies(item, localVersion)))
                .OrderBy(item => item.Type == "release" ? 0 : 1)
                .ThenByDescending(item => NumericVersion(item.Number))
                .ThenByDescending(item => item.Published);
            foreach (var candidate in options)
            {
                if (budget <= 0) break;
                choices.Add(missing.Key, candidate);
                if (await ResolveAsync()) return true;
                choices.Remove(missing.Key);
            }
            problem ??= $"Нет совместимой версии зависимости {missing.Key}.";
            return false;
        }

        async Task<bool> PreservesInstalledAsync()
        {
            var changed = choices.Where(pair => Local(pair.Key)?.File.VersionId != pair.Value.Id)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var item in installed.Values.SelectMany(list => list).Where(item => item.Enabled && !choices.ContainsKey(item.File.ProjectId)))
            {
                if (!versionCache.ContainsKey(item.File.VersionId) && !item.File.Requires.Any(changed.ContainsKey)) continue;
                var previous = await VersionAsync(item.File.VersionId);
                foreach (var (id, floor) in await RequirementsAsync(previous, validateExternal: false))
                {
                    if (!changed.TryGetValue(id, out var next) || Satisfies(next, floor)) continue;
                    problem = $"Обновление «{next.Name}» несовместимо с установленным файлом «{item.Name}».";
                    return false;
                }
                if (previous.Dependencies.Where(dep => dep.Type == "incompatible").Any(dep =>
                    changed.Values.Any(next => Matches(next.ProjectId, next.Id, dep)))) return false;
            }
            return true;
        }
    }

    private static bool Matches(string projectId, string versionId, Dependency dependency) =>
        dependency.VersionId is { Length: > 0 } exact ? exact == versionId :
        dependency.ProjectId is { Length: > 0 } project && project == projectId;

    private static bool Satisfies(ModVersion candidate, ModVersion? minimum)
    {
        if (minimum is null || minimum.Id == candidate.Id) return true;
        if (!candidate.ProjectId.Equals(minimum.ProjectId, StringComparison.Ordinal)) return false;
        var lower = NumericVersion(minimum.Number);
        var upper = NumericVersion(candidate.Number);
        return lower is not null && upper is not null && upper.Major == lower.Major && upper >= lower &&
            (candidate.Type == "release" || minimum.Type != "release");
    }

    private static Version? NumericVersion(string number)
    {
        var match = Regex.Match(number, @"^(?:mc\d+(?:\.\d+)+-)?v?(\d+\.\d+(?:\.\d+)?)(?:\+[\w.-]+)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var parsed)
            ? new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build)) : null;
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA512.HashDataAsync(input, token)).ToLowerInvariant();
    }
}
