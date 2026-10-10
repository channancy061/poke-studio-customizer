using PokeStudio.Core.Build;
using PokeStudio.Core.Models;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class BuildEnvironmentTests
{
    private static readonly BuildDefinition Def = new ExpansionModule().Build;

    [Fact]
    public async Task Missing_wsl_explains_how_to_turn_it_on_and_never_installs_it_itself()
    {
        var runner = new FakeProcessRunner { Handler = (_, _, _) => throw new ProgramNotFoundException("wsl.exe") };
        var env = new WslBuildEnvironment(runner);

        var status = Assert.Single(await env.CheckAsync(Def));

        Assert.Equal(ToolState.Missing, status.State);
        Assert.Equal(ToolFix.InstallWsl, status.Fix);
        Assert.Contains("wsl --install", status.HowToFix, StringComparison.Ordinal);
        Assert.Null(env.FixCommand(ToolFix.InstallWsl, Def));
        Assert.Null(env.FixCommand(ToolFix.InstallUbuntu, Def));
    }

    [Fact]
    public async Task Windows_wsl_stub_counts_as_not_installed()
    {
        const string stub = "The Windows Subsystem for Linux is not installed. You can install by running 'wsl.exe --install'.\r\nFor more information please visit https://aka.ms/wslinstall\r\n";
        var runner = new FakeProcessRunner { Handler = (_, _, _) => Task.FromResult(new ProcessResult(1, stub)) };

        var status = Assert.Single(await new WslBuildEnvironment(runner).CheckAsync(Def));

        Assert.Equal(ToolFix.InstallWsl, status.Fix);
    }

    [Fact]
    public async Task Wsl_with_no_linux_installed_asks_for_ubuntu()
    {
        var runner = new FakeProcessRunner { Handler = (_, _, _) => Task.FromResult(new ProcessResult(-1, "Windows Subsystem for Linux has no installed distributions.\r\n")) };

        var list = await new WslBuildEnvironment(runner).CheckAsync(Def);

        Assert.Equal(ToolState.Ready, list[0].State);
        Assert.Equal(ToolFix.InstallUbuntu, list[1].Fix);
    }

    [Fact]
    public async Task Wsl_without_ubuntu_says_so()
    {
        var runner = new FakeProcessRunner { Handler = (_, _, _) => Task.FromResult(new ProcessResult(0, "d\0o\0c\0k\0e\0r\0-\0d\0e\0s\0k\0t\0o\0p\0\r\0\n\0")) };

        var list = await new WslBuildEnvironment(runner).CheckAsync(Def);

        Assert.Equal(ToolState.Ready, list[0].State);
        Assert.Equal(ToolFix.InstallUbuntu, list[1].Fix);
    }

    [Fact]
    public async Task Missing_packages_are_named_in_plain_words_and_can_be_installed_inside_ubuntu()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (spec, _, _) => Task.FromResult(spec.Arguments[0] == "--list"
                ? new ProcessResult(0, "Ubuntu\n")
                : new ProcessResult(0, "OK:make\nOK:gcc\nMISSING:arm-none-eabi-gcc\nOK:python3\nMISSING:/usr/include/png.h\n")),
        };
        var env = new WslBuildEnvironment(runner);

        var tools = (await env.CheckAsync(Def)).Last();

        Assert.Equal(ToolState.Missing, tools.State);
        Assert.Contains("GBA compiler", tools.Detail, StringComparison.Ordinal);
        Assert.Contains("PNG library", tools.Detail, StringComparison.Ordinal);
        var fix = Assert.IsType<ProcessSpec>(env.FixCommand(tools.Fix, Def));
        Assert.Contains("root", fix.Arguments);
        Assert.Contains(fix.Arguments, a => a.Contains("gcc-arm-none-eabi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Everything_installed_is_ready()
    {
        var list = await new WslBuildEnvironment(FakeProcessRunner.AllReady()).CheckAsync(Def);
        Assert.All(list, s => Assert.Equal(ToolState.Ready, s.State));
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void Build_runs_inside_the_project_folder_through_wsl()
    {
        var spec = new WslBuildEnvironment(new FakeProcessRunner()).Command(@"D:\Hacks\my hack", Def.Command);
        Assert.Equal("wsl.exe", spec.FileName);
        Assert.Equal(["-d", "Ubuntu", "--cd", @"D:\Hacks\my hack", "--", "sh", "-c", Def.Command], spec.Arguments);
        Assert.Equal("pokeemerald.gba", Def.OutputFile);
    }
}

public class BuildServiceTests
{
    private static readonly BuildDefinition Def = new ExpansionModule().Build;

    private sealed class FixedClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Successful_build_keeps_a_dated_copy_and_reports_progress()
    {
        using var tmp = new TempDir();
        var source = Directory.CreateDirectory(tmp.Combine("source")).FullName;
        var project = Directory.CreateDirectory(tmp.Combine("project")).FullName;
        var runner = FakeProcessRunner.AllReady(Path.Combine(source, "pokeemerald.gba"));
        var service = new BuildService(runner, new WslBuildEnvironment(runner), new FixedClock(new DateTimeOffset(2026, 10, 10, 15, 30, 0, TimeSpan.Zero)));
        var steps = new List<BuildProgress>();

        var result = await service.BuildAsync(Def, source, project, progress: new SyncProgress<BuildProgress>(steps.Add));

        Assert.True(result.Succeeded);
        Assert.Matches(@"^2026-10-10_\d{6}_pokeemerald\.gba$", result.Output!.FileName);
        Assert.True(File.Exists(result.Output.FilePath));
        Assert.Contains(steps, s => s.Step == "Building pokemon.c");
        Assert.Contains(steps, s => s.Step == "Putting the game together…");
        Assert.Equal(1, steps[^1].Fraction);
        Assert.Single(BuildService.History(project));
    }

    [Fact]
    public void Only_the_newest_builds_are_kept()
    {
        using var tmp = new TempDir();
        var game = tmp.Combine("pokeemerald.gba");
        File.WriteAllBytes(game, [0]);
        var project = Directory.CreateDirectory(tmp.Combine("project")).FullName;
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var service = new BuildService(new FakeProcessRunner(), new NativeBuildEnvironment(new FakeProcessRunner()), clock);

        for (var i = 0; i < 7; i++)
        {
            service.Keep(game, project, keep: 5);
            clock.Now = clock.Now.AddHours(1);
        }

        var history = BuildService.History(project);
        Assert.Equal(5, history.Count);
        Assert.True(history[0].CreatedUtc > history[4].CreatedUtc); // newest first
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), history[0].CreatedUtc);
    }

    [Fact]
    public async Task Failed_build_is_explained_in_plain_words()
    {
        using var tmp = new TempDir();
        var runner = FakeProcessRunner.AllReady(make: _ => new ProcessResult(2,
            "src/data/pokemon/species_info/gen_1_families.h:42:20: error: 'SPECIES_BLOOMTLEX' undeclared here (not in a function)\nmake: *** [Makefile:400: build/emerald/src/pokemon.o] Error 1\n"));
        var service = new BuildService(runner, new WslBuildEnvironment(runner));

        var result = await service.BuildAsync(Def, tmp.Path, tmp.Path);

        Assert.Equal(BuildOutcome.Failed, result.Outcome);
        var problem = Assert.Single(result.Problems);
        Assert.Equal("“SPECIES_BLOOMTLEX” doesn't exist", problem.Title);
        Assert.Contains("No Pokémon has this name", problem.Explanation, StringComparison.Ordinal);
        Assert.Equal("src/data/pokemon/species_info/gen_1_families.h", problem.File);
        Assert.Equal(42, problem.Line);
        Assert.Empty(BuildService.History(tmp.Path));
    }

    [Fact]
    public async Task Without_wsl_nothing_runs_and_the_result_says_not_ready()
    {
        using var tmp = new TempDir();
        var runner = new FakeProcessRunner { Handler = (_, _, _) => throw new ProgramNotFoundException("wsl.exe") };

        var result = await new BuildService(runner, new WslBuildEnvironment(runner)).BuildAsync(Def, tmp.Path, tmp.Path);

        Assert.Equal(BuildOutcome.NotReady, result.Outcome);
        Assert.DoesNotContain(runner.Ran, s => s.Arguments.Contains("make -j\"$(nproc)\""));
    }

    [Fact]
    public async Task Cancelling_stops_the_build()
    {
        using var tmp = new TempDir();
        using var cts = new CancellationTokenSource();
        var runner = FakeProcessRunner.AllReady();
        var inner = runner.Handler;
        runner.Handler = (spec, onLine, ct) => spec.Arguments.Contains(Def.Command)
            ? Task.FromException<ProcessResult>(new OperationCanceledException(ct))
            : inner(spec, onLine, ct);

        var result = await new BuildService(runner, new WslBuildEnvironment(runner)).BuildAsync(Def, tmp.Path, tmp.Path, cancellationToken: cts.Token);

        Assert.Equal(BuildOutcome.Cancelled, result.Outcome);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public class BuildErrorTranslatorTests
{
    [Theory]
    [InlineData("build/emerald/src/x.o: undefined reference to `gSpeciesInfoBloom'", "“gSpeciesInfoBloom” is used but never defined")]
    [InlineData("arm-none-eabi-ld: pokeemerald.elf section `.rodata' will not fit in region `ROM'", "Your game is too big")]
    [InlineData("src/pokemon.c:5:10: fatal error: png.h: No such file or directory", "A required file is missing: png.h")]
    [InlineData("make: *** No rule to make target 'graphics/pokemon/bloomtle/front.png', needed by 'x'.  Stop.", "A file is missing: graphics/pokemon/bloomtle/front.png")]
    [InlineData("/bin/sh: 1: arm-none-eabi-gcc: not found", "A build tool is missing: arm-none-eabi-gcc")]
    [InlineData("src/data/moves_info.h:10:5: error: expected '}' before ';' token", "There's a typo in a data file")]
    [InlineData("cp: cannot create regular file 'pokeemerald.gba': Permission denied", "A file couldn't be written")]
    public void Common_errors_get_friendly_titles(string line, string title) =>
        Assert.Equal(title, BuildErrorTranslator.TryTranslate(line)?.Title);

    [Fact]
    public void Unknown_failures_still_get_a_message()
    {
        var p = Assert.Single(BuildErrorTranslator.Translate("something odd\nError 1"));
        Assert.Equal("The build stopped with an error", p.Title);
    }

    [Fact]
    public void Duplicate_lines_are_reported_once()
    {
        const string line = "src/a.h:1:1: error: 'MOVE_ZAP' undeclared here";
        Assert.Single(BuildErrorTranslator.Translate(line + "\n" + line));
    }
}

public class EmulatorTests
{
    [Theory]
    [InlineData("mGBA.exe", "mgba")]
    [InlineData("VisualBoyAdvance.exe", "vba")]
    [InlineData("DeSmuME_0.9.13_x64.exe", "desmume")]
    [InlineData("citra-qt.exe", "citra")]
    [InlineData("melonDS.exe", "melonds")]
    [InlineData("notepad.exe", null)]
    public void Recognises_emulators_by_file_name(string file, string? id) =>
        Assert.Equal(id, Emulators.Identify(Path.Combine("C:", "x", file))?.Id);

    [Fact]
    public void Suggests_one_per_platform_but_any_known_one_works()
    {
        Assert.Equal("mGBA", Emulators.Suggested(GamePlatform.Gba)!.Name);
        Assert.Equal("melonDS", Emulators.Suggested(GamePlatform.Nds)!.Name);
        Assert.Equal("Azahar", Emulators.Suggested(GamePlatform.N3ds)!.Name);
        Assert.Contains(Emulators.For(GamePlatform.Nds), k => k.Id == "desmume");
    }

    [Fact]
    public void Check_explains_problems_and_warns_about_old_vba()
    {
        using var tmp = new TempDir();
        var vba = tmp.Combine("VisualBoyAdvance.exe");
        var citra = tmp.Combine("citra-qt.exe");
        File.WriteAllBytes(vba, [0]);
        File.WriteAllBytes(citra, [0]);

        Assert.Equal(ToolState.Warning, Emulators.Check(null, GamePlatform.Gba).State);
        Assert.Equal(ToolState.Missing, Emulators.Check(tmp.Path, GamePlatform.Gba).State);
        Assert.Equal(ToolState.Missing, Emulators.Check(tmp.Combine("gone.exe"), GamePlatform.Gba).State);
        Assert.Equal(ToolState.Missing, Emulators.Check(citra, GamePlatform.Gba).State);
        Assert.Equal(ToolState.Ready, Emulators.Check(citra, GamePlatform.N3ds).State);
        var old = Emulators.Check(vba, GamePlatform.Gba);
        Assert.Equal(ToolState.Warning, old.State);
        Assert.Contains("very old", old.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Finds_emulators_in_nested_game_folders()
    {
        using var tmp = new TempDir();
        string Make(params string[] parts)
        {
            var path = tmp.Combine(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0]);
            return path;
        }

        var vba = Make("Pokemon", "GBA", "vbalink180b0", "VisualBoyAdvance.exe");
        var ds = Make("Pokemon", "NDS", "desmume-0.9.13-win64", "DeSmuME_0.9.13_x64.exe");
        var citraQt = Make("Pokemon", "3DS", "Citra", "nightly", "citra-qt.exe");
        Make("Pokemon", "3DS", "Citra", "nightly", "citra.exe");
        Make("Pokemon", "GBA", "Roms", "mgba.exe"); // ROM folders are skipped

        var found = Emulators.Find([tmp.Path]).Select(c => c.Path).ToList();

        Assert.Equal(3, found.Count);
        Assert.Contains(vba, found);
        Assert.Contains(ds, found);
        Assert.Contains(citraQt, found); // the GUI build is preferred over citra.exe
    }

    [Fact]
    public void Launch_passes_the_game_as_the_only_argument()
    {
        var spec = Emulators.LaunchSpec(Path.Combine("C:", "emu", "mgba.exe"), Path.Combine("C:", "my game.gba"));
        Assert.Equal([Path.Combine("C:", "my game.gba")], spec.Arguments);
    }
}

public class RealWslCheckTests
{
    /// <summary>Runs the real check on Windows: whatever is installed, it must answer in plain words without crashing.</summary>
    [Fact]
    public async Task Real_wsl_check_gives_a_readable_answer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var list = await new WslBuildEnvironment(new SystemProcessRunner()).CheckAsync(new ExpansionModule().Build);

        Assert.StartsWith("WSL", list[0].Name, StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("POKESTUDIO_EXPECT_NO_WSL") == "1")
        {
            Assert.Equal(ToolFix.InstallWsl, Assert.Single(list).Fix);
        }
    }
}
