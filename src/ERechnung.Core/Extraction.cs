using System.Globalization;
using System.Xml.Linq;

namespace ERechnung.Core;

public sealed record Party(
    string? Name, string? VatId, string? Street,
    string? PostalCode, string? City, string? CountryCode);

public sealed record InvoiceLine(
    string? Id, string? Name, decimal? Quantity, string? Unit, decimal? NetAmount);

/// <summary>Die wichtigsten Felder einer Rechnung, benannt nach EN 16931.</summary>
public sealed record InvoiceData(
    string? Number,           // BT-1
    DateOnly? IssueDate,      // BT-2
    string? TypeCode,         // BT-3
    string? Currency,         // BT-5
    DateOnly? DueDate,        // BT-9
    string? BuyerReference,   // BT-10 (Leitweg-ID bei Behörden)
    string? OrderReference,   // BT-13
    Party Seller,             // BG-4
    Party Buyer,              // BG-7
    decimal? NetTotal,        // BT-109
    decimal? TaxTotal,        // BT-110
    decimal? GrossTotal,      // BT-112
    decimal? PayableAmount,   // BT-115
    string? Iban,             // BT-84
    IReadOnlyList<InvoiceLine> Lines);

public static class InvoiceExtractor
{
    public static InvoiceData Extract(XDocument doc)
    {
        var info = FormatDetector.Detect(doc);
        return info.Syntax switch
        {
            Syntax.UblInvoice => FromUbl(doc.Root!, creditNote: false),
            Syntax.UblCreditNote => FromUbl(doc.Root!, creditNote: true),
            Syntax.Cii => FromCii(doc.Root!),
            _ => throw new NotSupportedException($"Syntax nicht unterstützt: {doc.Root!.Name}")
        };
    }

    // ---------- UBL ----------
    private static InvoiceData FromUbl(XElement r, bool creditNote)
    {
        XNamespace cbc = Ns.Cbc, cac = Ns.Cac;
        string? currency = Val(r.Element(cbc + "DocumentCurrencyCode"));
        var total = r.Element(cac + "LegalMonetaryTotal");

        // BT-110: die TaxTotal in Rechnungswährung (es kann eine zweite in BT-6 geben)
        var taxTotal = r.Elements(cac + "TaxTotal")
            .Select(t => t.Element(cbc + "TaxAmount"))
            .FirstOrDefault(a => a is not null && (string?)a.Attribute("currencyID") == currency);

        string lineName = creditNote ? "CreditNoteLine" : "InvoiceLine";
        string qtyName = creditNote ? "CreditedQuantity" : "InvoicedQuantity";

        return new InvoiceData(
            Number: Val(r.Element(cbc + "ID")),
            IssueDate: IsoDate(r.Element(cbc + "IssueDate")),
            TypeCode: Val(r.Element(cbc + (creditNote ? "CreditNoteTypeCode" : "InvoiceTypeCode"))),
            Currency: currency,
            DueDate: IsoDate(r.Element(cbc + "DueDate")
                     ?? r.Element(cac + "PaymentMeans")?.Element(cbc + "PaymentDueDate")),
            BuyerReference: Val(r.Element(cbc + "BuyerReference")),
            OrderReference: Val(r.Element(cac + "OrderReference")?.Element(cbc + "ID")),
            Seller: UblParty(r.Element(cac + "AccountingSupplierParty")?.Element(cac + "Party")),
            Buyer: UblParty(r.Element(cac + "AccountingCustomerParty")?.Element(cac + "Party")),
            NetTotal: Dec(total?.Element(cbc + "TaxExclusiveAmount")),
            TaxTotal: Dec(taxTotal),
            GrossTotal: Dec(total?.Element(cbc + "TaxInclusiveAmount")),
            PayableAmount: Dec(total?.Element(cbc + "PayableAmount")),
            Iban: Val(r.Elements(cac + "PaymentMeans")
                       .Select(p => p.Element(cac + "PayeeFinancialAccount")?.Element(cbc + "ID"))
                       .FirstOrDefault(e => e is not null)),
            Lines: r.Elements(cac + lineName).Select(l => new InvoiceLine(
                Id: Val(l.Element(cbc + "ID")),
                Name: Val(l.Element(cac + "Item")?.Element(cbc + "Name")),
                Quantity: Dec(l.Element(cbc + qtyName)),
                Unit: (string?)l.Element(cbc + qtyName)?.Attribute("unitCode"),
                NetAmount: Dec(l.Element(cbc + "LineExtensionAmount")))).ToList());
    }

    private static Party UblParty(XElement? p)
    {
        XNamespace cbc = Ns.Cbc, cac = Ns.Cac;
        if (p is null) return new Party(null, null, null, null, null, null);
        var addr = p.Element(cac + "PostalAddress");
        var vat = p.Elements(cac + "PartyTaxScheme")
            .FirstOrDefault(t => (string?)t.Element(cac + "TaxScheme")?.Element(cbc + "ID") == "VAT");
        return new Party(
            Name: Val(p.Element(cac + "PartyLegalEntity")?.Element(cbc + "RegistrationName"))
                  ?? Val(p.Element(cac + "PartyName")?.Element(cbc + "Name")),
            VatId: Val(vat?.Element(cbc + "CompanyID")),
            Street: Val(addr?.Element(cbc + "StreetName")),
            PostalCode: Val(addr?.Element(cbc + "PostalZone")),
            City: Val(addr?.Element(cbc + "CityName")),
            CountryCode: Val(addr?.Element(cac + "Country")?.Element(cbc + "IdentificationCode")));
    }

    // ---------- CII ----------
    private static InvoiceData FromCii(XElement r)
    {
        XNamespace rsm = Ns.Rsm, ram = Ns.Ram, udt = Ns.Udt;
        var docEl = r.Element(rsm + "ExchangedDocument");
        var tx = r.Element(rsm + "SupplyChainTradeTransaction");
        var agreement = tx?.Element(ram + "ApplicableHeaderTradeAgreement");
        var settlement = tx?.Element(ram + "ApplicableHeaderTradeSettlement");
        var sums = settlement?.Element(ram + "SpecifiedTradeSettlementHeaderMonetarySummation");
        string? currency = Val(settlement?.Element(ram + "InvoiceCurrencyCode"));

        // BT-110: TaxTotalAmount in Rechnungswährung
        var taxTotal = sums?.Elements(ram + "TaxTotalAmount")
            .FirstOrDefault(a => (string?)a.Attribute("currencyID") == currency)
            ?? sums?.Element(ram + "TaxTotalAmount");

        return new InvoiceData(
            Number: Val(docEl?.Element(ram + "ID")),
            IssueDate: CiiDate(docEl?.Element(ram + "IssueDateTime")?.Element(udt + "DateTimeString")),
            TypeCode: Val(docEl?.Element(ram + "TypeCode")),
            Currency: currency,
            DueDate: CiiDate(settlement?.Element(ram + "SpecifiedTradePaymentTerms")?
                .Element(ram + "DueDateDateTime")?.Element(udt + "DateTimeString")),
            BuyerReference: Val(agreement?.Element(ram + "BuyerReference")),
            OrderReference: Val(agreement?.Element(ram + "BuyerOrderReferencedDocument")?
                .Element(ram + "IssuerAssignedID")),
            Seller: CiiParty(agreement?.Element(ram + "SellerTradeParty")),
            Buyer: CiiParty(agreement?.Element(ram + "BuyerTradeParty")),
            NetTotal: Dec(sums?.Element(ram + "TaxBasisTotalAmount")),
            TaxTotal: Dec(taxTotal),
            GrossTotal: Dec(sums?.Element(ram + "GrandTotalAmount")),
            PayableAmount: Dec(sums?.Element(ram + "DuePayableAmount")),
            Iban: Val(settlement?.Elements(ram + "SpecifiedTradeSettlementPaymentMeans")
                .Select(p => p.Element(ram + "PayeePartyCreditorFinancialAccount")?.Element(ram + "IBANID"))
                .FirstOrDefault(e => e is not null)),
            Lines: ReadCiiLines(tx));
    }

    private static List<InvoiceLine> ReadCiiLines(XElement? tx)
    {
        XNamespace ram = Ns.Ram;
        if (tx is null) return [];
        return tx.Elements(ram + "IncludedSupplyChainTradeLineItem")
            .Select(l =>
            {
                var qty = l.Element(ram + "SpecifiedLineTradeDelivery")?
                    .Element(ram + "BilledQuantity");
                return new InvoiceLine(
                    Id: Val(l.Element(ram + "AssociatedDocumentLineDocument")?
                        .Element(ram + "LineID")),
                    Name: Val(l.Element(ram + "SpecifiedTradeProduct")?
                        .Element(ram + "Name")),
                    Quantity: Dec(qty),
                    Unit: (string?)qty?.Attribute("unitCode"),
                    NetAmount: Dec(l.Element(ram + "SpecifiedLineTradeSettlement")?
                        .Element(ram + "SpecifiedTradeSettlementLineMonetarySummation")?
                        .Element(ram + "LineTotalAmount")));
            })
            .ToList();
    }

    private static Party CiiParty(XElement? p)
    {
        XNamespace ram = Ns.Ram;
        if (p is null) return new Party(null, null, null, null, null, null);
        var addr = p.Element(ram + "PostalTradeAddress");
        var vat = p.Elements(ram + "SpecifiedTaxRegistration")
            .Select(t => t.Element(ram + "ID"))
            .FirstOrDefault(id => (string?)id?.Attribute("schemeID") == "VA");
        return new Party(
            Name: Val(p.Element(ram + "Name")),
            VatId: Val(vat),
            Street: Val(addr?.Element(ram + "LineOne")),
            PostalCode: Val(addr?.Element(ram + "PostcodeCode")),
            City: Val(addr?.Element(ram + "CityName")),
            CountryCode: Val(addr?.Element(ram + "CountryID")));
    }

    // ---------- Hilfsfunktionen ----------
    private static string? Val(XElement? e) =>
        string.IsNullOrWhiteSpace(e?.Value) ? null : e!.Value.Trim();

    private static decimal? Dec(XElement? e) =>
        decimal.TryParse(e?.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d : null;

    private static DateOnly? IsoDate(XElement? e) =>
        DateOnly.TryParseExact(e?.Value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;

    /// <summary>CII-Datum im Format 102 = JJJJMMTT.</summary>
    private static DateOnly? CiiDate(XElement? e) =>
        DateOnly.TryParseExact(e?.Value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
}
