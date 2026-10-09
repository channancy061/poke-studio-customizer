using PokeStudio.Core.Platform;

namespace PokeStudio.Core.Safety;

public sealed class UnsafeWriteException(string message) : InvalidOperationException(message);

/// <summary>
/// The only way modules write files. Writes are confined to one root folder (the project),
/// never touch the user's original game files, and are atomic: data goes to a temp file in the
/// same folder and then replaces the target, so a crash can't leave a half-written file.
/// </summary>
public sealed class SafeFileWriter
{
    private readonly string _root;
    private readonly HashSet<string> _protected;
    private readonly HashSet<string>? _allowed;

    /// <param name="root">Folder writes must stay inside.</param>
    /// <param name="protectedPaths">Files that must never be written (e.g. the original game file).</param>
    /// <param name="allowedFiles">If given, only these files may be written (used for in-place decomp editing).</param>
    public SafeFileWriter(string root, IEnumerable<string>? protectedPaths = null, IEnumerable<string>? allowedFiles = null)
    {
        _root = Path.GetFullPath(root);
        var comparer = PathRules.HostPaths == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _protected = new HashSet<string>((protectedPaths ?? []).Select(Path.GetFullPath), comparer);
        _allowed = allowedFiles is null ? null : new HashSet<string>(allowedFiles.Select(f => Path.GetFullPath(Path.Combine(_root, f))), comparer);
    }

    public string Root => _root;

    public string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
        if (!PathRules.IsInside(_root, full))
        {
            throw new UnsafeWriteException($"Refusing to write outside the project folder: {full}");
        }

        if (_protected.Contains(full))
        {
            throw new UnsafeWriteException($"Refusing to overwrite an original game file: {full}");
        }

        if (_allowed is not null && !_allowed.Contains(full))
        {
            throw new UnsafeWriteException($"This module isn't allowed to change {full}.");
        }

        return full;
    }

    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var target = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await stream.WriteAsync(data, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken = default) =>
        WriteAllBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(text), cancellationToken);
}
