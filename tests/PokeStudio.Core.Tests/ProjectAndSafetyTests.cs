using PokeStudio.Core.Platform;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Safety;
using PokeStudio.Core.Storage;

namespace PokeStudio.Core.Tests;

public class ProjectAndSafetyTests
{
    [Fact]
    public async Task Create_copies_the_game_and_never_changes_the_original()
    {
        using var tmp = new TempDir();
        var (project, original) = await Fixtures.CreateDemoProjectAsync(tmp);
        var originalHash = await Hashing.Sha256FileAsync(original);

        Assert.True(File.Exists(project.WorkingSourcePath));
        Assert.True(Directory.Exists(project.DataDir));
        Assert.Equal(originalHash, project.Manifest.OriginalSha256);
        Assert.Equal(original, project.Manifest.OriginalSourcePath);
        Assert.NotEqual(original, project.WorkingSourcePath);
    }

    [Fact]
    public async Task Open_round_trips_the_manifest()
    {
        using var tmp = new TempDir();
        var (project, _) = await Fixtures.CreateDemoProjectAsync(tmp);

        var reopened = await new ProjectService().OpenAsync(project.Directory);

        Assert.Equal(project.Manifest, reopened.Manifest);
    }

    [Fact]
    public async Task Projects_with_the_same_name_get_unique_folders()
    {
        using var tmp = new TempDir();
        var (a, original) = await Fixtures.CreateDemoProjectAsync(tmp);
        var module = new PokeStudio.Modules.Demo.DemoGameModule();
        var source = new PokeStudio.Core.Modules.GameSource(original, PokeStudio.Core.Modules.GameSourceKind.DemoFile);
        var b = await new ProjectService().CreateAsync("My Demo", source, module, module.Detect(source), tmp.Combine("projects"));
        Assert.NotEqual(a.Directory, b.Directory);
        Assert.EndsWith("My Demo (2)", b.Directory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_rejects_newer_schema_and_damaged_files()
    {
        using var tmp = new TempDir();
        var (project, _) = await Fixtures.CreateDemoProjectAsync(tmp);
        var service = new ProjectService();

        await JsonStore.WriteAsync(project.ManifestPath, project.Manifest with { SchemaVersion = 99 });
        var newer = await Assert.ThrowsAsync<ProjectFormatException>(() => service.OpenAsync(project.Directory));
        Assert.Contains("newer version", newer.Message, StringComparison.Ordinal);

        await File.WriteAllTextAsync(project.ManifestPath, "{ broken");
        await Assert.ThrowsAsync<ProjectFormatException>(() => service.OpenAsync(project.Directory));

        await Assert.ThrowsAsync<ProjectFormatException>(() => service.OpenAsync(tmp.Combine("nothing-here")));
    }

    [Fact]
    public async Task Safe_writer_refuses_paths_outside_root_and_the_original()
    {
        using var tmp = new TempDir();
        var (project, original) = await Fixtures.CreateDemoProjectAsync(tmp);
        var writer = project.CreateWriter();

        await Assert.ThrowsAsync<UnsafeWriteException>(() => writer.WriteAllTextAsync(Path.Combine("..", "escape.txt"), "x"));
        await Assert.ThrowsAsync<UnsafeWriteException>(() => writer.WriteAllTextAsync(original, "x"));

        // writing inside the project works and is atomic (no temp files left behind)
        await writer.WriteAllTextAsync(Path.Combine("data", "ok.txt"), "hello");
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(project.DataDir, "ok.txt")));
        Assert.Empty(Directory.GetFiles(project.DataDir, "*.tmp"));
    }

    [Fact]
    public async Task Safe_writer_refuses_an_original_that_sits_inside_its_root()
    {
        using var tmp = new TempDir();
        var original = tmp.Combine("game.gba");
        await File.WriteAllTextAsync(original, "original");
        var writer = new SafeFileWriter(tmp.Path, [original]);

        await Assert.ThrowsAsync<UnsafeWriteException>(() => writer.WriteAllTextAsync(original, "changed"));
        Assert.Equal("original", await File.ReadAllTextAsync(original));
    }

    [Theory]
    [InlineData("Sunny Remix", "Sunny Remix")]
    [InlineData("a/b\\c:d", "a-b-c-d")]
    [InlineData("  ...  ", "Untitled")]
    public void Safe_folder_names_work_on_every_os(string input, string expected) =>
        Assert.Equal(expected, PathRules.SafeFolderName(input));

    [Fact]
    public void Archive_names_compare_case_sensitively()
    {
        Assert.False(string.Equals("a/0/1/2", "A/0/1/2", PathRules.ArchiveNames));
        Assert.Equal("data/personal.narc", PathRules.ToPortableRelative("/root", Path.Combine("/root", "data", "personal.narc")));
    }
}
