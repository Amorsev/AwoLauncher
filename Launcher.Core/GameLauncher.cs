using System.Net.Http.Json;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installers;
using CmlLib.Core.Java;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Rules;

namespace Launcher.Core;

public sealed class GameLauncher(HttpClient http, string? sharedRuntime = null)
{
    private MinecraftPath GamePath(string gamePath)
    {
        var path = new MinecraftPath(gamePath);
        if (sharedRuntime is not null) path.Runtime = Path.GetFullPath(sharedRuntime);
        return path;
    }

    public async Task<string> InstallJavaAsync(Instance instance, string gamePath, IProgress<Activity> progress, CancellationToken token)
    {
        var path = GamePath(gamePath);
        var parameters = MinecraftLauncherParameters.CreateDefault(path, http);
        var launcher = new MinecraftLauncher(parameters);
        var version = await launcher.GetVersionAsync(instance.GameVersion, token);
        var extractor = new JavaFileExtractor(http, new MinecraftJavaPathResolver(path));
        var files = await extractor.Extract(path, version, new RulesEvaluatorContext(LauncherOSRule.Current), token);
        await launcher.GameInstaller.Install(files, new Progress<InstallerProgressChangedEventArgs>(args =>
            progress.Report(new("Java", args.TotalTasks > 0 ? args.ProgressedTasks * 100d / args.TotalTasks : null))), null, token);
        var java = launcher.GetJavaPath(version);
        if (!File.Exists(java)) throw new InvalidOperationException("Не удалось найти установленную Java.");
        return java;
    }
    public Task<string[]> LoadersAsync(Loader loader, string game, CancellationToken token) => loader switch
    {
        Loader.Vanilla => Task.FromResult(Array.Empty<string>()),
        Loader.Fabric or Loader.Quilt => MetaLoadersAsync(loader, game, token),
        Loader.Forge => ForgeLoadersAsync(game, token),
        Loader.NeoForge => NeoForgeLoadersAsync(game, token),
        _ => throw new ArgumentOutOfRangeException(nameof(loader))
    };

    private async Task<string[]> MetaLoadersAsync(Loader loader, string game, CancellationToken token)
    {
        var result = await http.GetFromJsonAsync<JsonElement>(MetaUrl(loader, game), token);
        return result.EnumerateArray().Select(item => item.GetProperty("loader").GetProperty("version").GetString()!).ToArray();
    }

    private async Task<string[]> ForgeLoadersAsync(string game, CancellationToken token)
    {
        var versions = await new ForgeVersionLoader(http).GetForgeVersions(game).WaitAsync(token);
        return versions.Select(version => version.ForgeVersionName).ToArray();
    }

    private async Task<string[]> NeoForgeLoadersAsync(string game, CancellationToken token)
    {
        var loader = new NeoForgeVersionLoader(http);
        return (await loader.GetNeoForgeVersions(game).WaitAsync(token)).Select(version => version.NeoForgeVersionName).ToArray();
    }

    public async Task PrepareAsync(Instance instance, string gamePath, IProgress<Activity> progress, CancellationToken token) =>
        _ = await PrepareLauncherAsync(instance, gamePath, progress, token);

    private async Task<(MinecraftLauncher Launcher, string Version)> PrepareLauncherAsync(
        Instance instance, string gamePath, IProgress<Activity> progress, CancellationToken token)
    {
        var launcher = new MinecraftLauncher(GamePath(gamePath));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        void ReportFiles(InstallerProgressChangedEventArgs args)
        {
            if (timer.ElapsedMilliseconds < 150 && args.ProgressedTasks != args.TotalTasks) return;
            progress.Report(new($"Minecraft: {args.EventType} {args.ProgressedTasks}/{args.TotalTasks}",
                args.TotalTasks > 0 ? args.ProgressedTasks * 100d / args.TotalTasks : null));
            timer.Restart();
        }
        launcher.FileProgressChanged += (_, args) => ReportFiles(args);
        string version = instance.GameVersion;
        if (instance.Loader is Loader.Fabric or Loader.Quilt)
        {
            var marker = Path.Combine(Path.GetDirectoryName(gamePath)!, "loader-version.json");
            version = LocalData.Read(marker, "");
            if (version.Length == 0 || !File.Exists(ProfilePath(gamePath, version)))
            {
                var url = $"{MetaUrl(instance.Loader, instance.GameVersion)}/{Uri.EscapeDataString(instance.LoaderVersion)}/profile/json";
                var profile = await http.GetFromJsonAsync<JsonElement>(url, token);
                version = LocalData.Segment(profile.GetProperty("id").GetString()!);
                LocalData.Write(ProfilePath(gamePath, version), profile);
                LocalData.Write(marker, version);
            }
        }
        else if (instance.Loader is Loader.Forge or Loader.NeoForge)
        {
            var options = new ForgeInstallOptions
            {
                CancellationToken = token,
                FileProgress = new Progress<InstallerProgressChangedEventArgs>(ReportFiles),
                InstallerOutput = new Progress<string>(line => progress.Report(new(line))),
                SkipIfAlreadyInstalled = true
            };
            var marker = Path.Combine(Path.GetDirectoryName(gamePath)!, "loader-version.json");
            version = LocalData.Read(marker, "");
            if (version.Length == 0 || !File.Exists(ProfilePath(gamePath, version)))
            {
                version = instance.Loader == Loader.Forge
                    ? await InstallForgeAsync(launcher, instance, options)
                    : await new NeoForgeInstaller(launcher, http).Install(instance.GameVersion, instance.LoaderVersion, options);
                LocalData.Write(marker, version);
            }
        }

        await launcher.InstallAsync(version, token);
        return (launcher, version);
    }

    private async Task<string> InstallForgeAsync(MinecraftLauncher launcher, Instance instance, ForgeInstallOptions options)
    {
        var token = options.CancellationToken;
        var versions = await new ForgeVersionLoader(http).GetForgeVersions(instance.GameVersion).WaitAsync(token);
        var forge = versions.FirstOrDefault(version => version.ForgeVersionName == instance.LoaderVersion)
            ?? throw new InvalidOperationException($"Forge {instance.LoaderVersion} недоступен для Minecraft {instance.GameVersion}.");
        // The mapper exposes the installers independently of the facade that opens a browser.
        var installer = new ForgeInstallerVersionMapper().CreateInstaller(forge);
        if (options.SkipIfAlreadyInstalled)
        {
            try
            {
                await launcher.GetVersionAsync(installer.VersionName, token);
                return installer.VersionName;
            }
            catch (KeyNotFoundException) { }
        }

        var vanilla = await launcher.GetVersionAsync(instance.GameVersion, token);
        await launcher.InstallAsync(vanilla, options.FileProgress, options.ByteProgress, token);
        var java = launcher.GetJavaPath(vanilla);
        if (!File.Exists(java)) java = launcher.GetDefaultJavaPath();
        if (!File.Exists(java)) throw new InvalidOperationException("Не удалось найти Java для установки Forge.");
        options.JavaPath = java;
        token.ThrowIfCancellationRequested();
        await installer.Install(launcher.MinecraftPath, launcher.GameInstaller, options);
        await launcher.GetAllVersionsAsync(token);
        return installer.VersionName;
    }

    public async Task<int> LaunchAsync(Instance instance, string gamePath, Profile profile, IProgress<Activity> progress,
        Action<string> log, CancellationToken token, Action started, string? java = null,
        string? server = null, int serverPort = 25565)
    {
        var (launcher, version) = await PrepareLauncherAsync(instance, gamePath, progress, token);
        var launchOptions = new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(profile.Nick),
            MaximumRamMb = instance.Ram
        };
        if (!string.IsNullOrWhiteSpace(java))
        {
            if (!File.Exists(java)) throw new FileNotFoundException("Java executable was not found.", java);
            launchOptions.JavaPath = java;
        }
        if (!string.IsNullOrWhiteSpace(server))
        {
            launchOptions.ServerIp = server;
            launchOptions.ServerPort = serverPort;
        }
        using var process = await launcher.BuildProcessAsync(version, launchOptions, token);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.OutputDataReceived += (_, args) => { if (args.Data is { } line) log(line); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) log(line); };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Windows не смог запустить Java.");
        started();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string ProfilePath(string game, string version) =>
        Path.Combine(game, "versions", LocalData.Segment(version), version + ".json");

    private static string MetaUrl(Loader loader, string game) =>
        (loader == Loader.Fabric ? "https://meta.fabricmc.net/v2/" : "https://meta.quiltmc.org/v3/")
        + $"versions/loader/{Uri.EscapeDataString(game)}";
}
