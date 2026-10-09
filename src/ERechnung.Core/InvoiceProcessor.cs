using System.Security.Cryptography;

namespace ERechnung.Core;

public sealed record ProcessingResult(
    string FileName,
    string Sha256,
    FormatInfo Format,
    ValidationResult? Validation,
    InvoiceData? Invoice,
    byte[]? Xml)
{
    /// <summary>Annehmen nur, wenn es eine E-Rechnung ist und die Prüfung bestanden wurde.</summary>
    public bool Accepted => Format.IsEInvoice && Validation is { IsValid: true };
}

/// <summary>
/// Die komplette Pipeline: erkennen → (extrahieren) → validieren → Daten auslesen.
/// </summary>
public sealed class InvoiceProcessor(MustangCli mustang, KositDaemonValidator kosit)
{
    public async Task<ProcessingResult> ProcessAsync(byte[] data, string fileName, CancellationToken ct = default)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        byte[]? xml = data;
        var format = FormatDetector.Detect(data);

        // 1. PDF? Dann den XML-Anhang herausholen.
        if (format.Container == Container.Pdf)
        {
            xml = await mustang.ExtractXmlAsync(data, ct);
            if (xml is null)
            {
                var noXml = format with
                {
                    Hint = "PDF ohne eingebettete Rechnungs-XML – das ist keine E-Rechnung."
                };
                return new ProcessingResult(fileName, hash, noXml, null, null, null);
            }
            // Formatdaten aus der XML übernehmen, Container bleibt PDF.
            format = FormatDetector.Detect(xml) with { Container = Container.Pdf };
        }

        if (format.Syntax == Syntax.Unknown)
            return new ProcessingResult(fileName, hash, format, null, null, null);

        // 2. Validieren: XRechnung und reines EN 16931 über KoSIT,
        //    ZUGFeRD-PDFs und übrige Profile über Mustang.
        IInvoiceValidator validator = format.Container == Container.Xml
                                      && format.Profile is "XRECHNUNG" or "EN16931"
            ? kosit
            : mustang;
        byte[] toValidate = format.Container == Container.Pdf ? data : xml;
        var validation = await validator.ValidateAsync(toValidate, fileName, ct);

        // 3. Daten auslesen – auch bei Fehlern, damit man sie anzeigen kann.
        InvoiceData? invoice = null;
        try
        {
            invoice = InvoiceExtractor.Extract(FormatDetector.LoadXmlSafe(xml));
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or NotSupportedException)
        {
            // Extraktion ist "best effort"; die Validierung hat Vorrang.
        }

        return new ProcessingResult(fileName, hash, format, validation, invoice, xml);
    }
}
