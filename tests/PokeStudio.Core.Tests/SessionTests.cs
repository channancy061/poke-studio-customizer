using PokeStudio.Core.Backups;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Projects;
using PokeStudio.Modules.Demo;

namespace PokeStudio.Core.Tests;

public class SessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static ValueChange<int> SpeedChange(ProjectSession s, int to)
    {
        var current = s.Data.Species[0].Stats.Speed;
        return new ValueChange<int>(
            "Pokémon", $"Leafling · Speed {current} → {to}",
            v => s.Data.Species[0] = s.Data.Species[0] with { Stats = s.Data.Species[0].Stats with { Speed = v } },
            current, to, "species:1:speed", (a, b) => $"Leafling · Speed {a} → {b}");
    }

    private static async Task<(ProjectSession Session, FakeClock Clock, TempDir Dir)> OpenDemoAsync()
    {
        var tmp = new TempDir();
        var clock = new FakeClock(Start);
        var (project, _) = await Fixtures.CreateDemoProjectAsync(tmp, clock);
        var registry = new ModuleRegistry().Register(new DemoGameModule());
        var session = await ProjectSession.OpenAsync(project, registry, new ProjectService(clock), new BackupService(tmp.Combine("backups"), clock), clock);
        return (session, clock, tmp);
    }

    [Fact]
    public async Task Rapid_edits_to_the_same_field_merge_into_one_undo_step()
    {
        var (session, clock, tmp) = await OpenDemoAsync();
        using var _ = tmp;
        using var __ = session;

        session.Undo.Execute(SpeedChange(session, 50));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        session.Undo.Execute(SpeedChange(session, 70));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        session.Undo.Execute(SpeedChange(session, 90));

        var only = Assert.Single(session.Undo.History);
        Assert.Equal("Leafling · Speed 45 → 90", only.Description);
        Assert.Equal("Leafling · Speed 45 → 90", Assert.Single(session.History()).Description);

        clock.Advance(TimeSpan.FromSeconds(5));
        session.Undo.Execute(SpeedChange(session, 100)); // later drag = new step
        Assert.Equal(2, session.Undo.History.Count);

        session.Undo.Undo();
        session.Undo.Undo();
        Assert.Equal(45, session.Data.Species[0].Stats.Speed);
    }

    [Fact]
    public async Task Save_backup_edit_restore_round_trip()
    {
        var (session, clock, tmp) = await OpenDemoAsync();
        using var _ = tmp;
        using var __ = session;

        session.Undo.Execute(SpeedChange(session, 90));
        await session.SaveAsync();
        Assert.False(session.Undo.IsDirty);
        var saved = await session.BackupAsync(BackupReason.Manual, "speed 90");

        clock.Advance(TimeSpan.FromMinutes(1));
        session.Undo.Execute(SpeedChange(session, 120));
        await session.SaveAsync();

        var reloaded = 0;
        session.DataReloaded += (_, _) => reloaded++;
        await session.RestoreAsync(saved.Id);

        Assert.Equal(90, session.Data.Species[0].Stats.Speed);
        Assert.Equal(1, reloaded);
        Assert.False(session.Undo.CanUndo);
        Assert.Contains(await session.ListBackupsAsync(), b => b.Reason == BackupReason.BeforeRestore);

        // the change log keeps working after the database was swapped
        session.Undo.Execute(SpeedChange(session, 95));
        Assert.Contains(session.History(), e => e.Description.Contains("→ 95", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opening_a_project_for_a_missing_module_explains_why()
    {
        using var tmp = new TempDir();
        var (project, _) = await Fixtures.CreateDemoProjectAsync(tmp);
        var ex = await Assert.ThrowsAsync<ProjectFormatException>(() =>
            ProjectSession.OpenAsync(project, new ModuleRegistry(), new ProjectService(), new BackupService(tmp.Combine("b"))));
        Assert.Contains("isn't installed", ex.Message, StringComparison.Ordinal);
    }
}
