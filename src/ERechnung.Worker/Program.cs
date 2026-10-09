using ERechnung.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<InboxOptions>(builder.Configuration.GetSection("Inbox"));
builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection("Graph"));
builder.Services.Configure<KositHostOptions>(builder.Configuration.GetSection("KositHost"));

// HTTP-Client für die eigene E-Rechnungs-API
builder.Services.AddHttpClient<InvoiceApiClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5080");
    c.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["Api:ApiKey"]);
    c.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddHttpClient<GraphMailSource>();

// Optional: den KoSIT-Daemon im selben Dienst betreiben
if (builder.Configuration.GetValue<bool>("KositHost:Enabled"))
    builder.Services.AddHostedService<JavaServiceHost>();

builder.Services.AddHostedService<FolderInboxWorker>();
if (builder.Configuration.GetValue<bool>("Graph:Enabled"))
    builder.Services.AddHostedService<MailInboxWorker>();

// Unter Windows als Dienst laufen lassen (NuGet: Microsoft.Extensions.Hosting.WindowsServices):
// builder.Services.AddWindowsService(o => o.ServiceName = "ERechnung-Eingang");

builder.Build().Run();
