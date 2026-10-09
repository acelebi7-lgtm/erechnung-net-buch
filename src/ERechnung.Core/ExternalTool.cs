using System.Diagnostics;
using System.Text;

namespace ERechnung.Core;

public sealed record ToolResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Startet ein externes Programm (z. B. Java) mit Zeitlimit.</summary>
public static class ExternalTool
{
    public static async Task<ToolResult> RunAsync(
        string fileName, IEnumerable<string> args, string workingDirectory,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // ArgumentList kümmert sich um Leerzeichen und Anführungszeichen.
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"{fileName} konnte nicht gestartet werden.");

        // Beide Ströme parallel lesen, sonst kann der Prozess blockieren.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{Path.GetFileName(fileName)} hat das Zeitlimit von {timeout.TotalSeconds:0} s überschritten.");
        }

        return new ToolResult(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>Temporäres Arbeitsverzeichnis, das sich selbst aufräumt.</summary>
public sealed class TempWorkspace : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "erechnung-" + Guid.NewGuid().ToString("N"));

    public TempWorkspace() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
