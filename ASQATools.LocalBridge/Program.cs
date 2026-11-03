using System.Net;
using System.Net.Sockets;
using ASQATools.Shared.Models;
using ASQATools.Shared.Services;

int selectedPort = 5173;
for (int p = 5173; p <= 5185; p++)
{
    try
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, p);
        tcpListener.Start();
        tcpListener.Stop();
        selectedPort = p;
        break;
    }
    catch (SocketException) { }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{selectedPort}");

builder.Services.AddScoped<CodebaseExporterService>();
builder.Services.AddScoped<CodeReflectorService>();
builder.Services.AddScoped<ReportExporterService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
    {
        context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }
    await next();
});

app.UseCors();

// Zero-config custom header guard for mutating and local filesystem endpoints
app.Use(async (context, next) =>
{
    if (HttpMethods.IsOptions(context.Request.Method) ||
        context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-ASQA-Bridge-Client", out var headerVal) ||
        string.IsNullOrWhiteSpace(headerVal))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Missing required X-ASQA-Bridge-Client security header." });
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new ClientAgentInfoDto
{
    Service = "ASQA.LocalBridge",
    Status = "Ready",
    MachineName = Environment.MachineName,
    UserName = Environment.UserName,
    OsVersion = Environment.OSVersion.VersionString,
    DotnetVersion = Environment.Version.ToString(),
    Port = selectedPort,
    Timestamp = DateTime.Now
}));

app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(200);
        lifetime.StopApplication();
    });
    return Results.Ok(new { message = "Agent is shutting down..." });
});

app.MapGet("/api/tfsbridge/default-directory", () =>
{
    string candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    if (!Directory.Exists(candidate))
    {
        candidate = Directory.GetCurrentDirectory();
    }
    return Results.Ok(new { directory = candidate });
});

app.MapPost("/api/tfsbridge/scan-tree-children", (ScanTreeChildrenRequest req, CodebaseExporterService exporter) =>
{
    try
    {
        var items = exporter.GetDirectoryChildren(req.RootPath, req.SubPath);
        return Results.Ok(items);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/tfsbridge/purge-backups", (CodebaseExportRequest req, CodebaseExporterService exporter) =>
{
    var logs = new List<string>();
    Action<string> logger = msg => logs.Add(msg);
    exporter.OnLog += logger;
    try
    {
        int deletedCount = exporter.PurgeBackups(req.RootPath);
        return Results.Ok(new OperationResponse<int>
        {
            Success = true,
            Data = deletedCount,
            Logs = logs
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new OperationResponse<int> { Success = false, Error = ex.Message, Logs = logs });
    }
    finally
    {
        exporter.OnLog -= logger;
    }
});

app.MapPost("/api/tfsbridge/export-codebase", async (CodebaseExportRequest req, CodebaseExporterService exporter) =>
{
    var logs = new List<string>();
    Action<string> logger = msg => logs.Add(msg);
    exporter.OnLog += logger;
    try
    {
        var result = await exporter.ExportProjectAsync(req);
        return Results.Ok(new OperationResponse<CodebaseExportResult>
        {
            Success = true,
            Data = result,
            Logs = logs
        });
    }
    catch (Exception ex)
    {
        logs.Add($"❌ {ex.Message}");
        return Results.Ok(new OperationResponse<CodebaseExportResult>
        {
            Success = false,
            Error = ex.Message,
            Logs = logs
        });
    }
    finally
    {
        exporter.OnLog -= logger;
    }
});

app.MapPost("/api/tfsbridge/reflector-parse", (CodeReflectorApplyRequest req, CodeReflectorService reflector) =>
{
    var parsed = reflector.ParseAiResponse(req.TargetDirectory, req.RawAiResponse);
    return Results.Ok(parsed);
});

app.MapPost("/api/tfsbridge/reflector-apply", async (CodeReflectorApplyRequest req, CodeReflectorService reflector) =>
{
    var logs = new List<string>();
    Action<string> logger = msg => logs.Add(msg);
    reflector.OnLog += logger;
    try
    {
        int count = await reflector.ApplyChangesAsync(req.TargetDirectory, req.RawAiResponse, req.CreateBackups);
        return Results.Ok(new OperationResponse<int>
        {
            Success = true,
            Data = count,
            Logs = logs
        });
    }
    catch (Exception ex)
    {
        logs.Add($"❌ {ex.Message}");
        return Results.Ok(new OperationResponse<int>
        {
            Success = false,
            Error = ex.Message,
            Logs = logs
        });
    }
    finally
    {
        reflector.OnLog -= logger;
    }
});

app.MapGet("/api/tfsbridge/reports/default-directory", () =>
{
    string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    if (!Directory.Exists(baseDir)) baseDir = Directory.GetCurrentDirectory();
    string defaultFolder = Path.Combine(baseDir, "exported_reports", $"Batch_{DateTime.Now:yyyy_MM_dd}");
    return Results.Ok(new { directory = defaultFolder });
});

app.MapPost("/api/tfsbridge/reports/export-batch", async (ReportExportRequest req, ReportExporterService reportExporter, CancellationToken ct) =>
{
    var logs = new List<string>();
    Action<string> logger = msg => logs.Add(msg);
    reportExporter.OnLog += logger;
    try
    {
        string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        if (!Directory.Exists(baseDir)) baseDir = Directory.GetCurrentDirectory();
        string defaultReportsRoot = Path.Combine(baseDir, "exported_reports");

        var result = await reportExporter.ExecuteBatchExportAsync(req, defaultReportsRoot, ct);
        return Results.Ok(new OperationResponse<ReportExportBatchResult>
        {
            Success = true,
            Data = result,
            Logs = logs
        });
    }
    catch (Exception ex)
    {
        logs.Add($"❌ {ex.Message}");
        return Results.Ok(new OperationResponse<ReportExportBatchResult>
        {
            Success = false,
            Error = ex.Message,
            Logs = logs
        });
    }
    finally
    {
        reportExporter.OnLog -= logger;
    }
});

app.MapPost("/api/tfsbridge/reports/open-folder", (OpenFolderRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.FilePath) || !Directory.Exists(req.FilePath))
    {
        return Results.BadRequest(new { error = "Target folder does not exist." });
    }

    try
    {
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{req.FilePath}\"",
                UseShellExecute = true
            });
        }
        else if (OperatingSystem.IsLinux())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{req.FilePath}\"",
                UseShellExecute = false
            });
        }
        return Results.Ok(new { opened = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

Console.WriteLine("============================================================");
Console.WriteLine($" 🚀 ASQA Local Desktop Agent listening on: http://127.0.0.1:{selectedPort}");
Console.WriteLine($" 👤 Workstation: {Environment.MachineName} | User: {Environment.UserName}");
Console.WriteLine($" 💻 OS: {Environment.OSVersion} | .NET: {Environment.Version}");
Console.WriteLine("============================================================");

app.Run();
