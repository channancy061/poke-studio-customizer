using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Platform;
using PokeStudio.Core.Safety;
using PokeStudio.Core.Storage;

namespace PokeStudio.Core.Projects;

/// <summary>project.json — human-readable, schema-versioned.</summary>
public sealed record ProjectManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ModuleId { get; init; }
    public GamePlatform Platform { get; init; }
    public GameSourceKind SourceKind { get; init; }
    public string GameTitle { get; init; } = "";

    /// <summary>Where the user's original lives. Never written to.</summary>
    public required string OriginalSourcePath { get; init; }

    /// <summary>SHA-256 of the original file at import time (null for folders).</summary>
    public string? OriginalSha256 { get; init; }

    /// <summary>Name of the working copy inside the project's "source" folder.</summary>
    public required string WorkingSourceName { get; init; }

    /// <summary>
    /// True for decomp projects: the app edits the user's project folder directly (only files the
    /// module declares, each backed up first) instead of a copy.
    /// </summary>
    public bool WorkInPlace { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
}

public sealed class ProjectFormatException(string message) : Exception(message);

/// <summary>An opened project folder:
/// <code>
/// project.json   manifest
/// source/        working copy of the user's game files (originals are never touched)
/// data/          module edit data
/// history.db     change log
/// </code></summary>
public sealed class Project
{
    public const string ManifestFileName = "project.json";
    public const string SourceFolderName = "source";
    public const string DataFolderName = "data";
    public const string HistoryFileName = "history.db";

    internal Project(string directory, ProjectManifest manifest)
    {
        Directory = Path.GetFullPath(directory);
        Manifest = manifest;
    }

    public string Directory { get; }
    public ProjectManifest Manifest { get; internal set; }

    public string ManifestPath => Path.Combine(Directory, ManifestFileName);
    public string SourceDir => Path.Combine(Directory, SourceFolderName);
    public string DataDir => Path.Combine(Directory, DataFolderName);
    public string HistoryPath => Path.Combine(Directory, HistoryFileName);
    public string WorkingSourcePath => Manifest.WorkInPlace ? Manifest.OriginalSourcePath : Path.Combine(SourceDir, Manifest.WorkingSourceName);

    public SafeFileWriter CreateWriter() => new(Directory, [Manifest.OriginalSourcePath]);

    /// <summary>Where a module may write when saving. In-place projects may only touch the module's managed files.</summary>
    public ModuleSaveContext CreateSaveContext(IGameModule? module = null, GameData? data = null) =>
        Manifest.WorkInPlace && module is not null
            ? new(new SafeFileWriter(WorkingSourcePath, allowedFiles: data is null ? module.ManagedFiles(WorkingSourcePath) : module.ManagedFiles(WorkingSourcePath, data)), DataDir, WorkingSourcePath)
            : new(CreateWriter(), DataDir, WorkingSourcePath);
}

public sealed class ProjectService(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Creates a project by copying the user's game into a new project folder.</summary>
    public async Task<Project> CreateAsync(
        string name,
        GameSource source,
        IGameModule module,
        DetectionResult detection,
        string projectsRoot,
        CancellationToken cancellationToken = default)
    {
        var sourceFull = Path.GetFullPath(source.Path);
        var isFile = File.Exists(sourceFull);
        if (!isFile && !System.IO.Directory.Exists(sourceFull))
        {
            throw new FileNotFoundException("The game file or folder could not be found.", sourceFull);
        }

        var dir = UniqueFolder(Path.Combine(projectsRoot, PathRules.SafeFolderName(name)));
        if (PathRules.IsInside(sourceFull, dir))
        {
            throw new UnsafeWriteException("A project can't be created inside the game folder it copies.");
        }

        System.IO.Directory.CreateDirectory(Path.Combine(dir, Project.SourceFolderName));
        System.IO.Directory.CreateDirectory(Path.Combine(dir, Project.DataFolderName));

        var workingName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceFull));
        // in place: decomp folders, and game files a module only reads (3DS games write a mod beside them, never into them)
        var inPlace = module.WorksInPlace;
        if (!inPlace)
        {
            var workingPath = Path.Combine(dir, Project.SourceFolderName, workingName);
            if (isFile)
            {
                File.Copy(sourceFull, workingPath);
            }
            else
            {
                CopyDirectory(sourceFull, workingPath);
            }
        }

        var now = _clock.GetUtcNow();
        var manifest = new ProjectManifest
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            ModuleId = module.Id,
            Platform = module.Platform,
            SourceKind = source.Kind,
            GameTitle = detection.GameTitle,
            OriginalSourcePath = sourceFull,
            OriginalSha256 = isFile ? await Hashing.Sha256FileAsync(sourceFull, cancellationToken) : null,
            WorkingSourceName = workingName,
            WorkInPlace = inPlace,
            CreatedUtc = now,
            ModifiedUtc = now,
        };

        var project = new Project(dir, manifest);
        await JsonStore.WriteAsync(project.ManifestPath, manifest, cancellationToken);
        return project;
    }

    public async Task<Project> OpenAsync(string projectDir, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(projectDir, Project.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new ProjectFormatException("This folder isn't a project (project.json is missing).");
        }

        ProjectManifest? manifest;
        try
        {
            manifest = await JsonStore.ReadAsync<ProjectManifest>(manifestPath, cancellationToken);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ProjectFormatException($"project.json is damaged: {ex.Message}");
        }

        if (manifest is null || manifest.SchemaVersion < 1)
        {
            throw new ProjectFormatException("project.json is empty or invalid.");
        }

        if (manifest.SchemaVersion > ProjectManifest.CurrentSchemaVersion)
        {
            throw new ProjectFormatException("This project was made with a newer version of the app. Please update to open it.");
        }

        return new Project(projectDir, manifest);
    }

    public async Task SaveManifestAsync(Project project, CancellationToken cancellationToken = default)
    {
        project.Manifest = project.Manifest with { ModifiedUtc = _clock.GetUtcNow() };
        await JsonStore.WriteAsync(project.ManifestPath, project.Manifest, cancellationToken);
    }

    private static string UniqueFolder(string desired)
    {
        var candidate = desired;
        for (var i = 2; System.IO.Directory.Exists(candidate) || File.Exists(candidate); i++)
        {
            candidate = $"{desired} ({i})";
        }

        return candidate;
    }

    public static void CopyDirectory(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);
        foreach (var dir in System.IO.Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            System.IO.Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }
}
