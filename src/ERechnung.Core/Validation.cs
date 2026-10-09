using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;

namespace ERechnung.Core;

public enum Severity { Error, Warning, Information }

public sealed record ValidationMessage(Severity Severity, string? Code, string Text, string? Location);

public sealed record ValidationResult(
    bool IsValid,
    string Engine,
    IReadOnlyList<ValidationMessage> Messages,
    string? ReportXml = null,
    string? ReportHtml = null)
{
    public int ErrorCount => Messages.Count(m => m.Severity == Severity.Error);
}

public interface IInvoiceValidator
{
    Task<ValidationResult> ValidateAsync(byte[] content, string fileName, CancellationToken ct = default);
}

/// <summary>
/// Spricht den KoSIT-Validator im Daemon-Modus an (java -jar validator-…-standalone.jar -D).
/// HTTP 200 = annehmbar, 406 = abgelehnt; der Prüfbericht steht im Antwort-Body.
/// </summary>
public sealed class KositDaemonValidator(HttpClient http) : IInvoiceValidator
{
    private static readonly XNamespace Rep = "http://www.xoev.de/de/validator/varl/1";

    public async Task<ValidationResult> ValidateAsync(byte[] content, string fileName, CancellationToken ct = default)
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/xml");

        // Das letzte Pfadsegment wird zum Dokumentnamen im Bericht.
        using var response = await http.PostAsync("/" + Uri.EscapeDataString(fileName), body, ct);
        string text = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NotAcceptable))
            throw new InvalidOperationException(
                $"KoSIT-Validator antwortet mit {(int)response.StatusCode}: {Truncate(text, 300)}");

        return ParseReport(text);
    }

    public static ValidationResult ParseReport(string reportXml)
    {
        var doc = XDocument.Parse(reportXml);
        bool accepted = doc.Descendants(Rep + "accept").Any();

        var messages = doc.Descendants(Rep + "message").Select(m => new ValidationMessage(
            Severity: (string?)m.Attribute("level") switch
            {
                "error" => Severity.Error,
                "warning" => Severity.Warning,
                _ => Severity.Information
            },
            Code: (string?)m.Attribute("code"),
            Text: m.Value.Trim(),
            Location: (string?)m.Attribute("xpathLocation"))).ToList();

        // Der lesbare Prüfbericht steckt als XHTML in rep:explanation.
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        string? html = doc.Descendants(Rep + "explanation").Elements(xhtml + "html")
            .FirstOrDefault()?.ToString();

        return new ValidationResult(accepted, "KoSIT", messages, reportXml, html);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
