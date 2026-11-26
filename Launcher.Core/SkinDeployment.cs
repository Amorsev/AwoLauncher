namespace Launcher.Core;

public sealed class SkinDeployment(LocalData data, Modrinth api, ContentInstaller content)
{
    public async Task InstallAndSyncAsync(Instance instance, Profile profile, IProgress<Activity> progress, CancellationToken token)
    {
        if (instance.Loader == Loader.Vanilla)
            throw new InvalidOperationException("Для скина в игре нужен экземпляр с загрузчиком модов.");
        if (profile.SkinPath is not { } skin || !File.Exists(skin))
            throw new FileNotFoundException("Файл скина не найден.", profile.SkinPath);
        var root = data.Game(instance.Id);
        var entries = content.GetContent(instance.Id);
        var loader = entries.FirstOrDefault(item => item.Kind == "mods" &&
            ModInspection.Metadata(LocalData.SafePath(root, item.DiskPath)).Ids.Contains("customskinloader", StringComparer.OrdinalIgnoreCase));
        if (loader is { Enabled: false })
            throw new InvalidOperationException("CustomSkinLoader отключён. Включите его на вкладке «Контент».");
        if (loader is null)
        {
            var project = await api.ProjectAsync("customskinloader", token);
            if (!entries.Any(item => item.Enabled && item.File.ProjectId == project.Id))
            {
                var available = await api.VersionsAsync(project.Id, token);
                var version = available.Where(item => item.Type == "release" && Modrinth.Compatible(item, instance, "mod"))
                    .MaxBy(item => item.Published);
                if (version is null)
                    throw new InvalidOperationException("Для экземпляра нет стабильной совместимой версии CustomSkinLoader.");
                await content.InstallAsync(instance, project, version, progress, token);
            }
        }
        await WriteSkinAsync(instance, profile, skin, token);
    }

    public Task SyncIfEnabledAsync(Instance instance, Profile profile, CancellationToken token)
    {
        if (!IsApplied(instance, profile)) return Task.CompletedTask;
        if (profile.SkinPath is { } skin && File.Exists(skin)) return WriteSkinAsync(instance, profile, skin, token);
        token.ThrowIfCancellationRequested();
        var root = data.Game(instance.Id);
        LocalData.Write(Preference(root, profile.Nick), true);
        File.Delete(Target(root, profile.Nick));
        return Task.CompletedTask;
    }

    private async Task WriteSkinAsync(Instance instance, Profile profile, string skin, CancellationToken token)
    {
        var root = data.Game(instance.Id);
        var file = Target(root, profile.Nick);
        var config = LocalData.SafePath(root, "CustomSkinLoader/ExtraList/AwoLauncher.json");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            await using (var input = File.OpenRead(skin))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                await input.CopyToAsync(output, token);
            token.ThrowIfCancellationRequested();
            LocalData.Write(config, new { name = "AwoLauncher Local Skin", type = "Legacy", skin = "LocalSkin/AwoLauncher/{USERNAME}.png" });
            File.Move(temporary, file, true);
        }
        finally { File.Delete(temporary); }
    }

    public bool IsApplied(Instance instance, Profile profile)
    {
        var root = data.Game(instance.Id);
        return File.Exists(Target(root, profile.Nick)) || File.Exists(Preference(root, profile.Nick));
    }

    public void Remove(Instance instance, Profile profile)
    {
        var root = data.Game(instance.Id);
        File.Delete(Target(root, profile.Nick));
        File.Delete(Preference(root, profile.Nick));
    }

    private static string Target(string root, string nick) =>
        LocalData.SafePath(root, $"CustomSkinLoader/LocalSkin/AwoLauncher/{LocalData.Segment(nick)}.png");

    private static string Preference(string root, string nick) =>
        LocalData.SafePath(root, $"CustomSkinLoader/LocalSkin/AwoLauncher/{LocalData.Segment(nick)}.sync.json");
}
