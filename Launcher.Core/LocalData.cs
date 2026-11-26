using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public enum Loader { Vanilla, Fabric, Forge, Quilt, NeoForge }

public sealed record Profile(string Nick, string? SkinPath = null);
public sealed record ProfileState(Profile[] Profiles, string? Selected);
public sealed record Instance(Guid Id, string Name, string GameVersion, Loader Loader, string LoaderVersion, int Ram);
public sealed record Activity(string Text, double? Percent = null);

public sealed record Settings
{
    public string DataDirectory { get; init; } = @"%LOCALAPPDATA%\AwoLauncher";
    public int TimeoutSeconds { get; init; } = 120;
    public int DefaultRamMb { get; init; } = 4096;
    public string UserAgent { get; init; } = "AwoLauncher/1.0.0";
    public string LogFile { get; init; } = "launcher.log";
    public string? UpdateFeed { get; init; }
}

public sealed class LocalData(Settings settings)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static readonly string[] ContentFolders = ["mods", "config", "resourcepacks", "shaderpacks"];
    public string Root { get; } = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.DataDirectory));
    public string Instances => Path.Combine(Root, "instances");
    public string Runtime => Path.Combine(Root, "runtime");
    public string Folder(Guid id) => Path.Combine(Instances, id.ToString("N"));
    public string Game(Guid id) => Path.Combine(Folder(id), ".minecraft");
    public string Stage() => Path.Combine(Root, "staging", Guid.NewGuid().ToString("N"));

    public ProfileState Profiles => Read(Path.Combine(Root, "profiles.json"), new ProfileState([], null));

    public void SaveProfiles(ProfileState state) => Write(Path.Combine(Root, "profiles.json"), state);

    public ProfileState AddProfile(string nick)
    {
        nick = nick.Trim();
        if (!Regex.IsMatch(nick, @"\A[A-Za-z0-9_]{3,16}\z"))
            throw new InvalidOperationException("Ник должен содержать от 3 до 16 латинских букв, цифр или знаков подчёркивания.");
        var state = Profiles;
        var existing = state.Profiles.FirstOrDefault(p => p.Nick.Equals(nick, StringComparison.OrdinalIgnoreCase));
        state = existing is null ? new([.. state.Profiles, new Profile(nick)], nick) : state with { Selected = existing.Nick };
        SaveProfiles(state);
        return state;
    }

    public Instance[] List() => Directory.Exists(Instances)
        ? Directory.EnumerateDirectories(Instances)
            .Select(folder => Read<Instance?>(Path.Combine(folder, "instance.json"), null))
            .OfType<Instance>().OrderBy(instance => instance.Name).ToArray()
        : [];

    public static Instance Create(string name, string game, Loader loader, string loaderVersion, int ram)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new InvalidOperationException("Укажите название экземпляра длиной до 100 символов.");
        game = Segment(game.Trim());
        loaderVersion = loader == Loader.Vanilla ? "" : Segment(loaderVersion.Trim());
        if (ram is < 512 or > 65536)
            throw new InvalidOperationException("Укажите объём RAM от 512 до 65536 МБ.");
        return new(Guid.NewGuid(), name.Trim(), game, loader, loaderVersion, ram);
    }

    public void Publish(string staging, Instance instance, bool replaceEmpty = false)
    {
        InitGame(Path.Combine(staging, ".minecraft"));
        Write(Path.Combine(staging, "instance.json"), instance);
        Directory.CreateDirectory(Instances);
        var target = Folder(instance.Id);
        if (!replaceEmpty)
        {
            Directory.Move(staging, target);
            return;
        }

        if (!IsEmpty(instance.Id))
            throw new InvalidOperationException("Экземпляр содержит файлы. Создайте новый экземпляр для модпака.");
        var backup = Stage();
        Directory.Move(target, backup);
        try { Directory.Move(staging, target); }
        catch
        {
            Directory.Move(backup, target);
            throw;
        }
        Cleanup(backup);
    }

    public bool IsEmpty(Guid id)
    {
        var game = Game(id);
        return Directory.Exists(game) && Directory.EnumerateFileSystemEntries(game).All(path =>
            ContentFolders.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            && Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            && !Directory.EnumerateFileSystemEntries(path).Any());
    }

    public void Rename(Instance instance, string name)
    {
        var updated = Create(name, instance.GameVersion, instance.Loader, instance.LoaderVersion, instance.Ram);
        Write(Path.Combine(Folder(instance.Id), "instance.json"), instance with { Name = updated.Name });
    }

    public void Delete(Guid id) => Directory.Delete(Folder(id), true);

    public static void InitGame(string game)
    {
        foreach (var name in ContentFolders) Directory.CreateDirectory(Path.Combine(game, name));
    }

    public static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ')
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\')
            || Regex.IsMatch(value.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])\z", RegexOptions.IgnoreCase))
            throw new InvalidDataException($"Недопустимое имя файла или версии: {value}");
        return value;
    }

    public static string SafePath(string root, string relative)
    {
        if (Directory.Exists(root) && File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"Корневая папка экземпляра является ссылкой: {root}");
        var result = root;
        foreach (var part in relative.Split('/'))
        {
            result = Path.Combine(result, Segment(part));
            if ((File.Exists(result) || Directory.Exists(result)) &&
                File.GetAttributes(result).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Ссылка в пути установки: {relative}");
        }
        return result;
    }

    public static T Read<T>(string path, T fallback) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? fallback : fallback;

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    public static void Cleanup(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (IOException exception) { System.Diagnostics.Trace.TraceError(exception.ToString()); }
        catch (UnauthorizedAccessException exception) { System.Diagnostics.Trace.TraceError(exception.ToString()); }
    }
}
