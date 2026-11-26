using System.Diagnostics;
using System.IO.Pipes;
using System.Buffers.Binary;
using System.Text.Json;

namespace Launcher.Core;

public sealed record JavaInstallation(string Path, string Label);

public sealed class JavaDetector
{
    public Task<JavaInstallation[]> ScanAsync(string game, CancellationToken token, string? sharedRuntime = null) => Task.Run(() =>
    {
        var roots = new[] { Environment.GetEnvironmentVariable("JAVA_HOME"), sharedRuntime, Path.Combine(game, "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft") };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (Path.IsPathFullyQualified(folder.Trim('"'))) paths.Add(Path.Combine(folder.Trim('"'), "java.exe"));
        foreach (var root in roots.Where(Directory.Exists))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6,
                IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var java in Directory.EnumerateFiles(root!, "java.exe", options))
            {
                token.ThrowIfCancellationRequested();
                paths.Add(java);
            }
        }
        return paths.Where(File.Exists).Order().Select(path =>
        {
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return new JavaInstallation(path, string.IsNullOrEmpty(version) ? path : $"{version} · {path}");
        }).ToArray();
    }, token);

    public static int? SuggestedMajor(string game)
    {
        if (!Version.TryParse(game, out var version)) return null;
        if (version.Major >= 26) return 25;
        if (version.Minor >= 21 || version is { Minor: 20, Build: >= 5 }) return 21;
        if (version.Minor >= 18) return 17;
        return version.Minor == 17 ? 16 : 8;
    }
}

public sealed record CrashReport(string ReasonKey, string Path, string Excerpt);

public sealed class CrashAnalyzer
{
    public async Task<CrashReport> AnalyzeAsync(string game, DateTimeOffset started, string launcherLog, CancellationToken token)
    {
        var crash = InstanceService.Files(Path.Combine(game, "crash-reports"))
            .Where(file => file.Extension == ".txt" && file.LastWriteTimeUtc >= started.UtcDateTime.AddSeconds(-2))
            .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        var latest = Path.Combine(game, "logs", "latest.log");
        var path = crash?.FullName ?? (File.Exists(latest) && File.GetLastWriteTimeUtc(latest) >= started.UtcDateTime.AddSeconds(-2)
            ? latest : launcherLog);
        var excerpt = "";
        if (File.Exists(path))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, true);
            if (crash is null) stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = new char[65536];
            var count = await reader.ReadBlockAsync(text.AsMemory(), token);
            excerpt = new string(text, 0, count);
        }
        string Reason(params (string Pattern, string Key)[] patterns) =>
            patterns.FirstOrDefault(item => excerpt.Contains(item.Pattern, StringComparison.OrdinalIgnoreCase)).Key ?? "CrashUnknown";
        return new(Reason(("OutOfMemoryError", "CrashMemory"), ("UnsupportedClassVersionError", "CrashJava"),
            ("Incompatible mods", "CrashMods"), ("Mod resolution", "CrashMods"), ("GLFW error", "CrashGraphics"),
            ("Mixin apply failed", "CrashMods")), path, excerpt);
    }
}

public sealed class DiscordRpcService : IAsyncDisposable
{
    private readonly string pipePrefix;
    private NamedPipeClientStream? pipe;
    public DiscordRpcService() : this("discord-ipc") { }
    internal DiscordRpcService(string pipePrefix) => this.pipePrefix = pipePrefix;

    public async Task SetAsync(string applicationId, Instance instance, CancellationToken token)
    {
        if (!ulong.TryParse(applicationId, out _)) return;
        for (int index = 0; index < 10; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = new NamedPipeClientStream(".", $"{pipePrefix}-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await candidate.ConnectAsync(80, token); }
            catch (TimeoutException) { await candidate.DisposeAsync(); continue; }
            catch { await candidate.DisposeAsync(); throw; }
            pipe = candidate;
            await SendAsync(0, new { v = 1, client_id = applicationId }, token);
            var ready = await ReadFrameAsync(token);
            if (!ready.TryGetProperty("evt", out var readyEvent) || readyEvent.GetString() != "READY")
                throw new IOException("Discord rejected the RPC handshake.");
            await ActivityAsync(new
            {
                details = $"Minecraft {instance.GameVersion} · {instance.Loader}",
                state = instance.Name,
                timestamps = new { start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            }, token);
            return;
        }
    }

    private async Task ActivityAsync(object? activity, CancellationToken token)
    {
        var nonce = Guid.NewGuid().ToString("N");
        await SendAsync(1, new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity }, nonce }, token);
        while (true)
        {
            var result = await ReadFrameAsync(token);
            if (!result.TryGetProperty("nonce", out var received) || received.ValueKind != JsonValueKind.String ||
                received.GetString() != nonce) continue;
            if (result.TryGetProperty("evt", out var error) && error.GetString() == "ERROR")
                throw new IOException("Discord declined the activity.");
            return;
        }
    }

    private async Task<JsonElement> ReadFrameAsync(CancellationToken token)
    {
        var connection = pipe ?? throw new IOException("Discord IPC is closed.");
        var header = new byte[8];
        await connection.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadInt32LittleEndian(header) != 1 || length is < 1 or > 65536)
            throw new IOException("Unexpected Discord frame.");
        var response = new byte[length];
        await connection.ReadExactlyAsync(response, token);
        using var json = JsonDocument.Parse(response);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Invalid Discord response.");
        return json.RootElement.Clone();
    }

    private async Task SendAsync(int opcode, object value, CancellationToken token)
    {
        if (pipe is null) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);
        await pipe.WriteAsync(header, token);
        await pipe.WriteAsync(payload, token);
    }

    public async ValueTask DisposeAsync()
    {
        if (pipe is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            if (pipe.IsConnected) await ActivityAsync(null, timeout.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { Trace.TraceWarning(e.Message); }
        finally { await pipe.DisposeAsync(); pipe = null; }
    }
}
