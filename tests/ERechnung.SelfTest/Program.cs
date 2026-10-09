// Regressionstest ohne Test-Framework:
//   dotnet run -- <Ordner mit Testrechnungen> [<Ordner für Referenzdateien>] [--kosit http://localhost:8081]
// Beim ersten Lauf werden Referenzdateien (*.expected.json) erzeugt, danach wird verglichen.
using System.Text.Json;
using System.Text.Json.Serialization;
using ERechnung.Core;

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? " OK " : "FAIL")}] {what}");
    if (!ok) failures++;
}

// ---------- 1. Einheitentests für die Plausibilitätsprüfungen ----------
Console.WriteLine("Plausibilität");
Check(Plausibility.IsValidLeitwegId("04011000-12345-03"), "Leitweg-ID mit korrekter Prüfziffer");
Check(Plausibility.IsValidLeitwegId("991-33333TEST-33"), "Leitweg-ID mit Buchstaben");
Check(!Plausibility.IsValidLeitwegId("04011000-12345-34"), "Leitweg-ID mit falscher Prüfziffer");
Check(Plausibility.IsValidIban("DE89 3704 0044 0532 0130 00"), "gültige IBAN");
Check(!Plausibility.IsValidIban("DE89 3704 0044 0532 0130 01"), "IBAN mit Zahlendreher");
Check(FormatDetector.ClassifyProfile("urn:factur-x.eu:1p0:basicwl") == ("BASIC-WL", false),
    "BASIC WL ist keine E-Rechnung");

// ---------- 1b. Kreditorzuordnung und Buchungsvorschlag ----------
Console.WriteLine("Integration");
var beispiel = FormatDetector.LoadXmlSafe(
    await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "RE-2026-0815_ubl.xml")));
var inv0 = InvoiceExtractor.Extract(beispiel);
var det0 = InvoiceDetailExtractor.Extract(beispiel);
var kreditoren = new[]
{
    new Supplier("70001", "Muster Bürobedarf GmbH", "DE 123 456 789",
        new HashSet<string> { "DE89370400440532013000" }, "4930"),
    new Supplier("70002", "Andere GmbH", "DE999999999", new HashSet<string>(), "4900")
};
var match = new SupplierMatcher(kreditoren).Match(inv0);
Check(match.Supplier?.Id == "70001" && match.Method == "USt-ID" && match.IbanKnown,
    "Kreditor über USt-ID gefunden, IBAN bekannt");
var vorschlag = BookingProposalBuilder.Build(inv0, det0, match.Supplier!,
    new Dictionary<decimal, string> { [19m] = "1576", [7m] = "1571" });
Check(vorschlag.Lines.Sum(l => l.Debit) == 267.51m && vorschlag.Lines.Sum(l => l.Credit) == 267.51m
      && vorschlag.Hints.Count == 0, "Buchungsvorschlag ausgeglichen");
Console.WriteLine(JsonSerializer.Serialize(vorschlag, json));

// ---------- 1c. Gutschrift ----------
Console.WriteLine("Gutschrift");
foreach (var name in new[] { "GS-2026-0042_ubl.xml", "GS-2026-0042_cii.xml" })
{
    var gsDoc = FormatDetector.LoadXmlSafe(
        await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, name)));
    var gsFormat = FormatDetector.Detect(gsDoc);
    var gs = InvoiceExtractor.Extract(gsDoc);
    var gsDet = InvoiceDetailExtractor.Extract(gsDoc);
    Check(gs.TypeCode == "381" && gs.PayableAmount == 106.98m && gs.Lines.Count == 1
          && gsDet.PrecedingInvoices.SequenceEqual(["RE-2026-0815"]),
        $"{name}: Typ 381, 106,98 €, Verweis auf RE-2026-0815 ({gsFormat.Syntax})");
    var gsVorschlag = BookingProposalBuilder.Build(gs, gsDet, match.Supplier!,
        new Dictionary<decimal, string> { [19m] = "1576" });
    Check(gsVorschlag.Lines.Single(l => l.Account.StartsWith("Kreditor")).Debit == 106.98m
          && gsVorschlag.Lines.Single(l => l.Account == "1576").Credit == 17.08m
          && gsVorschlag.Hints.Count == 0,
        $"{name}: Buchung umgekehrt (Kreditor im Soll)");
    if (name.EndsWith("_ubl.xml")) Console.WriteLine(JsonSerializer.Serialize(gsVorschlag, json));
}

// ---------- 2. Regressionstest über eine Sammlung von Rechnungen ----------
if (args.Length == 0)
{
    Console.WriteLine("\nKein Testordner angegeben – nur Einheitentests ausgeführt.");
    return failures == 0 ? 0 : 1;
}

string inputDir = args[0];
string expectedDir = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : inputDir;
string? kositUrl = args.SkipWhile(a => a != "--kosit").Skip(1).FirstOrDefault();
KositDaemonValidator? kosit = kositUrl is null ? null
    : new KositDaemonValidator(new HttpClient { BaseAddress = new Uri(kositUrl) });

Console.WriteLine($"\nRegressionstest: {inputDir}");
foreach (var file in Directory.EnumerateFiles(inputDir, "*.xml", SearchOption.AllDirectories).Order())
{
    byte[] data = await File.ReadAllBytesAsync(file);
    var format = FormatDetector.Detect(data);
    var doc = FormatDetector.LoadXmlSafe(data);
    var invoice = InvoiceExtractor.Extract(doc);
    var details = InvoiceDetailExtractor.Extract(doc);
    var validation = kosit is null ? null : await kosit.ValidateAsync(data, Path.GetFileName(file));

    // Nur stabile, fachlich relevante Werte vergleichen (keine Zeitstempel o. Ä.).
    var snapshot = new
    {
        format.Syntax, format.Profile, format.IsEInvoice,
        invoice.Number, invoice.IssueDate, invoice.Currency,
        invoice.NetTotal, invoice.TaxTotal, invoice.GrossTotal, invoice.PayableAmount,
        Lines = invoice.Lines.Count,
        Vat = details.Vat.Count,
        Attachments = details.Attachments.Count,
        Valid = validation?.IsValid,
        Errors = validation?.Messages.Where(m => m.Severity == Severity.Error).Select(m => m.Code).Order().ToList()
    };
    string actual = JsonSerializer.Serialize(snapshot, json);

    string rel = Path.GetRelativePath(inputDir, file);
    string expectedFile = Path.Combine(expectedDir, Path.ChangeExtension(rel, ".expected.json"));
    if (!File.Exists(expectedFile))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(expectedFile)!);
        await File.WriteAllTextAsync(expectedFile, actual);
        Console.WriteLine($"  [NEU ] {rel}");
        continue;
    }
    Check(actual == await File.ReadAllTextAsync(expectedFile), rel);
}

Console.WriteLine(failures == 0 ? "\nAlle Prüfungen bestanden." : $"\n{failures} Prüfung(en) fehlgeschlagen.");
return failures == 0 ? 0 : 1;
