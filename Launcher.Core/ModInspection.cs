using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record ModNotice(string Path, string Key);

public static class ModInspection
{
    public static ModNotice[] Inspect(string[] files, string game, Instance instance, CancellationToken token, bool includeExisting = true)
    {
        var mods = LocalData.SafePath(game, "mods");
        var existing = includeExisting && Directory.Exists(mods) ? Directory.EnumerateFiles(mods, "*.jar") : [];
        var names = new HashSet<string>(existing.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        var ids = existing.Select(Metadata).SelectMany(item => item.Ids).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ModNotice> notices = [];
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            var metadata = Metadata(path);
            var duplicateId = metadata.Ids.Select(id => ids.Add(id)).ToArray().Any(added => !added);
            if (!names.Add(Path.GetFileName(path)) || duplicateId)
                notices.Add(new(path, "ModDuplicate"));
            if (instance.Loader == Loader.Vanilla || metadata.Loaders.Length > 0 && !metadata.Loaders.Contains(instance.Loader)
                && !(instance.Loader == Loader.Quilt && metadata.Loaders.Contains(Loader.Fabric)))
                notices.Add(new(path, "ModLoaderConflict"));
            var gameRequirement = metadata.GameVersion;
            if (gameRequirement is not null && CompatibleVersion(instance.GameVersion, gameRequirement) == false)
                notices.Add(new(path, "ModGameConflict"));
            if (metadata.Loaders.Length == 0) notices.Add(new(path, "ModUnknown"));
        }
        return notices.ToArray();
    }

    private static bool? CompatibleVersion(string game, string requirement)
    {
        static Version? Parse(string text) => Version.TryParse(text.Trim(), out var parsed)
            ? new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build)) : null;
        if (Parse(game) is not { } actual || string.IsNullOrWhiteSpace(requirement) || requirement == "*") return null;
        var alternatives = requirement.Split("||", StringSplitOptions.TrimEntries);
        if (alternatives.Length > 1)
        {
            var results = alternatives.Select(item => CompatibleVersion(game, item)).ToArray();
            return results.Contains(true) ? true : results.Contains(null) ? null : false;
        }
        var interval = Regex.Match(requirement, @"^(\[|\()([^,]*),([^,]*)(\]|\))$");
        if (interval.Success)
        {
            var lower = Parse(interval.Groups[2].Value);
            var upper = Parse(interval.Groups[3].Value);
            if (lower is null && upper is null) return null;
            return (lower is null || actual > lower || interval.Groups[1].Value == "[" && actual == lower) &&
                   (upper is null || actual < upper || interval.Groups[4].Value == "]" && actual == upper);
        }
        var comparisons = requirement.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = comparisons.Select(item => Regex.Match(item, @"^(>=|<=|>|<|=)?(\d+\.\d+(?:\.\d+)?)$")).ToArray();
        if (matches.Length == 0 || matches.Any(item => !item.Success)) return null;
        return matches.All(item =>
        {
            var expected = Parse(item.Groups[2].Value);
            return item.Groups[1].Value switch
            {
                ">=" => actual >= expected, "<=" => actual <= expected, ">" => actual > expected, "<" => actual < expected,
                _ => actual == expected
            };
        });
    }

    public static async Task ImportAsync(string[] paths, string game, string stage, CancellationToken token)
    {
        var moved = new List<string>();
        try
        {
            Directory.CreateDirectory(stage);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                using var archive = ZipFile.OpenRead(path);
                var name = LocalData.Segment(Path.GetFileName(path));
                var destination = LocalData.SafePath(game, "mods/" + name);
                if (File.Exists(destination) || !names.Add(name)) throw new IOException($"Mod file exists: {name}");
                await using var input = File.OpenRead(path);
                await using var output = File.Create(Path.Combine(stage, name));
                await input.CopyToAsync(output, token);
            }
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(LocalData.SafePath(game, "mods"));
            foreach (var name in names)
            {
                var destination = LocalData.SafePath(game, "mods/" + name);
                File.Move(Path.Combine(stage, name), destination);
                moved.Add(destination);
            }
        }
        catch
        {
            foreach (var path in moved) File.Delete(path);
            throw;
        }
        finally { LocalData.Cleanup(stage); }
    }

    internal static (string[] Ids, Loader[] Loaders, string? GameVersion) Metadata(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            List<Loader> loaders = [];
            var ids = new HashSet<string>();
            string? version = null;
            foreach (var (name, loader) in new[] { ("fabric.mod.json", Loader.Fabric), ("quilt.mod.json", Loader.Quilt),
                         ("META-INF/mods.toml", Loader.Forge), ("META-INF/neoforge.mods.toml", Loader.NeoForge) })
            {
                if (archive.GetEntry(name) is not { } entry || entry.Length > 1024 * 1024) continue;
                loaders.Add(loader);
                using var input = entry.Open();
                if (name.EndsWith(".toml"))
                {
                    using var reader = new StreamReader(input);
                    var blocks = Regex.Split(reader.ReadToEnd(), @"(?m)^\s*\[\[");
                    foreach (var block in blocks)
                    {
                        var modId = Regex.Match(block, @"(?m)^\s*modId\s*=\s*[""']([^""']+)").Groups[1].Value;
                        if (block.StartsWith("mods]]") && modId.Length > 0) ids.Add(modId);
                        else if (block.StartsWith("dependencies.") && modId == "minecraft")
                        {
                            var range = Regex.Match(block, @"(?m)^\s*versionRange\s*=\s*[""']([^""']+)").Groups[1].Value;
                            if (range.Length > 0) version = range;
                        }
                    }
                    continue;
                }
                using var document = JsonDocument.Parse(input);
                var root = document.RootElement;
                var meta = root.TryGetProperty("quilt_loader", out var quilt) ? quilt : root;
                if (meta.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) ids.Add(id.GetString()!);
                if (root.TryGetProperty("depends", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object &&
                    dependencies.TryGetProperty("minecraft", out var requirement) && requirement.ValueKind == JsonValueKind.String)
                    version = requirement.GetString();
            }
            return (ids.ToArray(), loaders.ToArray(), version);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException)
        {
            return ([], [], null);
        }
    }
}
