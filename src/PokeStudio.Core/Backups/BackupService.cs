using System.Globalization;
using PokeStudio.Core.Platform;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Storage;

namespace PokeStudio.Core.Backups;

public enum BackupReason
{
    Manual,
    Auto,
    BeforeBuild,
    BeforeRestore,
    BeforeRandomize,
}

public sealed record BackupFile(string RelativePath, long Size, string Sha256);

/// <summary>Files outside the project folder to include (the decomp files an in-place module edits).</summary>
public sealed record ExternalFiles(string Root, IReadOnlyList<string> RelativePaths);

/// <summary>backup.json, stored next to the copied files so a backup is understandable without the app.</summary>
public sealed record BackupManifest
{
    public const string FileName = "backup.json";
    public const string FilesFolder = "files";

    /// <summary>Prefix for external (decomp) files inside a backup: "@source/src/data/…".</summary>
    public const string ExternalPrefix = "@source/";

    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public BackupReason Reason { get; init; }
    public string? Label { get; init; }
    public bool Pinned { get; init; }
    public IReadOnlyList<BackupFile> Files { get; init; } = [];
    public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed class BackupCorruptException(string message) : Exception(message);

/// <summary>
/// Timestamped snapshots of a project folder in the app's backups directory.
/// Restoring always backs up the current state first and verifies checksums before touching anything.
/// </summary>
public sealed class BackupService(string backupsRoot, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string ProjectBackupsDir(string projectId) => Path.Combine(backupsRoot, projectId);

    public async Task<BackupManifest> CreateAsync(Project project, BackupReason reason, string? label = null, ExternalFiles? external = null, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var id = now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + reason.ToString().ToLowerInvariant();
        var dir = Path.Combine(ProjectBackupsDir(project.Manifest.Id), id);
        for (var i = 2; Directory.Exists(dir); i++)
        {
            dir = Path.Combine(ProjectBackupsDir(project.Manifest.Id), $"{id}-{i}");
        }

        var filesDir = Path.Combine(dir, BackupManifest.FilesFolder);
        Directory.CreateDirectory(filesDir);

        var files = new List<BackupFile>();
        foreach (var file in Directory.EnumerateFiles(project.Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTransient(file))
            {
                continue;
            }

            var rel = PathRules.ToPortableRelative(project.Directory, file);
            var target = PathRules.FromPortableRelative(filesDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            files.Add(new BackupFile(rel, new FileInfo(target).Length, await Hashing.Sha256FileAsync(target, cancellationToken)));
        }

        foreach (var rel in external?.RelativePaths ?? [])
        {
            var source = PathRules.FromPortableRelative(external!.Root, rel);
            if (!File.Exists(source))
            {
                continue;
            }

            var key = BackupManifest.ExternalPrefix + rel;
            var target = PathRules.FromPortableRelative(filesDir, key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            files.Add(new BackupFile(key, new FileInfo(target).Length, await Hashing.Sha256FileAsync(target, cancellationToken)));
        }

        var manifest = new BackupManifest
        {
            Id = Path.GetFileName(dir),
            ProjectId = project.Manifest.Id,
            ProjectName = project.Manifest.Name,
            CreatedUtc = now,
            Reason = reason,
            Label = label,
            Files = files,
        };
        await JsonStore.WriteAsync(Path.Combine(dir, BackupManifest.FileName), manifest, cancellationToken);
        return manifest;
    }

    /// <summary>Newest first. Folders with a missing or unreadable manifest are skipped.</summary>
    public async Task<IReadOnlyList<BackupManifest>> ListAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var root = ProjectBackupsDir(projectId);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var list = new List<BackupManifest>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(dir, BackupManifest.FileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var m = await JsonStore.ReadAsync<BackupManifest>(path, cancellationToken);
                if (m is not null)
                {
                    list.Add(m);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // unreadable manifest: leave the folder alone, just don't list it
            }
        }

        return list.OrderByDescending(m => m.CreatedUtc).ToList();
    }

    public async Task SetPinnedAsync(string projectId, string backupId, bool pinned, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(ProjectBackupsDir(projectId), backupId, BackupManifest.FileName);
        var m = await JsonStore.ReadAsync<BackupManifest>(path, cancellationToken)
            ?? throw new BackupCorruptException("The backup's description file is empty.");
        await JsonStore.WriteAsync(path, m with { Pinned = pinned }, cancellationToken);
    }

    /// <summary>Keeps the newest <paramref name="keep"/> unpinned backups; pinned ones are always kept.</summary>
    public async Task<int> PruneAsync(string projectId, int keep, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        var all = await ListAsync(projectId, cancellationToken);
        var toDelete = all.Where(m => !m.Pinned).Skip(keep).ToList();
        foreach (var m in toDelete)
        {
            Directory.Delete(Path.Combine(ProjectBackupsDir(projectId), m.Id), recursive: true);
        }

        return toDelete.Count;
    }

    /// <summary>Checks every file against its recorded checksum. Throws <see cref="BackupCorruptException"/>.</summary>
    public async Task VerifyAsync(string projectId, string backupId, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(ProjectBackupsDir(projectId), backupId);
        var manifest = await JsonStore.ReadAsync<BackupManifest>(Path.Combine(dir, BackupManifest.FileName), cancellationToken)
            ?? throw new BackupCorruptException("The backup's description file is empty.");
        var filesDir = Path.Combine(dir, BackupManifest.FilesFolder);
        foreach (var f in manifest.Files)
        {
            var path = PathRules.FromPortableRelative(filesDir, f.RelativePath);
            if (!PathRules.IsInside(filesDir, path))
            {
                throw new BackupCorruptException($"The backup lists a file outside its folder: {f.RelativePath}");
            }

            if (!File.Exists(path))
            {
                throw new BackupCorruptException($"A file is missing from this backup: {f.RelativePath}");
            }

            if (await Hashing.Sha256FileAsync(path, cancellationToken) != f.Sha256)
            {
                throw new BackupCorruptException($"A file in this backup is damaged: {f.RelativePath}");
            }
        }
    }

    /// <summary>
    /// Replaces the project's contents with a backup. Order: verify → back up current state →
    /// copy into a staging folder → swap. The caller must close open files (e.g. the change log) first.
    /// Returns the safety backup made of the state before restoring.
    /// </summary>
    public async Task<BackupManifest> RestoreAsync(Project project, string backupId, ExternalFiles? external = null, CancellationToken cancellationToken = default)
    {
        var projectId = project.Manifest.Id;
        await VerifyAsync(projectId, backupId, cancellationToken);
        var safety = await CreateAsync(project, BackupReason.BeforeRestore, $"Before restoring {backupId}", external, cancellationToken);

        var backupDir = Path.Combine(ProjectBackupsDir(projectId), backupId);
        var filesDir = Path.Combine(backupDir, BackupManifest.FilesFolder);
        var manifest = await JsonStore.ReadAsync<BackupManifest>(Path.Combine(backupDir, BackupManifest.FileName), cancellationToken);
        var parent = Path.GetDirectoryName(project.Directory)!;
        var name = Path.GetFileName(project.Directory);
        var staging = Path.Combine(parent, $".{name}.restoring-{Guid.NewGuid():N}");
        var aside = Path.Combine(parent, $".{name}.old-{Guid.NewGuid():N}");

        ProjectService.CopyDirectory(filesDir, staging);
        var stagedExternal = Path.Combine(staging, BackupManifest.ExternalPrefix.TrimEnd('/'));
        if (Directory.Exists(stagedExternal))
        {
            Directory.Delete(stagedExternal, recursive: true);
        }

        // Put the decomp files back first (atomic per file), only those this backup holds.
        if (external is not null && manifest is not null)
        {
            var writer = new Safety.SafeFileWriter(external.Root, allowedFiles: external.RelativePaths);
            foreach (var f in manifest.Files.Where(f => f.RelativePath.StartsWith(BackupManifest.ExternalPrefix, StringComparison.Ordinal)))
            {
                var rel = f.RelativePath[BackupManifest.ExternalPrefix.Length..];
                var bytes = await File.ReadAllBytesAsync(PathRules.FromPortableRelative(filesDir, f.RelativePath), cancellationToken);
                await writer.WriteAllBytesAsync(PathRules.FromPortableRelative(external.Root, rel), bytes, cancellationToken);
            }
        }

        Directory.Move(project.Directory, aside);
        try
        {
            Directory.Move(staging, project.Directory);
        }
        catch
        {
            Directory.Move(aside, project.Directory); // put the original back
            throw;
        }

        Directory.Delete(aside, recursive: true);
        return safety;
    }

    private static bool IsTransient(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tmp", StringComparison.Ordinal)
            || name.EndsWith("-journal", StringComparison.Ordinal)
            || name.EndsWith("-wal", StringComparison.Ordinal)
            || name.EndsWith("-shm", StringComparison.Ordinal);
    }
}
