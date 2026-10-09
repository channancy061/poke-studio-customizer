using PokeStudio.Core.Modules;
using PokeStudio.Core.Projects;
using PokeStudio.Modules.Demo;

namespace PokeStudio.Core.Tests;

/// <summary>A unique temp folder deleted after the test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pokestudio-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a locked file on Windows shouldn't fail the test run
        }
    }
}

/// <summary>Clock the tests can move forward.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public static class Fixtures
{
    public static string DemoGameFile =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Demo Valley.demogame.json");

    /// <summary>Copies the demo game into <paramref name="dir"/> (as the user's "original") and creates a project from it.</summary>
    public static async Task<(Project Project, string Original)> CreateDemoProjectAsync(TempDir dir, TimeProvider? clock = null)
    {
        var originalDir = dir.Combine("my-games");
        Directory.CreateDirectory(originalDir);
        var original = System.IO.Path.Combine(originalDir, "Demo Valley.demogame.json");
        File.Copy(DemoGameFile, original);

        var module = new DemoGameModule();
        var source = new GameSource(original, GameSourceKind.DemoFile);
        var project = await new ProjectService(clock).CreateAsync("My Demo", source, module, module.Detect(source), dir.Combine("projects"));
        return (project, original);
    }
}
