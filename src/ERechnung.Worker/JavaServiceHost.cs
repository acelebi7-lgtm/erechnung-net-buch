using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace ERechnung.Worker;

public sealed class KositHostOptions
{
    /// <summary>Soll der Worker den KoSIT-Daemon selbst starten und überwachen?</summary>
    public bool Enabled { get; set; }
    public string JavaPath { get; set; } = "java";
    public string ValidatorJar { get; set; } = "tools/validator-standalone.jar";
    public string ConfigDir { get; set; } = "tools/xrechnung-config";
    public int Port { get; set; } = 8081;
    public string JavaOptions { get; set; } = "-Xmx1g";
}

/// <summary>
/// Startet den KoSIT-Validator als Kindprozess, startet ihn nach einem Absturz neu
/// (mit wachsender Wartezeit) und beendet ihn sauber beim Stoppen des Dienstes.
/// So läuft der Java-Daemon unter demselben Windows-Dienst wie der Worker.
/// </summary>
public sealed class JavaServiceHost(IOptions<KositHostOptions> options, ILogger<JavaServiceHost> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var delay = TimeSpan.FromSeconds(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            var psi = new ProcessStartInfo(o.JavaPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var opt in o.JavaOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(opt);
            foreach (var a in new[] { "-jar", Path.GetFullPath(o.ValidatorJar),
                                      "-s", Path.Combine(Path.GetFullPath(o.ConfigDir), "scenarios.xml"),
                                      "-r", Path.GetFullPath(o.ConfigDir),
                                      "-D", "-H", "localhost", "-P", o.Port.ToString(), "-G" })
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.LogDebug("KoSIT: {Line}", e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.LogDebug("KoSIT: {Line}", e.Data); };

            DateTime started = DateTime.UtcNow;
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                log.LogInformation("KoSIT-Daemon gestartet (PID {Pid}, Port {Port})", process.Id, o.Port);
                await process.WaitForExitAsync(stoppingToken);
                log.LogWarning("KoSIT-Daemon beendet mit Code {Code}", process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                // Dienst wird gestoppt: Java-Prozess mitnehmen.
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                log.LogInformation("KoSIT-Daemon gestoppt");
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "KoSIT-Daemon konnte nicht gestartet werden");
            }

            // Lief der Prozess länger als 10 Minuten stabil, Wartezeit zurücksetzen.
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(10)) delay = TimeSpan.FromSeconds(5);
            log.LogInformation("Neustart in {Delay}", delay);
            await Task.Delay(delay, stoppingToken);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
        }
    }
}
