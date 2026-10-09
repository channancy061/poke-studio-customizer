using PokeStudio.Core.Backups;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Projects;

/// <summary>
/// An open project: its module, loaded data, undo stack and change log. UI-independent so the
/// whole edit → save → backup → restore loop is unit-testable.
/// </summary>
public sealed class ProjectSession : IDisposable
{
    private readonly ProjectService _projects;
    private readonly BackupService _backups;
    private readonly TimeProvider _clock;
    private ChangeLog _log;

    private ProjectSession(Project project, IGameModule module, GameData data, ProjectService projects, BackupService backups, TimeProvider clock)
    {
        Project = project;
        Module = module;
        Data = data;
        _projects = projects;
        _backups = backups;
        _clock = clock;
        Undo = new UndoRedoStack(clock);
        _log = new ChangeLog(project.HistoryPath, clock);
        Undo.Changed += (_, e) => _log.Record(e.Change, e.Action); // _log is swapped on restore
    }

    public Project Project { get; private set; }
    public IGameModule Module { get; }
    public GameData Data { get; private set; }
    public UndoRedoStack Undo { get; }
    public DateTimeOffset? LastBackupUtc { get; private set; }

    /// <summary>Raised after data is replaced (e.g. by a restore) so views can reload.</summary>
    public event EventHandler? DataReloaded;

    public static async Task<ProjectSession> OpenAsync(
        Project project,
        ModuleRegistry registry,
        ProjectService projects,
        BackupService backups,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        var module = registry.Find(project.Manifest.ModuleId)
            ?? throw new ProjectFormatException($"This project needs the \"{project.Manifest.ModuleId}\" module, which isn't installed.");
        var data = await module.LoadDataAsync(project.WorkingSourcePath, project.DataDir, cancellationToken);
        var session = new ProjectSession(project, module, data, projects, backups, clock ?? TimeProvider.System);
        var all = await backups.ListAsync(project.Manifest.Id, cancellationToken);
        var latest = all.Count > 0 ? all[0] : null;
        session.LastBackupUtc = latest?.CreatedUtc;
        return session;
    }

    public IReadOnlyList<ChangeLogEntry> History(int limit = 500) => _log.ReadRecent(limit);

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await Module.SaveDataAsync(Data, Project.CreateSaveContext(), cancellationToken);
        await _projects.SaveManifestAsync(Project, cancellationToken);
        Undo.MarkSaved();
        _log.MarkSaved();
    }

    public async Task<BackupManifest> BackupAsync(BackupReason reason, string? label = null, int? keep = null, CancellationToken cancellationToken = default)
    {
        var manifest = await _backups.CreateAsync(Project, reason, label, cancellationToken);
        LastBackupUtc = manifest.CreatedUtc;
        if (keep is int k)
        {
            await _backups.PruneAsync(Project.Manifest.Id, k, cancellationToken);
        }

        return manifest;
    }

    public Task<IReadOnlyList<BackupManifest>> ListBackupsAsync(CancellationToken cancellationToken = default) =>
        _backups.ListAsync(Project.Manifest.Id, cancellationToken);

    public Task SetBackupPinnedAsync(string backupId, bool pinned, CancellationToken cancellationToken = default) =>
        _backups.SetPinnedAsync(Project.Manifest.Id, backupId, pinned, cancellationToken);

    /// <summary>Restores a backup (a safety backup is made first), then reloads data. Undo history resets.</summary>
    public async Task RestoreAsync(string backupId, CancellationToken cancellationToken = default)
    {
        _log.Dispose(); // the database file must be closed while the folder is swapped
        try
        {
            await _backups.RestoreAsync(Project, backupId, cancellationToken);
        }
        finally
        {
            Project = await _projects.OpenAsync(Project.Directory, cancellationToken);
            _log = new ChangeLog(Project.HistoryPath, _clock);
        }

        Data = await Module.LoadDataAsync(Project.WorkingSourcePath, Project.DataDir, cancellationToken);
        Undo.Clear();
        LastBackupUtc = _clock.GetUtcNow();
        DataReloaded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _log.Dispose();
}
