using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ASQATools.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Registers HttpClient for the WebAssembly client to call the Server API
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ClientBridgeService>();

await builder.Build().RunAsync();
