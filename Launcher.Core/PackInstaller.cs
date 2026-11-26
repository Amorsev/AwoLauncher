using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Launcher.Core;

public sealed record PackFile(string Path, Dictionary<string, string> Hashes, string[] Downloads, Dictionary<string, string>? Env)
{
    public string Client => Env?.GetValueOrDefault("client", "required") ?? "required";
}

public sealed record PackIndex(int FormatVersion, string Game, string Name,
    [property: JsonPropertyName("versionId")] string Version, Dictionary<string, string> Dependencies, PackFile[] Files);

public sealed record PackOrigin(string ProjectId, string VersionId, string VersionName, Dictionary<string, string> Files);

public sealed class PackInstaller(Modrinth api, LocalData data, GameLauncher launcher, Settings settings)
{
    public async Task<Instance> InstallAsync(string? localFile, ModVersion? remote, Instance? target,
        Func<PackFile[], Task<HashSet<string>?>> chooseOptional, IProgress<Activity> progress, CancellationToken token,
        bool prepareRuntime = true)
    {
        if (target is not null && !data.IsEmpty(target.Id))
            throw new InvalidOperationException("Экземпляр содержит файлы. Создайте новый экземпляр для модпака.");

        var work = data.Stage();
        var stage = Path.Combine(work, "instance");
        var game = Path.Combine(stage, ".minecraft");
        Directory.CreateDirectory(game);
        try
        {
            if (remote is not null)
            {
                var file = Modrinth.Primary(remote);
                localFile = Path.Combine(work, "pack.mrpack");
                await api.DownloadAsync(file.Url, localFile, file.Hashes.GetValueOrDefault("sha512", ""), progress, token);
            }
            Instance instance;
            using (var zip = ZipFile.OpenRead(localFile ?? throw new InvalidOperationException("Выберите файл .mrpack.")))
            {
                var manifests = zip.Entries.Where(entry => entry.FullName == "modrinth.index.json").ToArray();
                if (manifests.Length != 1 || manifests[0].Length > 16 * 1024 * 1024)
                    throw new InvalidDataException("В архиве должен быть один корректный modrinth.index.json.");
                await using var manifest = manifests[0].Open();
                var index = await JsonSerializer.DeserializeAsync<PackIndex>(manifest, LocalData.Json, token)
                    ?? throw new InvalidDataException("Манифест модпака пуст.");
                instance = CreateInstance(index, target);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in index.Files)
                {
                    _ = LocalData.SafePath(game, file.Path);
                    if (!paths.Add(file.Path) || file.Client is not ("required" or "optional" or "unsupported"))
                        throw new InvalidDataException($"Некорректная запись файла модпака: {file.Path}");
                }
                var optional = index.Files.Where(file => file.Client == "optional").ToArray();
                var included = optional.Length == 0 ? [] : await chooseOptional(optional);
                if (included is null) throw new OperationCanceledException();
                foreach (var file in index.Files.Where(file => file.Client == "required" || included.Contains(file.Path)))
                    await DownloadPackFile(file, LocalData.SafePath(game, file.Path), progress, token);

                await ExtractOverrides(zip, game, "overrides/", token);
                await ExtractOverrides(zip, game, "client-overrides/", token);
            }
            var packPaths = InstanceService.Files(game).Select(file =>
                Path.GetRelativePath(game, file.FullName).Replace('\\', '/')).ToArray();
            LocalData.Write(Path.Combine(stage, "pack-files.json"), packPaths);
            if (remote is not null)
            {
                var managed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in packPaths)
                {
                    await using var input = File.OpenRead(LocalData.SafePath(game, path));
                    managed.Add(path,
                        Convert.ToHexString(await SHA512.HashDataAsync(input, token)));
                }
                LocalData.Write(Path.Combine(stage, "pack.json"), new PackOrigin(remote.ProjectId, remote.Id, remote.Name, managed));
            }
            if (!prepareRuntime)
            {
                token.ThrowIfCancellationRequested();
                data.Publish(stage, instance, target is not null);
                return instance;
            }

            // Prepare the runtime in a clean directory so pack overrides cannot influence installer execution.
            var runtime = Path.Combine(work, "runtime");
            var runtimeGame = Path.Combine(runtime, ".minecraft");
            Directory.CreateDirectory(runtimeGame);
            await launcher.PrepareAsync(instance, runtimeGame, progress, token);
            foreach (var source in Directory.EnumerateFileSystemEntries(runtimeGame))
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(game, Path.GetFileName(source));
                if (File.Exists(destination) || Directory.Exists(destination))
                    throw new InvalidDataException($"Файл модпака конфликтует с компонентами игры: {Path.GetFileName(source)}");
                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination);
            }
            var marker = Path.Combine(runtime, "loader-version.json");
            if (File.Exists(marker)) File.Move(marker, Path.Combine(stage, "loader-version.json"));
            token.ThrowIfCancellationRequested();
            data.Publish(stage, instance, target is not null);
            return instance;
        }
        finally { LocalData.Cleanup(work); }
    }

    private Instance CreateInstance(PackIndex index, Instance? target)
    {
        if (index.FormatVersion != 1 || index.Game != "minecraft" ||
            !index.Dependencies.TryGetValue("minecraft", out var game))
            throw new InvalidDataException("Неподдерживаемый формат модпака или отсутствует версия Minecraft.");
        var loaders = index.Dependencies.Where(entry => entry.Key != "minecraft").ToArray();
        if (loaders.Length > 1) throw new InvalidDataException("Модпак указывает несколько загрузчиков.");
        var loader = loaders.FirstOrDefault().Key switch
        {
            null => Loader.Vanilla,
            "fabric-loader" => Loader.Fabric,
            "quilt-loader" => Loader.Quilt,
            "forge" => Loader.Forge,
            "neoforge" => Loader.NeoForge,
            var unknown => throw new InvalidDataException($"Неизвестный загрузчик: {unknown}")
        };
        var instance = LocalData.Create(target?.Name ?? index.Name, game, loader,
            loaders.FirstOrDefault().Value ?? "", target?.Ram ?? settings.DefaultRamMb);
        return target is null ? instance : instance with { Id = target.Id };
    }

    private async Task DownloadPackFile(PackFile file, string destination, IProgress<Activity> progress, CancellationToken token)
    {
        Exception? failure = null;
        foreach (var url in file.Downloads)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var address) ||
                    !new[] { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" }
                        .Contains(address.Host, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Хост файла отсутствует в списке разрешённых для .mrpack.");
                await api.DownloadAsync(url, destination, file.Hashes.GetValueOrDefault("sha512", ""), progress, token);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TimeoutException or InvalidDataException ||
                                              exception is TaskCanceledException && !token.IsCancellationRequested)
            {
                failure = exception;
            }
        }
        throw new InvalidDataException($"Не удалось загрузить файл модпака {file.Path}. {failure?.Message}", failure);
    }

    private static async Task ExtractOverrides(ZipArchive zip, string game, string prefix, CancellationToken token)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName[prefix.Length..];
            if (relative.Length == 0) continue;
            var directory = relative.EndsWith('/');
            var destination = LocalData.SafePath(game, directory ? relative[..^1] : relative);
            if (directory) continue;
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !paths.Add(destination))
                throw new InvalidDataException($"Некорректная запись архива: {relative}");
            expanded = checked(expanded + entry.Length);
            if (expanded > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Размер распакованных файлов превышает 4 ГБ.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            await input.CopyToAsync(output, token);
        }
    }
}
