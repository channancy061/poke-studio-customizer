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
        data.Story = await LoadJsonAsync<Story.StorySettings>(project, StoryFileName, cancellationToken);
        data.Branding = await LoadOptionalJsonAsync<Branding.BrandingSettings>(project, BrandingFileName, cancellationToken);
        var session = new ProjectSession(project, module, data, projects, backups, clock ?? TimeProvider.System);
        session.RecordBaseline();
        var all = await backups.ListAsync(project.Manifest.Id, cancellationToken);
        var latest = all.Count > 0 ? all[0] : null;
        session.LastBackupUtc = latest?.CreatedUtc;
        return session;
    }

    public const string LabFileName = "champion-lab.json";
    public const string GameOptionsFileName = "game-options.json";
    public const string StoryFileName = "story-battles.json";
    public const string BrandingFileName = "branding.json";

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

    /// <summary>Settings kept in the project's data folder, or null when there are none yet.</summary>
    private static async Task<T?> LoadOptionalJsonAsync<T>(Project project, string fileName, CancellationToken cancellationToken)
        where T : class, new() =>
        File.Exists(Path.Combine(project.DataDir, fileName)) ? await LoadJsonAsync<T>(project, fileName, cancellationToken) : null;

    public IReadOnlyList<ChangeLogEntry> History(int limit = 500) => _log.ReadRecent(limit);

    private bool _backedUpThisSession;

    /// <summary>For in-place (decomp) projects: the files the module edits, so backups include them.</summary>
    public ExternalFiles? External => Project.Manifest.WorkInPlace
        ? new ExternalFiles(Project.WorkingSourcePath, Module.ManagedFiles(Project.WorkingSourcePath, Data))
        : null;

    // issues the game already had when the project was opened (its own data, not the user's changes)
    private HashSet<string> _baseline = new(StringComparer.Ordinal);

    private static string Signature(Validation.ValidationIssue i) => $"{i.Severity}|{i.Area}|{i.Subject}|{i.Message}";

    private void RecordBaseline() => _baseline = Validation.GameValidator.Run(Data, Module).Select(Signature).ToHashSet(StringComparer.Ordinal);

    /// <summary>How many issues of the original game the last check left out.</summary>
    public int HiddenOriginalIssues { get; private set; }

    /// <summary>
    /// Runs every check (general + module-specific) on the current data. Issues the game already had when the project
    /// was opened are left out (counted in <see cref="HiddenOriginalIssues"/>): only what the user's changes caused is shown.
    /// </summary>
    public IReadOnlyList<Validation.ValidationIssue> Validate()
    {
        var all = Validation.GameValidator.Run(Data, Module);
        var shown = all.Where(i => !_baseline.Contains(Signature(i))).ToList();
        HiddenOriginalIssues = all.Count - shown.Count;
        return shown;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (Project.Manifest.WorkInPlace && !_backedUpThisSession)
        {
            // Never write into the user's decomp files without a copy of how they were.
            await BackupAsync(BackupReason.Auto, "Before the first save of this session", cancellationToken: cancellationToken);
            _backedUpThisSession = true;
        }

        await Module.SaveDataAsync(Data, Project.CreateSaveContext(Module, Data), cancellationToken);
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

        if (Data.Branding is { } branding)
        {
            Directory.CreateDirectory(Project.DataDir);
            await Storage.JsonStore.WriteAsync(Path.Combine(Project.DataDir, BrandingFileName), branding, cancellationToken);
        }

        if (Data.Story is { } story)
        {
            Directory.CreateDirectory(Project.DataDir);
            await Storage.JsonStore.WriteAsync(Path.Combine(Project.DataDir, StoryFileName), story, cancellationToken);
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
        Data.Lab = await LoadJsonAsync<ChampionLab.LabSettings>(Project, LabFileName, cancellationToken);
        Data.GameOptions = await LoadJsonAsync<GameOptions.GameOptionsSettings>(Project, GameOptionsFileName, cancellationToken);
        Data.Story = await LoadJsonAsync<Story.StorySettings>(Project, StoryFileName, cancellationToken);
        Data.Branding = await LoadOptionalJsonAsync<Branding.BrandingSettings>(Project, BrandingFileName, cancellationToken);
        RecordBaseline();
        Undo.Clear();
        LastBackupUtc = _clock.GetUtcNow();
        DataReloaded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _log.Dispose();
}
