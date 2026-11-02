using System.Net.Http.Json;
using ASQATools.Shared.Models;

namespace ASQATools.Client.Services;

public class ClientBridgeService : IDisposable
{
    private readonly HttpClient _http;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public ClientAgentInfoDto? ConnectedAgent { get; private set; }
    public bool IsConnected => ConnectedAgent != null;
    public bool IsConnecting { get; private set; }
    public int CurrentPort { get; private set; } = 5173;

    public event Action? OnChange;
    public event Action<string>? OnLog;

    public ClientBridgeService(HttpClient http)
    {
        _http = http;
    }

    public void StartHeartbeat()
    {
        if (_timer != null) return;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(2.5));

        _ = Task.Run(async () =>
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                if (IsConnected)
                {
                    try
                    {
                        using var pingCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
                        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1000) };
                        var res = await client.GetFromJsonAsync<ClientAgentInfoDto>($"http://127.0.0.1:{CurrentPort}/health", pingCts.Token);
                        if (res == null || res.Service != "ASQA.LocalBridge")
                        {
                            HandleDisconnect();
                        }
                    }
                    catch
                    {
                        HandleDisconnect();
                    }
                }
            }
        });
    }

    private void HandleDisconnect()
    {
        if (ConnectedAgent != null)
        {
            ConnectedAgent = null;
            OnLog?.Invoke($"ℹ [DESKTOP BRIDGE] Agent stopped or disconnected from port {CurrentPort}.");
            Notify();
        }
    }

    public async Task<bool> ConnectAsync(int? targetPort = null)
    {
        if (IsConnecting) return false;
        IsConnecting = true;
        Notify();

        try
        {
            if (targetPort.HasValue)
            {
                if (await ProbePortAsync(targetPort.Value)) return true;
            }
            else
            {
                if (await ProbePortAsync(5173)) return true;
                for (int p = 5174; p <= 5185; p++)
                {
                    if (await ProbePortAsync(p)) return true;
                }
            }
        }
        finally
        {
            IsConnecting = false;
            Notify();
        }

        return false;
    }

    private async Task<bool> ProbePortAsync(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1000) };
            var res = await client.GetFromJsonAsync<ClientAgentInfoDto>($"http://127.0.0.1:{port}/health", cts.Token);
            if (res != null && res.Service == "ASQA.LocalBridge")
            {
                CurrentPort = res.Port;
                ConnectedAgent = res;
                OnLog?.Invoke($"✔ [DESKTOP BRIDGE] Connected on port {res.Port} | Workstation: '{res.MachineName}' | User: '{res.UserName}' | OS: '{res.OsVersion}' | .NET Runtime: '{res.DotnetVersion}'");
                Notify();
                return true;
            }
        }
        catch { }
        return false;
    }

    public async Task StopAgentAsync()
    {
        if (!IsConnected) return;
        int port = CurrentPort;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
            await client.PostAsync($"http://127.0.0.1:{port}/shutdown", null);
        }
        catch { }

        ConnectedAgent = null;
        OnLog?.Invoke($"🛑 [DESKTOP BRIDGE] Agent shutdown command sent to port {port}.");
        Notify();
    }

    public void Disconnect()
    {
        ConnectedAgent = null;
        OnLog?.Invoke("ℹ [DESKTOP BRIDGE] Disconnected from local agent.");
        Notify();
    }

    private void Notify() => OnChange?.Invoke();

    public void Dispose()
    {
        _cts?.Cancel();
        _timer?.Dispose();
    }
}
