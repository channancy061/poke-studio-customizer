using System.Diagnostics;
using System.Text;

namespace PokeStudio.Core.Build;

/// <summary>A program to run, with its arguments passed safely (no shell quoting by us).</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null)
{
    /// <summary>Some Windows tools (e.g. "wsl --list") print UTF-16.</summary>
    public Encoding? OutputEncoding { get; init; }

    public override string ToString() => FileName + " " + string.Join(' ', Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
}

public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs external programs. Behind an interface so builds and tool checks can be tested without the real tools.</summary>
public interface IProcessRunner
{
    /// <summary>Runs to completion. <paramref name="onLine"/> receives stdout and stderr lines as they arrive.
    /// Throws <see cref="ProgramNotFoundException"/> when the program doesn't exist. Cancelling kills the whole process tree.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onLine = null, CancellationToken cancellationToken = default);

    /// <summary>Starts a program without waiting (emulators, file managers).</summary>
    void Launch(ProcessSpec spec);
}

public sealed class ProgramNotFoundException(string program, Exception? inner = null)
    : Exception($"The program “{program}” couldn't be started.", inner)
{
    public string Program { get; } = program;
}

public sealed class SystemProcessRunner : IProcessRunner
{
    private const int MaxCapturedChars = 4 * 1024 * 1024;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        var psi = Info(spec);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        if (spec.OutputEncoding is { } enc)
        {
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var gate = new object();
        void Handle(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                if (output.Length < MaxCapturedChars)
                {
                    output.AppendLine(line);
                }
            }

            onLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ProgramNotFoundException(spec.FileName, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            throw;
        }

        process.WaitForExit(); // flush remaining output events
        lock (gate)
        {
            return new ProcessResult(process.ExitCode, output.ToString());
        }
    }

    public void Launch(ProcessSpec spec)
    {
        try
        {
            using var p = Process.Start(Info(spec));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ProgramNotFoundException(spec.FileName, ex);
        }
    }

    private static ProcessStartInfo Info(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in spec.Arguments)
        {
            psi.ArgumentList.Add(a);
        }

        if (spec.WorkingDirectory is not null)
        {
            psi.WorkingDirectory = spec.WorkingDirectory;
        }

        return psi;
    }
}
