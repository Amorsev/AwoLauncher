using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core;

public sealed record ProjectHit([property: JsonPropertyName("project_id")] string Id, string Title, string Description,
    [property: JsonPropertyName("project_type")] string Type, string? Author = null, long Downloads = 0,
    [property: JsonPropertyName("icon_url")] string? IconUrl = null, string[]? Categories = null)
{
    public Uri? Icon => Uri.TryCreate(IconUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "cdn.modrinth.com" && uri.UserInfo.Length == 0 ? uri : null;
    public string Loaders => string.Join(" · ", (Categories ?? []).Where(item => item is "fabric" or "forge" or "neoforge" or "quilt")
        .Select(item => item == "neoforge" ? "NeoForge" : char.ToUpperInvariant(item[0]) + item[1..]));
}
public sealed record SearchPage(ProjectHit[] Hits, [property: JsonPropertyName("total_hits")] int Total);
public sealed record ProjectImage(string Url, bool Featured, string? Title,
    [property: JsonPropertyName("raw_url")] string? RawUrl = null)
{
    public Uri? Image => Modrinth.CdnImage(Url);
    public Uri? OriginalImage => Modrinth.CdnImage(RawUrl) ?? Image;
}
public sealed record Project(string Id, string Title, string Description, string Body,
    [property: JsonPropertyName("project_type")] string Type,
    [property: JsonPropertyName("icon_url")] string? IconUrl = null, long Downloads = 0, ProjectImage[]? Gallery = null,
    string? Slug = null)
{
    public Uri? Icon => Modrinth.CdnImage(IconUrl);
}
public sealed record ModFile(string Filename, string Url, bool Primary, Dictionary<string, string> Hashes);
public sealed record Dependency([property: JsonPropertyName("version_id")] string? VersionId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("dependency_type")] string Type,
    [property: JsonPropertyName("file_name")] string? FileName = null);
public sealed record ModVersion(string Id, [property: JsonPropertyName("project_id")] string ProjectId,
    string Name, [property: JsonPropertyName("version_number")] string Number,
    [property: JsonPropertyName("game_versions")] string[] GameVersions, string[] Loaders,
    [property: JsonPropertyName("date_published")] DateTimeOffset Published,
    [property: JsonPropertyName("version_type")] string Type, ModFile[] Files, Dependency[] Dependencies, string? Changelog = null)
{
    public string Label => $"{Name} ({Number})";
}

public sealed class Modrinth(HttpClient http)
{
    public const int PageSize = 20;
    private const string Api = "https://api.modrinth.com/v2/";

    public Task<SearchPage> SearchAsync(string query, string type, string game, string loader, string sort, int offset, CancellationToken token)
    {
        List<string[]> facets = [];
        if (type.Length > 0) facets.Add([$"project_type:{type}"]);
        if (game.Length > 0) facets.Add([$"versions:{game}"]);
        if (loader.Length > 0) facets.Add([$"categories:{loader}"]);
        return Get<SearchPage>($"search?query={Escape(query)}&facets={Escape(JsonSerializer.Serialize(facets))}&index={Escape(sort)}&offset={offset}&limit={PageSize}", token);
    }

    public Task<Project> ProjectAsync(string id, CancellationToken token) => Get<Project>($"project/{Escape(id)}", token);
    public Task<Project[]> ProjectsAsync(IEnumerable<string> ids, CancellationToken token) =>
        Get<Project[]>($"projects?ids={Escape(JsonSerializer.Serialize(ids))}", token);
    public Task<ModVersion[]> VersionsAsync(string id, CancellationToken token) => Get<ModVersion[]>($"project/{Escape(id)}/version", token);
    public Task<ModVersion> VersionAsync(string id, CancellationToken token) => Get<ModVersion>($"version/{Escape(id)}", token);

    public async Task<Dictionary<string, ModVersion>> VersionsFromHashesAsync(string[] hashes, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(Api + "version_files", new { hashes, algorithm = "sha512" }, token);
        Check(response);
        return await response.Content.ReadFromJsonAsync<Dictionary<string, ModVersion>>(LocalData.Json, token)
            ?? throw new InvalidDataException("Modrinth вернул пустой ответ.");
    }

    internal static Uri? CdnImage(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "cdn.modrinth.com" && uri.UserInfo.Length == 0 ? uri : null;

    private async Task<T> Get<T>(string path, CancellationToken token)
    {
        using var response = await http.GetAsync(Api + path, token);
        Check(response);
        return await response.Content.ReadFromJsonAsync<T>(LocalData.Json, token)
            ?? throw new InvalidDataException("Modrinth вернул пустой ответ.");
    }

    public async Task DownloadAsync(string url, string destination, string sha512, IProgress<Activity> progress, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            throw new InvalidDataException("Адрес загрузки должен использовать HTTPS.");
        if (sha512.Length != 128 || !sha512.All(Uri.IsHexDigit))
            throw new InvalidDataException("Отсутствует корректный хеш SHA-512.");

        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        Check(response);
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Сервер перенаправил загрузку на небезопасный адрес.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            var buffer = new byte[65536];
            long read = 0;
            var total = response.Content.Headers.ContentLength;
            var report = System.Diagnostics.Stopwatch.StartNew();
            progress.Report(new($"Загрузка {Path.GetFileName(destination)}"));
            int length;
            while (true)
            {
                readTimeout.CancelAfter(http.Timeout);
                length = await source.ReadAsync(buffer, readTimeout.Token);
                readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if (length == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, length), token);
                hash.AppendData(buffer, 0, length);
                read += length;
                if (report.ElapsedMilliseconds < 150) continue;
                progress.Report(new($"Загрузка {Path.GetFileName(destination)}", total > 0 ? read * 100d / total : null));
                report.Restart();
            }

            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Контрольная сумма файла {Path.GetFileName(destination)} не совпадает.");
            await target.DisposeAsync();
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Сервер перестал передавать данные.", exception);
        }
        finally { File.Delete(temporary); }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Сервис ограничил число запросов. Повторите попытку позже.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    public static ModFile Primary(ModVersion version) => version.Files.FirstOrDefault(file => file.Primary)
        ?? version.Files.FirstOrDefault() ?? throw new InvalidDataException("У выбранной версии нет файлов.");

    public static bool Compatible(ModVersion version, Instance instance, string type) =>
        version.GameVersions.Contains(instance.GameVersion) &&
        (type != "mod" || version.Loaders.Contains(instance.Loader.ToString(), StringComparer.OrdinalIgnoreCase)
            && instance.Loader != Loader.Vanilla);
}
