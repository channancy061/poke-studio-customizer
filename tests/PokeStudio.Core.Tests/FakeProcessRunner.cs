using PokeStudio.Core.Build;

namespace PokeStudio.Core.Tests;

/// <summary>Stands in for real programs: answers each command with a scripted result and records what was run.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessSpec> Ran { get; } = [];
    public List<ProcessSpec> Launched { get; } = [];
    public Func<ProcessSpec, Action<string>?, CancellationToken, Task<ProcessResult>> Handler { get; set; } =
        (_, _, _) => Task.FromResult(new ProcessResult(0, ""));

    public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        Ran.Add(spec);
        return Handler(spec, onLine, cancellationToken);
    }

    public void Launch(ProcessSpec spec) => Launched.Add(spec);

    /// <summary>WSL with Ubuntu and every tool present; "make" writes <paramref name="output"/>.</summary>
    public static FakeProcessRunner AllReady(string? output = null, Func<string, ProcessResult>? make = null) => new()
    {
        Handler = (spec, onLine, _) =>
        {
            var args = string.Join(' ', spec.Arguments);
            if (args.StartsWith("--list", StringComparison.Ordinal))
            {
                return Task.FromResult(new ProcessResult(0, "Ubuntu\r\n"));
            }

            if (args.Contains("command -v", StringComparison.Ordinal))
            {
                var lines = string.Join('\n', new[] { "make", "gcc", "arm-none-eabi-gcc", "python3", "/usr/include/png.h" }.Select(t => "OK:" + t));
                return Task.FromResult(new ProcessResult(0, lines));
            }

            onLine?.Invoke("CC src/pokemon.c");
            onLine?.Invoke("arm-none-eabi-ld -o pokeemerald.elf");
            if (make is not null)
            {
                return Task.FromResult(make(args));
            }

            if (output is not null)
            {
                File.WriteAllBytes(output, [1, 2, 3, 4]);
            }

            return Task.FromResult(new ProcessResult(0, "done"));
        },
    };
}
