// Fehlerstatistik über die Ergebnisdateien des Workers:
//   dotnet run -- <Ablageordner, z. B. data/abgelehnt>
using System.Text.Json;

string root = args.Length > 0 ? args[0] : "data/abgelehnt";
var byCode = new Dictionary<string, int>();
var bySeller = new Dictionary<string, int>();
int files = 0;

foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
{
    using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
    var r = doc.RootElement;
    files++;

    string seller = r.TryGetProperty("Invoice", out var inv) && inv.ValueKind == JsonValueKind.Object
        && inv.TryGetProperty("seller", out var s) && s.TryGetProperty("name", out var n)
        ? n.GetString() ?? "?" : "?";
    bySeller[seller] = bySeller.GetValueOrDefault(seller) + 1;

    if (r.TryGetProperty("Validation", out var v) && v.ValueKind == JsonValueKind.Object
        && v.TryGetProperty("messages", out var msgs))
        foreach (var m in msgs.EnumerateArray())
            if (m.GetProperty("severity").GetString() == "Error")
            {
                string code = m.GetProperty("code").GetString() ?? "?";
                byCode[code] = byCode.GetValueOrDefault(code) + 1;
            }
}

Console.WriteLine($"{files} abgelehnte Rechnungen\n\nHäufigste Fehler:");
foreach (var (code, count) in byCode.OrderByDescending(x => x.Value).Take(10))
    Console.WriteLine($"  {count,5}  {code}");
Console.WriteLine("\nLieferanten mit den meisten Ablehnungen:");
foreach (var (seller, count) in bySeller.OrderByDescending(x => x.Value).Take(10))
    Console.WriteLine($"  {count,5}  {seller}");
