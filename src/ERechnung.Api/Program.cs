using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ERechnung.Api;
using ERechnung.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ---------- Konfiguration ----------
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection("Api"));
var mustangOptions = builder.Configuration.GetSection("Mustang").Get<MustangOptions>() ?? new();
string kositUrl = builder.Configuration["Kosit:BaseUrl"] ?? "http://localhost:8081";

// Uploads begrenzen (Rechnungen sind selten größer als ein paar MB).
const long MaxUploadBytes = 20 * 1024 * 1024;
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxUploadBytes);

// ---------- Dienste ----------
builder.Services.AddSingleton(mustangOptions);
builder.Services.AddSingleton<MustangCli>();
builder.Services.AddHttpClient<KositDaemonValidator>(c =>
{
    c.BaseAddress = new Uri(kositUrl);
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddTransient<InvoiceProcessor>();
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    // Bei Upload-Fehlern die verständliche Meldung mitschicken
    if (ctx.Exception is BadHttpRequestException bad)
        ctx.ProblemDetails.Detail = bad.Message;
});
// Enums als Text ausgeben ("Pdf" statt 1)
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHealthChecks()
    .AddCheck<KositHealthCheck>("kosit")
    .AddCheck<JavaHealthCheck>("java");

var app = builder.Build();
// Fehler beim Lesen des Uploads (z. B. zu groß, Feld fehlt) als 4xx statt 500 melden
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : 500
});
app.UseStatusCodePages();

app.MapHealthChecks("/health");

// ---------- Endpunkte ----------
var invoices = app.MapGroup("/api/v1/invoices")
    .AddEndpointFilter<ApiKeyFilter>()
    .DisableAntiforgery();

// Format erkennen (UBL/CII, Profil, E-Rechnung ja/nein)
invoices.MapPost("/identify", async (HttpRequest req, MustangCli mustang, CancellationToken ct) =>
{
    var upload = await Upload.ReadAsync(req, MaxUploadBytes, ct);
    var format = FormatDetector.Detect(upload.Data);
    if (format.Container == Container.Pdf)
    {
        var xml = await mustang.ExtractXmlAsync(upload.Data, ct);
        format = xml is null
            ? format with { Hint = "PDF ohne eingebettete Rechnungs-XML." }
            : FormatDetector.Detect(xml) with { Container = Container.Pdf };
    }
    return Results.Ok(format);
});

// Vollständige Prüfung inkl. Datenextraktion
invoices.MapPost("/process", async (HttpRequest req, InvoiceProcessor processor,
    ILogger<Program> log, bool? report, CancellationToken ct) =>
{
    var upload = await Upload.ReadAsync(req, MaxUploadBytes, ct);
    var r = await processor.ProcessAsync(upload.Data, upload.FileName, ct);

    // Audit-Log: wer hat was wann geprüft – ohne Rechnungsinhalt.
    log.LogInformation(
        "Rechnung verarbeitet: Datei={File} SHA256={Hash} Profil={Profile} Gültig={Valid} Fehler={Errors}",
        r.FileName, r.Sha256, r.Format.Profile, r.Validation?.IsValid, r.Validation?.ErrorCount);

    return Results.Ok(new
    {
        r.FileName,
        r.Sha256,
        r.Accepted,
        r.Format,
        Validation = r.Validation is null ? null : new
        {
            r.Validation.IsValid,
            r.Validation.Engine,
            r.Validation.ErrorCount,
            r.Validation.Messages,
            ReportHtml = report == true ? r.Validation.ReportHtml : null
        },
        r.Invoice
    });
});

// Nur Daten auslesen
invoices.MapPost("/extract", async (HttpRequest req, MustangCli mustang, CancellationToken ct) =>
{
    var upload = await Upload.ReadAsync(req, MaxUploadBytes, ct);
    byte[]? xml = FormatDetector.IsPdf(upload.Data)
        ? await mustang.ExtractXmlAsync(upload.Data, ct)
        : upload.Data;
    if (xml is null)
        return Results.Problem("Die PDF enthält keine Rechnungs-XML.", statusCode: 422);

    try
    {
        return Results.Ok(InvoiceExtractor.Extract(FormatDetector.LoadXmlSafe(xml)));
    }
    catch (Exception ex) when (ex is System.Xml.XmlException or NotSupportedException)
    {
        return Results.Problem(ex.Message, statusCode: 422);
    }
});

// Zusatzangaben und eigene Plausibilitätsprüfungen
invoices.MapPost("/details", async (HttpRequest req, MustangCli mustang, CancellationToken ct) =>
{
    var upload = await Upload.ReadAsync(req, MaxUploadBytes, ct);
    byte[]? xml = FormatDetector.IsPdf(upload.Data)
        ? await mustang.ExtractXmlAsync(upload.Data, ct)
        : upload.Data;
    if (xml is null)
        return Results.Problem("Die PDF enthält keine Rechnungs-XML.", statusCode: 422);

    var doc = FormatDetector.LoadXmlSafe(xml);
    var invoice = InvoiceExtractor.Extract(doc);
    var details = InvoiceDetailExtractor.Extract(doc);
    return Results.Ok(new
    {
        Invoice = invoice,
        Details = details with
        {
            // Inhalte der Anlagen nicht im JSON mitschicken, dafür gibt es /attachments
            Attachments = details.Attachments.Select(a => a with { Content = null }).ToList()
        },
        AttachmentSizes = details.Attachments.Select(a => a.Size).ToList(),
        Checks = Plausibility.Check(invoice)
    });
});

// Eingebettete Anlagen (BG-24) als ZIP
invoices.MapPost("/attachments", async (HttpRequest req, MustangCli mustang, CancellationToken ct) =>
{
    var upload = await Upload.ReadAsync(req, MaxUploadBytes, ct);
    byte[]? xml = FormatDetector.IsPdf(upload.Data)
        ? await mustang.ExtractXmlAsync(upload.Data, ct)
        : upload.Data;
    if (xml is null)
        return Results.Problem("Die PDF enthält keine Rechnungs-XML.", statusCode: 422);

    var attachments = InvoiceDetailExtractor.Extract(FormatDetector.LoadXmlSafe(xml))
        .Attachments.Where(a => a.Content is not null).ToList();
    if (attachments.Count == 0)
        return Results.NoContent();

    using var ms = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
    {
        int i = 0;
        foreach (var a in attachments)
        {
            // Dateinamen aus der Rechnung sind nicht vertrauenswürdig: bereinigen und eindeutig machen.
            string name = $"{++i:00}_{Path.GetFileName(a.FileName ?? a.Id ?? "anlage")}";
            var entry = zip.CreateEntry(name);
            await using var es = entry.Open();
            await es.WriteAsync(a.Content!, ct);
        }
    }
    string zipName = Path.GetFileNameWithoutExtension(upload.FileName) + "-anlagen.zip";
    return Results.File(ms.ToArray(), "application/zip", zipName);
});

// Sichtbeleg als HTML oder PDF
invoices.MapPost("/visualize", async (HttpRequest req, MustangCli mustang,
    string? format, CancellationToken ct) =>
{
    format = (format ?? "pdf").ToLowerInvariant();
    if (format is not ("pdf" or "html" or "kurz"))
        return Results.Problem("format muss pdf, html oder kurz sein.", statusCode: 400);

    var upload = await Upload.ReadAsync(req, MaxUploadBytes, ct);
    byte[]? xml = FormatDetector.IsPdf(upload.Data)
        ? await mustang.ExtractXmlAsync(upload.Data, ct)
        : upload.Data;
    if (xml is null)
        return Results.Problem("Die PDF enthält keine Rechnungs-XML.", statusCode: 422);

    // Kurzansicht aus dem eigenen Datenmodell, ohne Java
    if (format == "kurz")
    {
        var invoice = InvoiceExtractor.Extract(FormatDetector.LoadXmlSafe(xml));
        return Results.Content(ShortView.Render(invoice), "text/html; charset=utf-8");
    }

    var bytes = await mustang.VisualizeAsync(xml, format, ct);
    string name = Path.GetFileNameWithoutExtension(upload.FileName) + "-sichtbeleg." + format;
    return format == "pdf"
        ? Results.File(bytes, "application/pdf", name)
        : Results.File(bytes, "text/html; charset=utf-8", name);
});

app.Run();

namespace ERechnung.Api
{
    public sealed class ApiOptions
    {
        public string ApiKey { get; set; } = "";
    }

    /// <summary>Prüft den Header X-Api-Key in konstanter Zeit.</summary>
    public sealed class ApiKeyFilter(IOptions<ApiOptions> options) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
        {
            string expected = options.Value.ApiKey;
            string? given = ctx.HttpContext.Request.Headers["X-Api-Key"];
            if (string.IsNullOrEmpty(expected) || given is null ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given)))
            {
                return Results.Problem("Ungültiger oder fehlender API-Schlüssel.", statusCode: 401);
            }
            return await next(ctx);
        }
    }

    public sealed record UploadedFile(string FileName, byte[] Data);

    /// <summary>Akzeptiert multipart/form-data (Feld "file") oder den rohen Body.</summary>
    public static class Upload
    {
        public static async Task<UploadedFile> ReadAsync(HttpRequest req, long maxBytes, CancellationToken ct)
        {
            if (req.HasFormContentType)
            {
                var form = await req.ReadFormAsync(ct);
                var file = form.Files.GetFile("file")
                    ?? throw new BadHttpRequestException("Formularfeld 'file' fehlt.");
                if (file.Length > maxBytes) throw new BadHttpRequestException("Datei zu groß.", 413);
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                return new UploadedFile(Path.GetFileName(file.FileName), ms.ToArray());
            }

            using var body = new MemoryStream();
            await req.Body.CopyToAsync(body, ct);
            if (body.Length == 0) throw new BadHttpRequestException("Leerer Request-Body.");
            string name = req.Headers["X-File-Name"].FirstOrDefault() ?? "rechnung";
            return new UploadedFile(Path.GetFileName(name), body.ToArray());
        }
    }

    /// <summary>Ist der KoSIT-Daemon erreichbar?</summary>
    public sealed class KositHealthCheck(IConfiguration config, IHttpClientFactory factory) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        {
            try
            {
                using var http = factory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                // Der Daemon beantwortet GET mit seiner Startseite (oder 405, wenn die GUI aus ist).
                using var resp = await http.GetAsync(config["Kosit:BaseUrl"] ?? "http://localhost:8081", ct);
                return HealthCheckResult.Healthy($"HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("KoSIT-Validator nicht erreichbar", ex);
            }
        }
    }

    /// <summary>Gibt es Java und die Mustang-JAR?</summary>
    public sealed class JavaHealthCheck(MustangOptions options) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        {
            if (!File.Exists(options.JarPath))
                return HealthCheckResult.Unhealthy($"Mustang-JAR fehlt: {options.JarPath}");
            try
            {
                var r = await ExternalTool.RunAsync(options.JavaPath, ["-version"],
                    Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(10), ct);
                return r.ExitCode == 0
                    ? HealthCheckResult.Healthy(r.StdErr.Split('\n')[0].Trim())
                    : HealthCheckResult.Unhealthy("java -version fehlgeschlagen");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Java nicht startbar", ex);
            }
        }
    }
}
