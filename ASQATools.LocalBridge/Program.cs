using System.Net;
using System.Net.Sockets;

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

app.MapGet("/health", () => Results.Ok(new
{
    service = "ASQA.LocalBridge",
    status = "Ready",
    machineName = Environment.MachineName,
    userName = Environment.UserName,
    osVersion = Environment.OSVersion.VersionString,
    dotnetVersion = Environment.Version.ToString(),
    port = selectedPort,
    timestamp = DateTime.Now
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

Console.WriteLine("============================================================");
Console.WriteLine($" 🚀 ASQA Local Desktop Agent listening on: http://127.0.0.1:{selectedPort}");
Console.WriteLine($" 👤 Workstation: {Environment.MachineName} | User: {Environment.UserName}");
Console.WriteLine($" 💻 OS: {Environment.OSVersion} | .NET: {Environment.Version}");
Console.WriteLine("============================================================");

app.Run();
