using System.IO.Compression;
using PokeStudio.Core.Platform;

namespace PokeStudio.Modules.Expansion;

public sealed record DownloadProgress(string Stage, double? Fraction, string Detail);

/// <summary>
/// Downloads a pokeemerald-expansion release from its official GitHub repository and unpacks it.
/// Only this fixed source is used; nothing else is ever downloaded.
/// </summary>
public sealed class ExpansionDownloader(HttpClient http)
{
    public const string Repository = "https://github.com/rh-hideout/pokeemerald-expansion";
    public const string ReleaseTag = "expansion/1.17.1";

    public static Uri ZipUrl(string tag = ReleaseTag) => new($"https://codeload.github.com/rh-hideout/pokeemerald-expansion/zip/refs/tags/{tag}");

    /// <summary>Downloads into a new folder inside <paramref name="parentFolder"/> and returns its path.</summary>
    public async Task<string> DownloadAsync(string parentFolder, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(parentFolder);
        var target = UniqueFolder(Path.Combine(parentFolder, "pokeemerald-expansion"));
        var zipPath = Path.Combine(parentFolder, $".pokeemerald-expansion-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadZipAsync(zipPath, progress, cancellationToken);
            Extract(zipPath, target, progress, cancellationToken);
        }
        catch
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            throw;
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }

        progress?.Report(new DownloadProgress("Done", 1, target));
        return target;
    }

    private async Task DownloadZipAsync(string zipPath, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(ZipUrl(), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long done = 0;
        var lastReport = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            done += read;
            if (done - lastReport > 1 << 20)
            {
                lastReport = done;
                progress?.Report(new DownloadProgress("Downloading", total > 0 ? (double)done / total.Value : null, $"{done / 1048576.0:0} MB"));
            }
        }
    }

    /// <summary>Unpacks the zip, dropping GitHub's top-level folder and refusing paths that escape the target.</summary>
    internal static void Extract(string zipPath, string target, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries;
        Directory.CreateDirectory(target);
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            var slash = entry.FullName.IndexOf('/', StringComparison.Ordinal);
            var relative = slash < 0 ? "" : entry.FullName[(slash + 1)..];
            if (relative.Length == 0)
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine([target, .. relative.Split('/', StringSplitOptions.RemoveEmptyEntries)]));
            if (!PathRules.IsInside(target, path))
            {
                throw new InvalidDataException($"The download contains an unsafe path: {entry.FullName}");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
            if (i % 500 == 0)
            {
                progress?.Report(new DownloadProgress("Unpacking", (double)i / entries.Count, $"{i:N0} of {entries.Count:N0} files"));
            }
        }
    }

    private static string UniqueFolder(string desired)
    {
        var candidate = desired;
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
        {
            candidate = $"{desired}-{i}";
        }

        return candidate;
    }
}
