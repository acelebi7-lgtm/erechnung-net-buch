using System.Globalization;
using System.Net;
using System.Text;

namespace ERechnung.Core;

/// <summary>
/// Kompakte HTML-Kurzansicht aus dem eigenen Datenmodell, z. B. für eine Freigabemaske.
/// Ersetzt nicht den vollständigen Sichtbeleg.
/// </summary>
public static class ShortView
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "–");
    private static string Money(decimal? d, string? cur) => d is null ? "–" : $"{d.Value.ToString("N2", De)} {H(cur)}";
    private static string Date(DateOnly? d) => d?.ToString("dd.MM.yyyy", De) ?? "–";

    public static string Render(InvoiceData i, ValidationResult? v = null)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="de"><head><meta charset="utf-8">
            <title>Rechnung</title>
            <style>
              body{font-family:system-ui,sans-serif;margin:2rem;color:#222;max-width:52rem}
              h1{font-size:1.4rem;margin:0 0 .2rem} .muted{color:#666}
              .ok{color:#1b7f3b} .err{color:#b3261e}
              table{border-collapse:collapse;width:100%;margin-top:1rem}
              th,td{border-bottom:1px solid #ddd;padding:.35rem .5rem;text-align:left}
              td.num,th.num{text-align:right}
              .grid{display:grid;grid-template-columns:1fr 1fr;gap:1rem;margin-top:1rem}
            </style></head><body>
            """);
        sb.Append($"<h1>Rechnung {H(i.Number)}</h1>");
        sb.Append($"<div class=\"muted\">vom {Date(i.IssueDate)} · Typ {H(i.TypeCode)} · fällig {Date(i.DueDate)}</div>");

        if (v is not null)
            sb.Append(v.IsValid
                ? "<p class=\"ok\">✔ Prüfung bestanden</p>"
                : $"<p class=\"err\">✘ Prüfung nicht bestanden ({v.ErrorCount} Fehler)</p>");

        sb.Append("<div class=\"grid\">");
        foreach (var (title, p) in new[] { ("Verkäufer", i.Seller), ("Käufer", i.Buyer) })
            sb.Append($"<div><strong>{title}</strong><br>{H(p.Name)}<br>{H(p.Street)}<br>" +
                      $"{H(p.PostalCode)} {H(p.City)} {H(p.CountryCode)}<br>" +
                      $"<span class=\"muted\">USt-ID {H(p.VatId)}</span></div>");
        sb.Append("</div>");

        sb.Append("<table><tr><th>Pos.</th><th>Bezeichnung</th><th class=\"num\">Menge</th><th class=\"num\">Netto</th></tr>");
        foreach (var l in i.Lines)
            sb.Append($"<tr><td>{H(l.Id)}</td><td>{H(l.Name)}</td>" +
                      $"<td class=\"num\">{l.Quantity?.ToString("0.###", De) ?? "–"} {H(l.Unit)}</td>" +
                      $"<td class=\"num\">{Money(l.NetAmount, i.Currency)}</td></tr>");
        sb.Append("</table>");

        sb.Append("<table>");
        sb.Append($"<tr><td>Summe netto</td><td class=\"num\">{Money(i.NetTotal, i.Currency)}</td></tr>");
        sb.Append($"<tr><td>Umsatzsteuer</td><td class=\"num\">{Money(i.TaxTotal, i.Currency)}</td></tr>");
        sb.Append($"<tr><td><strong>Zahlbetrag</strong></td><td class=\"num\"><strong>{Money(i.PayableAmount, i.Currency)}</strong></td></tr>");
        sb.Append("</table>");

        sb.Append($"<p class=\"muted\">IBAN {H(i.Iban)} · Käuferreferenz {H(i.BuyerReference)} · Bestellung {H(i.OrderReference)}</p>");
        sb.Append("<p class=\"muted\">Kurzansicht – maßgeblich ist die strukturierte Rechnung (XML).</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
