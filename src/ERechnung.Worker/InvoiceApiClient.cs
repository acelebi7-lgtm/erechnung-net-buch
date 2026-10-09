using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERechnung.Worker;

/// <summary>Das, was der Worker von der API-Antwort braucht.</summary>
public sealed record ProcessResponse(
    string FileName,
    string Sha256,
    bool Accepted,
    JsonElement Format,
    JsonElement? Validation,
    JsonElement? Invoice);

public sealed class InvoiceApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ProcessResponse> ProcessAsync(byte[] data, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/xml");
        form.Add(file, "file", fileName);

        using var resp = await http.PostAsync("/api/v1/invoices/process?report=true", form, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ProcessResponse>(Json, ct))!;
    }

    public async Task<byte[]> VisualizeAsync(byte[] data, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(data), "file", fileName);
        using var resp = await http.PostAsync("/api/v1/invoices/visualize?format=pdf", form, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
