using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace ERechnung.Core;

/// <summary>
/// Eigene Plausibilitätsprüfungen, die über die Schematron-Regeln hinausgehen.
/// Sie ersetzen die Validierung nicht, sondern ergänzen sie um hausinterne Regeln.
/// </summary>
public static partial class Plausibility
{
    // Leitweg-ID: Grobadressierung (2–12 Ziffern), optional Feinadressierung
    // (bis 30 Zeichen A–Z/0–9), Prüfziffer (2 Ziffern).
    [GeneratedRegex(@"^\d{2,12}(-[A-Za-z0-9]{1,30})?-\d{2}$")]
    private static partial Regex LeitwegPattern();

    public static bool LooksLikeLeitwegId(string? value) =>
        value is not null && LeitwegPattern().IsMatch(value.Trim());

    /// <summary>Prüfziffer nach ISO/IEC 7064, MOD 97-10.</summary>
    public static bool IsValidLeitwegId(string value)
    {
        value = value.Trim();
        int last = value.LastIndexOf('-');
        if (last < 0) return false;
        string body = value[..last].Replace("-", "");
        string check = value[(last + 1)..];
        int expected = 98 - (int)(BigInteger.Parse(ToDigits(body) + "00") % 97);
        return check == expected.ToString("00");
    }

    /// <summary>IBAN-Prüfsumme nach ISO 13616 (Rest 1 bei Division durch 97).</summary>
    public static bool IsValidIban(string iban)
    {
        string s = iban.Replace(" ", "").ToUpperInvariant();
        if (s.Length < 15 || s.Length > 34 || !s.All(char.IsLetterOrDigit)) return false;
        string rearranged = s[4..] + s[..4];
        return BigInteger.Parse(ToDigits(rearranged)) % 97 == 1;
    }

    /// <summary>Buchstaben in Zahlen umwandeln: A=10, B=11, … Z=35.</summary>
    private static string ToDigits(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToUpperInvariant())
            sb.Append(char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString());
        return sb.ToString();
    }

    /// <summary>Alle Plausibilitätsprüfungen auf einmal.</summary>
    public static IReadOnlyList<ValidationMessage> Check(InvoiceData invoice, ISet<string>? knownIbans = null)
    {
        var messages = new List<ValidationMessage>();

        if (LooksLikeLeitwegId(invoice.BuyerReference) && !IsValidLeitwegId(invoice.BuyerReference!))
            messages.Add(new(Severity.Warning, "PLAUSI-LEITWEG",
                $"Die Käuferreferenz {invoice.BuyerReference} sieht aus wie eine Leitweg-ID, "
                + "die Prüfziffer stimmt aber nicht.", "BT-10"));

        if (invoice.Iban is { } iban)
        {
            if (!IsValidIban(iban))
                messages.Add(new(Severity.Error, "PLAUSI-IBAN",
                    $"Die IBAN {iban} hat eine ungültige Prüfsumme.", "BT-84"));
            // Bei Gutschriften (381) ist BT-84 das Konto des Käufers, nicht des Lieferanten.
            else if (knownIbans is not null && invoice.TypeCode != "381"
                     && !knownIbans.Contains(iban.Replace(" ", "")))
                messages.Add(new(Severity.Warning, "PLAUSI-IBAN-UNBEKANNT",
                    "Die IBAN ist für diesen Lieferanten nicht hinterlegt. Bitte manuell prüfen.", "BT-84"));
        }

        if (invoice is { NetTotal: { } net, TaxTotal: { } tax, GrossTotal: { } gross }
            && net + tax != gross)
            messages.Add(new(Severity.Error, "PLAUSI-SUMME",
                $"Netto {net} + Steuer {tax} ergibt nicht Brutto {gross}.", "BT-109/110/112"));

        return messages;
    }
}
