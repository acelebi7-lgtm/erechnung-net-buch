using System.Text;
using System.Xml.Linq;

namespace ERechnung.Core;

public sealed class MustangOptions
{
    public string JavaPath { get; set; } = "java";
    public string JarPath { get; set; } = "tools/Mustang-CLI.jar";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Kapselt die Mustang-Kommandozeile: Extraktion, Validierung und Sichtbeleg.
/// Jeder Aufruf läuft in einem eigenen temporären Verzeichnis.
/// </summary>
public sealed class MustangCli(MustangOptions options) : IInvoiceValidator
{
    // Höchstens vier Java-Prozesse gleichzeitig.
    private static readonly SemaphoreSlim Gate = new(initialCount: 4);

    private async Task<ToolResult> RunAsync(TempWorkspace ws, CancellationToken ct, params string[] args)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await ExternalTool.RunAsync(options.JavaPath,
                ["-Dfile.encoding=UTF-8", "-jar", Path.GetFullPath(options.JarPath), .. args],
                ws.Path, options.Timeout, ct);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Holt die eingebettete XML aus einer ZUGFeRD/Factur-X-PDF. Null, wenn keine da ist.</summary>
    public async Task<byte[]?> ExtractXmlAsync(byte[] pdf, CancellationToken ct = default)
    {
        using var ws = new TempWorkspace();
        await File.WriteAllBytesAsync(ws.File("in.pdf"), pdf, ct);
        await RunAsync(ws, ct, "--action", "extract", "--source", "in.pdf", "--out", "out.xml");
        string outFile = ws.File("out.xml");
        return File.Exists(outFile) && new FileInfo(outFile).Length > 0
            ? await File.ReadAllBytesAsync(outFile, ct)
            : null;
    }

    /// <summary>Validiert XML oder PDF (inkl. PDF/A-Prüfung bei PDFs).</summary>
    public async Task<ValidationResult> ValidateAsync(byte[] content, string fileName, CancellationToken ct = default)
    {
        using var ws = new TempWorkspace();
        string input = FormatDetector.IsPdf(content) ? "in.pdf" : "in.xml";
        await File.WriteAllBytesAsync(ws.File(input), content, ct);

        var result = await RunAsync(ws, ct, "--action", "validate", "--source", input,
            "--no-notices", "--disable-file-logging");

        // Mustang schreibt den Bericht als XML auf die Standardausgabe; der Exit-Code
        // ist bei ungültigen Rechnungen ungleich 0, deshalb werten wir den Bericht aus.
        string all = result.StdOut;
        int start = all.IndexOf("<validation", StringComparison.Ordinal);
        int end = all.LastIndexOf("</validation>", StringComparison.Ordinal);
        if (start < 0 || end < 0)
            throw new InvalidOperationException("Mustang lieferte keinen Prüfbericht: " + result.StdErr);

        string report = all[start..(end + "</validation>".Length)];
        return ParseReport(report);
    }

    public static ValidationResult ParseReport(string reportXml)
    {
        var doc = XDocument.Parse(reportXml);
        bool valid = (string?)doc.Root!.Element("summary")?.Attribute("status") == "valid";

        static ValidationMessage ToMessage(XElement e, Severity s) => new(
            s,
            Code: (string?)e.Attribute("type"),
            Text: e.Value.Trim(),
            Location: (string?)e.Attribute("location"));

        var messages = doc.Descendants("error").Select(e => ToMessage(e, Severity.Error))
            .Concat(doc.Descendants("warning").Select(e => ToMessage(e, Severity.Warning)))
            .Concat(doc.Descendants("notice").Select(e => ToMessage(e, Severity.Information)))
            .ToList();

        return new ValidationResult(valid, "Mustang", messages, reportXml);
    }

    /// <summary>Erzeugt den Sichtbeleg als "html" oder "pdf".</summary>
    public async Task<byte[]> VisualizeAsync(byte[] xml, string format, CancellationToken ct = default)
    {
        if (format is not ("html" or "pdf"))
            throw new ArgumentException("Format muss html oder pdf sein.", nameof(format));

        using var ws = new TempWorkspace();
        await File.WriteAllBytesAsync(ws.File("in.xml"), xml, ct);
        string action = format == "pdf" ? "pdf" : "visualize";
        string outName = "out." + format;

        var result = await RunAsync(ws, ct, "--action", action, "--source", "in.xml",
            "--out", outName, "--language", "de");

        string outFile = ws.File(outName);
        if (!File.Exists(outFile))
            throw new InvalidOperationException("Sichtbeleg konnte nicht erzeugt werden: " + result.StdErr);
        return await File.ReadAllBytesAsync(outFile, ct);
    }
}
