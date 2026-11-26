using System.Diagnostics;
using System.Net.Http.Json;

namespace Launcher.Core;

public sealed record LauncherRelease(string Version, string Url, string Sha512);

public sealed class UpdateService(HttpClient http, string? feed)
{
    public bool Configured => !string.IsNullOrWhiteSpace(feed);

    public async Task<LauncherRelease?> CheckAsync(Version current, CancellationToken token)
    {
        if (!Configured) return null;
        if (!Uri.TryCreate(feed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            throw new InvalidDataException("The update feed must use HTTPS.");
        var release = await http.GetFromJsonAsync<LauncherRelease>(uri, token);
        if (release is null || !Version.TryParse(release.Version, out var version))
            throw new InvalidDataException("The update feed returned an invalid version.");
        var requested = new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
        return requested > current ? release : null;
    }

    public async Task InstallAsync(LauncherRelease release, string root, IProgress<Activity> progress, CancellationToken token)
    {
        if (!Uri.TryCreate(release.Url, UriKind.Absolute, out var uri) || !uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update must point to a Windows installer (.exe).");
        var destination = Path.Combine(root, "updates", Guid.NewGuid().ToString("N"), "AwoLauncher.Setup.exe");
        await new Modrinth(http).DownloadAsync(release.Url, destination, release.Sha512, progress, token);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true })
            ?? throw new InvalidOperationException("Could not start the update installer.");
    }
}
