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
        data.Lab = await LoadJsonAsync<ChampionLab.LabSettings>(project, LabFileName, cancellationToken);
        data.GameOptions = await LoadJsonAsync<GameOptions.GameOptionsSettings>(project, GameOptionsFileName, cancellationToken);
        var session = new ProjectSession(project, module, data, projects, backups, clock ?? TimeProvider.System);
        var all = await backups.ListAsync(project.Manifest.Id, cancellationToken);
        var latest = all.Count > 0 ? all[0] : null;
        session.LastBackupUtc = latest?.CreatedUtc;
        return session;
    }

    public const string LabFileName = "champion-lab.json";
    public const string GameOptionsFileName = "game-options.json";

    /// <summary>Settings kept in the project's data folder, or the defaults.</summary>
    private static async Task<T> LoadJsonAsync<T>(Project project, string fileName, CancellationToken cancellationToken)
        where T : class, new()
    {
        var path = Path.Combine(project.DataDir, fileName);
        if (!File.Exists(path))
        {
            return new T();
        }

        try
        {
            return await Storage.JsonStore.ReadAsync<T>(path, cancellationToken) ?? new T();
        }
        catch (System.Text.Json.JsonException)
        {
            return new T(); // a damaged file never stops the project from opening
        }
    }

    public IReadOnlyList<ChangeLogEntry> History(int limit = 500) => _log.ReadRecent(limit);

    private bool _backedUpThisSession;

    /// <summary>For in-place (decomp) projects: the files the module edits, so backups include them.</summary>
    public ExternalFiles? External => Project.Manifest.WorkInPlace
        ? new ExternalFiles(Project.WorkingSourcePath, Module.ManagedFiles(Project.WorkingSourcePath))
        : null;

    /// <summary>Runs every check (general + module-specific) on the current data.</summary>
    public IReadOnlyList<Validation.ValidationIssue> Validate() => Validation.GameValidator.Run(Data, Module);

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (Project.Manifest.WorkInPlace && !_backedUpThisSession)
        {
            // Never write into the user's decomp files without a copy of how they were.
            await BackupAsync(BackupReason.Auto, "Before the first save of this session", cancellationToken: cancellationToken);
            _backedUpThisSession = true;
        }

        await Module.SaveDataAsync(Data, Project.CreateSaveContext(Module), cancellationToken);
        if (Data.Lab is { } lab)
        {
            Directory.CreateDirectory(Project.DataDir);
            await Storage.JsonStore.WriteAsync(Path.Combine(Project.DataDir, LabFileName), lab, cancellationToken);
        }

        if (Data.GameOptions is { } options)
        {
            Directory.CreateDirectory(Project.DataDir);
            await Storage.JsonStore.WriteAsync(Path.Combine(Project.DataDir, GameOptionsFileName), options, cancellationToken);
        }

        await _projects.SaveManifestAsync(Project, cancellationToken);
        Undo.MarkSaved();
        _log.MarkSaved();
    }

    public async Task<BackupManifest> BackupAsync(BackupReason reason, string? label = null, int? keep = null, CancellationToken cancellationToken = default)
    {
        var manifest = await _backups.CreateAsync(Project, reason, label, External, cancellationToken);
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
            await _backups.RestoreAsync(Project, backupId, External, cancellationToken);
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
