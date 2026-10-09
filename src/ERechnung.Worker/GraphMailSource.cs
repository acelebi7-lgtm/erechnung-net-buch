using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ERechnung.Worker;

public sealed class GraphOptions
{
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";   // besser: Zertifikat oder Secret-Store
    public string Mailbox { get; set; } = "rechnungseingang@example.com";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed record MailAttachment(string MessageId, string FileName, byte[] Content);

/// <summary>
/// Liest Rechnungsanhänge aus einem Exchange-Online-Postfach über Microsoft Graph.
/// Benötigt die Anwendungsberechtigung Mail.ReadWrite (am besten per
/// Application Access Policy auf das eine Postfach beschränkt).
/// </summary>
public sealed class GraphMailSource(HttpClient http, IOptions<GraphOptions> options)
{
    private readonly GraphOptions _o = options.Value;
    private string? _token;
    private DateTimeOffset _tokenExpires;

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires.AddMinutes(-5)) return;

        using var resp = await http.PostAsync(
            $"https://login.microsoftonline.com/{_o.TenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _o.ClientId,
                ["client_secret"] = _o.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            }), ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        _token = json.GetProperty("access_token").GetString();
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32());
    }

    private async Task<JsonElement> GetAsync(string url, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>Ungelesene Mails mit Anhang → XML- und PDF-Anhänge.</summary>
    public async IAsyncEnumerable<MailAttachment> GetNewAttachmentsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string box = Uri.EscapeDataString(_o.Mailbox);
        string url = $"https://graph.microsoft.com/v1.0/users/{box}/mailFolders/inbox/messages" +
                     "?$filter=isRead eq false and hasAttachments eq true&$select=id,subject&$top=25";

        var messages = await GetAsync(url, ct);
        foreach (var msg in messages.GetProperty("value").EnumerateArray())
        {
            string id = msg.GetProperty("id").GetString()!;
            var atts = await GetAsync(
                $"https://graph.microsoft.com/v1.0/users/{box}/messages/{id}/attachments", ct);

            foreach (var a in atts.GetProperty("value").EnumerateArray())
            {
                if (a.GetProperty("@odata.type").GetString() != "#microsoft.graph.fileAttachment") continue;
                string name = a.GetProperty("name").GetString() ?? "anhang";
                if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

                byte[] bytes = Convert.FromBase64String(a.GetProperty("contentBytes").GetString()!);
                yield return new MailAttachment(id, name, bytes);
            }
        }
    }

    public async Task MarkAsReadAsync(string messageId, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        string box = Uri.EscapeDataString(_o.Mailbox);
        using var req = new HttpRequestMessage(HttpMethod.Patch,
            $"https://graph.microsoft.com/v1.0/users/{box}/messages/{messageId}")
        {
            Content = JsonContent.Create(new { isRead = true })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}

/// <summary>Holt Anhänge aus dem Postfach und legt sie in den Eingangsordner.</summary>
public sealed class MailInboxWorker(
    GraphMailSource mail, IOptions<GraphOptions> graph, IOptions<InboxOptions> inbox,
    ILogger<MailInboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(graph.Value.PollInterval);
        do
        {
            try
            {
                var done = new HashSet<string>();
                await foreach (var att in mail.GetNewAttachmentsAsync(stoppingToken))
                {
                    string file = Path.Combine(inbox.Value.InboxPath,
                        $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(att.FileName)}");
                    await File.WriteAllBytesAsync(file, att.Content, stoppingToken);
                    done.Add(att.MessageId);
                    log.LogInformation("Anhang {Name} abgelegt", att.FileName);
                }
                foreach (var id in done) await mail.MarkAsReadAsync(id, stoppingToken);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "Postfachabruf fehlgeschlagen, nächster Versuch beim nächsten Intervall");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
