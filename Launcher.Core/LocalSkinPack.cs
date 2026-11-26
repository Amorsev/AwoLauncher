using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Core;

internal static class LocalSkinPack
{
    private const string FileName = "AwoLauncher-OfflineSkin.zip";
    private const string PackId = "file/" + FileName;
    private const string Marker = ".awolauncher-local-skin";
    private static readonly string[] Names = ["alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri"];

    internal static void Configure(Instance instance, string game, Guid uuid, Func<bool, byte[]?>? renderSkin)
    {
        var path = LocalData.SafePath(game, "resourcepacks/" + FileName);
        var options = LocalData.SafePath(game, "options.txt");
        var exists = File.Exists(path);
        if (!exists && renderSkin is null) return;
        if (exists)
        {
            using var previous = ZipFile.OpenRead(path);
            if (previous.GetEntry(Marker) is null)
            {
                if (renderSkin is null) return;
                throw new InvalidDataException($"The resource pack {FileName} already exists and belongs to the instance.");
            }
        }

        // Remove the previous profile's pack before preparing the new one.
        if (exists)
        {
            SetEnabled(options, false);
            File.Delete(path);
        }
        if (renderSkin is null) return;

        var version = LocalData.Segment(instance.GameVersion);
        if (!Version.TryParse(version, out var gameVersion) || gameVersion < new Version(1, 20, 2))
            throw new InvalidDataException("Local resource-pack skins require Minecraft Java 1.20.2 or later.");

        // Match java.util.UUID.hashCode and DefaultPlayerSkin's ordered entries.
        Span<byte> bytes = stackalloc byte[16];
        uuid.TryWriteBytes(bytes, bigEndian: true, out _);
        var hash = BinaryPrimitives.ReadInt32BigEndian(bytes) ^ BinaryPrimitives.ReadInt32BigEndian(bytes[4..]) ^
            BinaryPrimitives.ReadInt32BigEndian(bytes[8..]) ^ BinaryPrimitives.ReadInt32BigEndian(bytes[12..]);
        var index = ((hash % 18) + 18) % 18;
        var slim = index < Names.Length;
        var texture = $"assets/minecraft/textures/entity/player/{(slim ? "slim" : "wide")}/{Names[index % Names.Length]}.png";
        var jar = LocalData.SafePath(game, $"versions/{version}/{version}.jar");

        using var client = ZipFile.OpenRead(jar);
        if (client.GetEntry(texture) is null || client.GetEntry("version.json") is not { } versionEntry)
            throw new InvalidDataException($"Minecraft {version} does not use the supported default skin textures.");
        using var versionStream = versionEntry.Open();
        using var metadata = JsonDocument.Parse(versionStream);
        var formats = metadata.RootElement.GetProperty("pack_version");
        var major = formats.TryGetProperty("resource_major", out var format)
            ? format.GetInt32() : formats.GetProperty("resource").GetInt32();
        var dataVersion = metadata.RootElement.GetProperty("world_version").GetInt32();
        var png = renderSkin(slim) ?? throw new InvalidDataException("The selected skin PNG could not be read.");
        WritePack(path, texture, png, major);
        try { SetEnabled(options, true, dataVersion); }
        catch { File.Delete(path); throw; }
    }

    private static void WritePack(string path, string texture, byte[] png, int format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                const string description = "AwoLauncher • local offline skin";
                object pack = format >= 65
                    ? new { description, min_format = format, max_format = format }
                    : new { description, pack_format = format };
                WriteEntry(zip, "pack.mcmeta", JsonSerializer.SerializeToUtf8Bytes(new { pack }));
                WriteEntry(zip, texture, png);
                zip.CreateEntry(Marker);
            }
            File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    private static void SetEnabled(string path, bool enable, int dataVersion = 0)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        if (lines.Count == 0 && enable) lines.Add($"version:{dataVersion}");
        ChangePacks(lines, "resourcePacks:", enable);
        ChangePacks(lines, "incompatibleResourcePacks:", false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    private static void ChangePacks(List<string> lines, string prefix, bool enable)
    {
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.Ordinal));
        var packs = index < 0 ? [] : JsonSerializer.Deserialize<List<string>>(lines[index][prefix.Length..]) ?? [];
        var removed = packs.RemoveAll(pack => pack == PackId);
        if (enable) packs.Add(PackId);
        if (!enable && removed == 0) return;
        var value = prefix + JsonSerializer.Serialize(packs);
        if (index < 0) lines.Add(value);
        else lines[index] = value;
    }
}
