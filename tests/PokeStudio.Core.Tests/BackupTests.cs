using PokeStudio.Core.Backups;
using PokeStudio.Core.Projects;

namespace PokeStudio.Core.Tests;

public class BackupTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_copies_every_file_with_checksums()
    {
        using var tmp = new TempDir();
        var (project, _) = await Fixtures.CreateDemoProjectAsync(tmp);
        var service = new BackupService(tmp.Combine("backups"));

        var backup = await service.CreateAsync(project, BackupReason.Manual, "Before I touch the gyms");

        Assert.Contains(backup.Files, f => f.RelativePath == "project.json");
        Assert.Contains(backup.Files, f => f.RelativePath == "source/Demo Valley.demogame.json");
        Assert.All(backup.Files, f => Assert.Equal(64, f.Sha256.Length));
        await service.VerifyAsync(project.Manifest.Id, backup.Id);
    }

    [Fact]
    public async Task List_is_newest_first_and_prune_keeps_pinned()
    {
        using var tmp = new TempDir();
        var clock = new FakeClock(Start);
        var (project, _) = await Fixtures.CreateDemoProjectAsync(tmp, clock);
        var service = new BackupService(tmp.Combine("backups"), clock);
        var id = project.Manifest.Id;

        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await service.CreateAsync(project, BackupReason.Auto)).Id);
            clock.Advance(TimeSpan.FromMinutes(10));
        }

        await service.SetPinnedAsync(id, ids[0], pinned: true); // oldest, pinned

        var listed = await service.ListAsync(id);
        Assert.Equal(ids.AsEnumerable().Reverse(), listed.Select(b => b.Id));

        var removed = await service.PruneAsync(id, keep: 2);
        Assert.Equal(2, removed);
        var left = (await service.ListAsync(id)).Select(b => b.Id).ToList();
        Assert.Equal([ids[4], ids[3], ids[0]], left);
    }

    [Fact]
    public async Task Restore_brings_back_old_files_and_backs_up_current_state_first()
    {
        using var tmp = new TempDir();
        var clock = new FakeClock(Start);
        var (project, _) = await Fixtures.CreateDemoProjectAsync(tmp, clock);
        var service = new BackupService(tmp.Combine("backups"), clock);

        var edits = Path.Combine(project.DataDir, "edits.json");
        await File.WriteAllTextAsync(edits, "version one");
        var good = await service.CreateAsync(project, BackupReason.Manual);
        clock.Advance(TimeSpan.FromMinutes(1));

        await File.WriteAllTextAsync(edits, "version two");
        await File.WriteAllTextAsync(Path.Combine(project.DataDir, "new.json"), "added later");

        var safety = await service.RestoreAsync(project, good.Id);

        Assert.Equal("version one", await File.ReadAllTextAsync(edits));
        Assert.False(File.Exists(Path.Combine(project.DataDir, "new.json")));
        Assert.Equal(BackupReason.BeforeRestore, safety.Reason);
        Assert.Contains(safety.Files, f => f.RelativePath == "data/new.json");
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(project.Directory)!, ".*"));
        await new ProjectService().OpenAsync(project.Directory); // still a valid project
    }

    [Fact]
    public async Task Damaged_backup_is_rejected_before_anything_changes()
    {
        using var tmp = new TempDir();
        var (project, _) = await Fixtures.CreateDemoProjectAsync(tmp);
        var service = new BackupService(tmp.Combine("backups"));
        var backup = await service.CreateAsync(project, BackupReason.Manual);

        var copied = Path.Combine(service.ProjectBackupsDir(project.Manifest.Id), backup.Id, BackupManifest.FilesFolder, "project.json");
        await File.AppendAllTextAsync(copied, " tampered");
        var before = await File.ReadAllTextAsync(project.ManifestPath);

        await Assert.ThrowsAsync<BackupCorruptException>(() => service.RestoreAsync(project, backup.Id));
        Assert.Equal(before, await File.ReadAllTextAsync(project.ManifestPath));
        Assert.Single(await service.ListAsync(project.Manifest.Id)); // no safety backup was made either
    }
}
