using System.Globalization;
using System.Xml.Linq;

namespace ERechnung.Core;

public sealed record Contact(string? Name, string? Phone, string? Email);                 // BG-6

public sealed record VatBreakdown(
    string? Category, decimal? Rate, decimal? TaxableAmount, decimal? TaxAmount);           // BG-23

public sealed record AllowanceCharge(bool IsCharge, decimal? Amount, string? Reason);      // BG-20/21

public sealed record Attachment(
    string? Id, string? Description, string? FileName, string? MimeCode,
    byte[]? Content, string? ExternalUri)                                                   // BG-24
{
    public int Size => Content?.Length ?? 0;
}

/// <summary>Zusätzliche Rechnungsangaben für Prüfung, Kontierung und Archiv.</summary>
public sealed record InvoiceDetails(
    Contact SellerContact,
    DateOnly? DeliveryDate,                 // BT-72
    DateOnly? PeriodStart,                  // BT-73
    DateOnly? PeriodEnd,                    // BT-74
    string? PaymentTerms,                   // BT-20
    IReadOnlyList<string> PrecedingInvoices,// BT-25
    IReadOnlyList<string> Notes,            // BT-22
    IReadOnlyList<VatBreakdown> Vat,
    IReadOnlyList<AllowanceCharge> AllowancesCharges,
    IReadOnlyList<Attachment> Attachments);

public static partial class InvoiceDetailExtractor
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^#[A-Z]{3}#")]
    private static partial System.Text.RegularExpressions.Regex SubjectPrefix();

    public static InvoiceDetails Extract(XDocument doc) =>
        FormatDetector.Detect(doc).Syntax switch
        {
            Syntax.UblInvoice or Syntax.UblCreditNote => FromUbl(doc.Root!),
            Syntax.Cii => FromCii(doc.Root!),
            _ => throw new NotSupportedException($"Syntax nicht unterstützt: {doc.Root!.Name}")
        };

    // ---------- UBL ----------
    private static InvoiceDetails FromUbl(XElement r)
    {
        XNamespace cbc = Ns.Cbc, cac = Ns.Cac;
        var contact = r.Element(cac + "AccountingSupplierParty")?.Element(cac + "Party")?.Element(cac + "Contact");
        var period = r.Element(cac + "InvoicePeriod");

        return new InvoiceDetails(
            SellerContact: new Contact(
                Val(contact?.Element(cbc + "Name")),
                Val(contact?.Element(cbc + "Telephone")),
                Val(contact?.Element(cbc + "ElectronicMail"))),
            DeliveryDate: IsoDate(r.Element(cac + "Delivery")?.Element(cbc + "ActualDeliveryDate")),
            PeriodStart: IsoDate(period?.Element(cbc + "StartDate")),
            PeriodEnd: IsoDate(period?.Element(cbc + "EndDate")),
            PaymentTerms: Val(r.Element(cac + "PaymentTerms")?.Element(cbc + "Note")),
            PrecedingInvoices: r.Elements(cac + "BillingReference")
                .Select(b => Val(b.Element(cac + "InvoiceDocumentReference")?.Element(cbc + "ID")))
                .OfType<string>().ToList(),
            // In UBL steht der Betreffcode (BT-21) als Präfix im Text: "#AAI#Hinweistext".
            Notes: r.Elements(cbc + "Note").Select(Val).OfType<string>()
                .Select(n => SubjectPrefix().Replace(n, "")).ToList(),
            Vat: r.Elements(cac + "TaxTotal").SelectMany(t => t.Elements(cac + "TaxSubtotal"))
                .Select(s => new VatBreakdown(
                    Category: Val(s.Element(cac + "TaxCategory")?.Element(cbc + "ID")),
                    Rate: Dec(s.Element(cac + "TaxCategory")?.Element(cbc + "Percent")),
                    TaxableAmount: Dec(s.Element(cbc + "TaxableAmount")),
                    TaxAmount: Dec(s.Element(cbc + "TaxAmount")))).ToList(),
            AllowancesCharges: r.Elements(cac + "AllowanceCharge")
                .Select(a => new AllowanceCharge(
                    IsCharge: (string?)a.Element(cbc + "ChargeIndicator") == "true",
                    Amount: Dec(a.Element(cbc + "Amount")),
                    Reason: Val(a.Element(cbc + "AllowanceChargeReason")))).ToList(),
            Attachments: r.Elements(cac + "AdditionalDocumentReference")
                .Where(d => d.Element(cac + "Attachment") is not null)
                .Select(d =>
                {
                    var att = d.Element(cac + "Attachment")!;
                    var bin = att.Element(cbc + "EmbeddedDocumentBinaryObject");
                    return new Attachment(
                        Id: Val(d.Element(cbc + "ID")),
                        Description: Val(d.Element(cbc + "DocumentDescription")),
                        FileName: (string?)bin?.Attribute("filename"),
                        MimeCode: (string?)bin?.Attribute("mimeCode"),
                        Content: Base64(bin),
                        ExternalUri: Val(att.Element(cac + "ExternalReference")?.Element(cbc + "URI")));
                }).ToList());
    }

    // ---------- CII ----------
    private static InvoiceDetails FromCii(XElement r)
    {
        XNamespace rsm = Ns.Rsm, ram = Ns.Ram, udt = Ns.Udt;
        var tx = r.Element(rsm + "SupplyChainTradeTransaction");
        var agreement = tx?.Element(ram + "ApplicableHeaderTradeAgreement");
        var delivery = tx?.Element(ram + "ApplicableHeaderTradeDelivery");
        var settlement = tx?.Element(ram + "ApplicableHeaderTradeSettlement");
        var contact = agreement?.Element(ram + "SellerTradeParty")?.Element(ram + "DefinedTradeContact");
        var period = settlement?.Element(ram + "BillingSpecifiedPeriod");

        return new InvoiceDetails(
            SellerContact: new Contact(
                Val(contact?.Element(ram + "PersonName")),
                Val(contact?.Element(ram + "TelephoneUniversalCommunication")?.Element(ram + "CompleteNumber")),
                Val(contact?.Element(ram + "EmailURIUniversalCommunication")?.Element(ram + "URIID"))),
            DeliveryDate: CiiDate(delivery?.Element(ram + "ActualDeliverySupplyChainEvent")?
                .Element(ram + "OccurrenceDateTime")?.Element(udt + "DateTimeString")),
            PeriodStart: CiiDate(period?.Element(ram + "StartDateTime")?.Element(udt + "DateTimeString")),
            PeriodEnd: CiiDate(period?.Element(ram + "EndDateTime")?.Element(udt + "DateTimeString")),
            PaymentTerms: Val(settlement?.Element(ram + "SpecifiedTradePaymentTerms")?.Element(ram + "Description")),
            PrecedingInvoices: (settlement?.Elements(ram + "InvoiceReferencedDocument") ?? [])
                .Select(d => Val(d.Element(ram + "IssuerAssignedID"))).OfType<string>().ToList(),
            Notes: (r.Element(rsm + "ExchangedDocument")?.Elements(ram + "IncludedNote") ?? [])
                .Select(n => Val(n.Element(ram + "Content"))).OfType<string>().ToList(),
            Vat: (settlement?.Elements(ram + "ApplicableTradeTax") ?? [])
                .Select(t => new VatBreakdown(
                    Category: Val(t.Element(ram + "CategoryCode")),
                    Rate: Dec(t.Element(ram + "RateApplicablePercent")),
                    TaxableAmount: Dec(t.Element(ram + "BasisAmount")),
                    TaxAmount: Dec(t.Element(ram + "CalculatedAmount")))).ToList(),
            AllowancesCharges: (settlement?.Elements(ram + "SpecifiedTradeAllowanceCharge") ?? [])
                .Select(a => new AllowanceCharge(
                    IsCharge: (string?)a.Element(ram + "ChargeIndicator")?.Element(udt + "Indicator") == "true",
                    Amount: Dec(a.Element(ram + "ActualAmount")),
                    Reason: Val(a.Element(ram + "Reason")))).ToList(),
            // BG-24: zusätzliche Belege haben den TypeCode 916
            Attachments: (agreement?.Elements(ram + "AdditionalReferencedDocument") ?? [])
                .Where(d => (string?)d.Element(ram + "TypeCode") == "916")
                .Select(d =>
                {
                    var bin = d.Element(ram + "AttachmentBinaryObject");
                    return new Attachment(
                        Id: Val(d.Element(ram + "IssuerAssignedID")),
                        Description: Val(d.Element(ram + "Name")),
                        FileName: (string?)bin?.Attribute("filename"),
                        MimeCode: (string?)bin?.Attribute("mimeCode"),
                        Content: Base64(bin),
                        ExternalUri: Val(d.Element(ram + "URIID")));
                }).ToList());
    }

    // ---------- Hilfsfunktionen ----------
    private static string? Val(XElement? e) =>
        string.IsNullOrWhiteSpace(e?.Value) ? null : e!.Value.Trim();

    private static decimal? Dec(XElement? e) =>
        decimal.TryParse(e?.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateOnly? IsoDate(XElement? e) =>
        DateOnly.TryParseExact(e?.Value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;

    private static DateOnly? CiiDate(XElement? e) =>
        DateOnly.TryParseExact(e?.Value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Base64 tolerant dekodieren (Zeilenumbrüche und Leerzeichen sind erlaubt).</summary>
    private static byte[]? Base64(XElement? e)
    {
        if (string.IsNullOrWhiteSpace(e?.Value)) return null;
        string clean = string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c)));
        try { return Convert.FromBase64String(clean); }
        catch (FormatException) { return null; }
    }
}
