using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ERechnung.Worker;

public sealed class InboxOptions
{
    public string InboxPath { get; set; } = "data/eingang";
    public string AcceptedPath { get; set; } = "data/angenommen";
    public string RejectedPath { get; set; } = "data/abgelehnt";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Routing: Leitweg-ID/Käuferreferenz (BT-10) → Unterordner.</summary>
    public Dictionary<string, string> RoutesByBuyerReference { get; set; } = new();
}

/// <summary>
/// Überwacht einen Eingangsordner, schickt jede Datei an die API und sortiert das Ergebnis
/// in "angenommen" oder "abgelehnt" – mit Sichtbeleg und Prüfbericht.
/// </summary>
public sealed class FolderInboxWorker(
    InvoiceApiClient api, IOptions<InboxOptions> options, ILogger<FolderInboxWorker> log)
    : BackgroundService
{
    private readonly InboxOptions _o = options.Value;

    // Umlaute lesbar lassen statt \u00FC – die Dateien sollen auch für Menschen lesbar sein.
    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };
    private readonly Dictionary<string, int> _attempts = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_o.InboxPath);
        log.LogInformation("Überwache {Inbox} alle {Interval}", _o.InboxPath, _o.PollInterval);

        using var timer = new PeriodicTimer(_o.PollInterval);
        do
        {
            foreach (var path in Directory.EnumerateFiles(_o.InboxPath)
                         .Where(p => p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                  || p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                // Nur Dateien anfassen, die seit ein paar Sekunden nicht mehr geschrieben werden.
                if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddSeconds(-5)) continue;
                await HandleFileAsync(path, stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task HandleFileAsync(string path, CancellationToken ct)
    {
        string name = Path.GetFileName(path);
        try
        {
            byte[] data = await File.ReadAllBytesAsync(path, ct);
            var result = await api.ProcessAsync(data, name, ct);

            // Ziel: Ergebnisordner / (Route) / JJJJ-MM / <hash>_<name>
            string baseDir = result.Accepted ? _o.AcceptedPath : _o.RejectedPath;
            string route = RouteFor(result);
            string target = Path.Combine(baseDir, route, DateTime.Now.ToString("yyyy-MM"));
            Directory.CreateDirectory(target);
            string prefix = Path.Combine(target, result.Sha256[..12] + "_" + Path.GetFileNameWithoutExtension(name));

            // Idempotenz: dieselbe Datei (gleicher Hash) wurde schon verarbeitet.
            if (File.Exists(prefix + Path.GetExtension(name)))
            {
                log.LogWarning("Duplikat erkannt, wird verworfen: {File} ({Hash})", name, result.Sha256);
                File.Delete(path);
                return;
            }

            await File.WriteAllTextAsync(prefix + ".json",
                JsonSerializer.Serialize(result, FileJson), ct);

            if (result.Accepted)
                await File.WriteAllBytesAsync(prefix + "-sichtbeleg.pdf", await api.VisualizeAsync(data, name, ct), ct);
            else if (result.Validation?.TryGetProperty("reportHtml", out var html) == true
                     && html.ValueKind == JsonValueKind.String)
                await File.WriteAllTextAsync(prefix + "-pruefbericht.html", html.GetString(), ct);

            // Original zuletzt verschieben: Erst wenn alles geschrieben ist, gilt die Datei als erledigt.
            File.Move(path, prefix + Path.GetExtension(name));
            _attempts.Remove(path);
            log.LogInformation("{File}: {Status} → {Target}", name,
                result.Accepted ? "angenommen" : "abgelehnt", target);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            int n = _attempts[path] = _attempts.GetValueOrDefault(path) + 1;
            log.LogWarning(ex, "{File}: Versuch {N}/{Max} fehlgeschlagen", name, n, _o.MaxAttempts);
            if (n >= _o.MaxAttempts)
            {
                string quarantine = Path.Combine(_o.RejectedPath, "_technischer-fehler");
                Directory.CreateDirectory(quarantine);
                File.Move(path, Path.Combine(quarantine, name), overwrite: true);
                _attempts.Remove(path);
            }
        }
    }

    private string RouteFor(ProcessResponse r)
    {
        if (r.Invoice is { ValueKind: JsonValueKind.Object } inv
            && inv.TryGetProperty("buyerReference", out var br)
            && br.GetString() is { } key
            && _o.RoutesByBuyerReference.TryGetValue(key, out var folder))
            return folder;
        return "allgemein";
    }
}
