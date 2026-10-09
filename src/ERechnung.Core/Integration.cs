namespace ERechnung.Core;

/// <summary>Ein Kreditor aus den Stammdaten der Buchhaltung (vereinfacht).</summary>
public sealed record Supplier(
    string Id, string Name, string? VatId, IReadOnlySet<string> Ibans, string DefaultExpenseAccount);

public sealed record SupplierMatch(Supplier? Supplier, string Method, bool IbanKnown);

/// <summary>Ordnet den Verkäufer einer Rechnung einem Kreditor zu.</summary>
public sealed class SupplierMatcher(IEnumerable<Supplier> suppliers)
{
    private readonly List<Supplier> _all = suppliers.ToList();

    private static string Norm(string? s) =>
        new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public SupplierMatch Match(InvoiceData invoice)
    {
        string iban = Norm(invoice.Iban);

        // 1. Über die USt-ID: eindeutig und stabil
        string vat = Norm(invoice.Seller.VatId);
        if (vat.Length > 0 && _all.FirstOrDefault(s => Norm(s.VatId) == vat) is { } byVat)
            return new(byVat, "USt-ID", byVat.Ibans.Select(Norm).Contains(iban));

        // 2. Über die IBAN: brauchbar, aber eine IBAN kann sich ändern
        if (iban.Length > 0 && _all.FirstOrDefault(s => s.Ibans.Select(Norm).Contains(iban)) is { } byIban)
            return new(byIban, "IBAN", true);

        // 3. Über den Namen: nur als Vorschlag für die manuelle Zuordnung
        string name = Norm(invoice.Seller.Name);
        if (name.Length > 0 && _all.FirstOrDefault(s => Norm(s.Name) == name) is { } byName)
            return new(byName, "Name (bitte prüfen)", byName.Ibans.Select(Norm).Contains(iban));

        return new(null, "keine Zuordnung", false);
    }
}

public sealed record BookingLine(string Account, string Text, decimal Debit, decimal Credit, string? TaxCode);

public sealed record BookingProposal(
    string SupplierId, string InvoiceNumber, DateOnly? InvoiceDate, DateOnly? DueDate,
    decimal Gross, IReadOnlyList<BookingLine> Lines, IReadOnlyList<string> Hints);

/// <summary>
/// Erzeugt einen einfachen Buchungsvorschlag je Steuersatz:
/// Aufwand und Vorsteuer im Soll, Kreditor im Haben.
/// Kontennummern kommen aus der Konfiguration, nicht aus dem Code.
/// </summary>
public static class BookingProposalBuilder
{
    public static BookingProposal Build(InvoiceData invoice, InvoiceDetails details, Supplier supplier,
        IReadOnlyDictionary<decimal, string> inputTaxAccountByRate)
    {
        var lines = new List<BookingLine>();
        var hints = new List<string>();
        string text = $"{supplier.Name} {invoice.Number}";
        bool isCreditNote = invoice.TypeCode is "381";

        foreach (var vat in details.Vat)
        {
            decimal net = vat.TaxableAmount ?? 0, tax = vat.TaxAmount ?? 0;
            lines.Add(new(supplier.DefaultExpenseAccount, text, net, 0, $"{vat.Category}{vat.Rate:0.##}"));
            if (tax != 0)
            {
                if (vat.Rate is { } rate && inputTaxAccountByRate.TryGetValue(rate, out var taxAccount))
                    lines.Add(new(taxAccount, $"Vorsteuer {rate:0.##} %", tax, 0, null));
                else
                    hints.Add($"Kein Vorsteuerkonto für Steuersatz {vat.Rate} % konfiguriert.");
            }
        }

        decimal gross = invoice.GrossTotal ?? lines.Sum(l => l.Debit);
        lines.Add(new($"Kreditor {supplier.Id}", text, 0, gross, null));

        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
            hints.Add("Soll und Haben stimmen nicht überein (Nachlässe/Zuschläge oder Rundung prüfen).");
        if (details.Vat.Any(v => v.Category is "AE"))
            hints.Add("Reverse Charge: Umsatzsteuer schuldet der Leistungsempfänger – Steuerbuchung prüfen.");

        // Gutschrift: Beträge sind positiv, die Buchung läuft umgekehrt
        // (Kreditor im Soll, Aufwand und Vorsteuer im Haben).
        if (isCreditNote)
        {
            lines = lines.Select(l => l with { Debit = l.Credit, Credit = l.Debit }).ToList();
            if (details.PrecedingInvoices.Count == 0)
                hints.Add("Gutschrift ohne Verweis auf die ursprüngliche Rechnung (BG-3) – Zuordnung manuell prüfen.");
        }

        return new BookingProposal(supplier.Id, invoice.Number ?? "?", invoice.IssueDate, invoice.DueDate,
            gross, lines, hints);
    }
}
