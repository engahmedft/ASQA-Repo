using System.Net.Http.Json;
using ASQATools.Shared.Models;

namespace ASQATools.Client.Services;

public class ClientBridgeService : IDisposable
{
    private const string BridgeSecurityHeader = "X-ASQA-Bridge-Client";
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
                        var res = await FetchHealthAsync(CurrentPort, timeoutMs: 1000);
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
                // Fast check default port 5173 first
                if (await ProbePortAsync(5173, timeoutMs: 600)) return true;

                // Probe remaining ports 5174..5185 concurrently so offline discovery completes in <1s
                var ports = Enumerable.Range(5174, 12);
                var probeTasks = ports.Select(p => FetchHealthAsync(p, timeoutMs: 800)).ToArray();
                var results = await Task.WhenAll(probeTasks);

                var match = results.FirstOrDefault(r => r != null && r.Service == "ASQA.LocalBridge");
                if (match != null)
                {
                    SetConnected(match);
                    return true;
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

    private async Task<bool> ProbePortAsync(int port, int timeoutMs = 900)
    {
        var res = await FetchHealthAsync(port, timeoutMs);
        if (res != null && res.Service == "ASQA.LocalBridge")
        {
            SetConnected(res);
            return true;
        }
        return false;
    }

    private void SetConnected(ClientAgentInfoDto res)
    {
        CurrentPort = res.Port;
        ConnectedAgent = res;
        OnLog?.Invoke($"✔ [DESKTOP BRIDGE] Connected on port {res.Port} | Workstation: '{res.MachineName}' | User: '{res.UserName}' | OS: '{res.OsVersion}' | .NET Runtime: '{res.DotnetVersion}'");
        Notify();
    }

    private async Task<ClientAgentInfoDto?> FetchHealthAsync(int port, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/health");
            req.Headers.TryAddWithoutValidation(BridgeSecurityHeader, "1");
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<ClientAgentInfoDto>(cancellationToken: cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Executes a GET request against LocalBridge first when connected, automatically falling back to the Server on error or when offline.
    /// </summary>
    public async Task<TResponse?> GetWithFallbackAsync<TResponse>(string relativeEndpoint, bool silentFallback = false)
    {
        string cleanPath = relativeEndpoint.TrimStart('/');

        if (IsConnected)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{CurrentPort}/{cleanPath}");
                req.Headers.TryAddWithoutValidation(BridgeSecurityHeader, "1");
                using var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadFromJsonAsync<TResponse>();
                }
                OnLog?.Invoke($"⚠️ [BRIDGE FALLBACK] Local Agent returned HTTP {(int)resp.StatusCode} for '{cleanPath}'. Falling back to Server...");
            }
            catch (Exception ex)
            {
                HandleDisconnect();
                OnLog?.Invoke($"⚠️ [BRIDGE FALLBACK] Local Agent unreachable for '{cleanPath}' ({ex.Message}). Falling back to Server...");
            }
        }
        else if (!silentFallback)
        {
            OnLog?.Invoke($"🌐 [SERVER FALLBACK] Desktop Bridge is offline — executing '{cleanPath}' on Server.");
        }

        return await _http.GetFromJsonAsync<TResponse>(cleanPath);
    }

    /// <summary>
    /// Executes a POST request against LocalBridge first when connected, automatically falling back to the Server on error or when offline.
    /// </summary>
    public async Task<HttpResponseMessage> PostWithFallbackAsync<TRequest>(string relativeEndpoint, TRequest payload, bool silentFallback = false)
    {
        string cleanPath = relativeEndpoint.TrimStart('/');

        if (IsConnected)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{CurrentPort}/{cleanPath}")
                {
                    Content = JsonContent.Create(payload)
                };
                req.Headers.TryAddWithoutValidation(BridgeSecurityHeader, "1");

                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    return resp;
                }

                resp.Dispose();
                OnLog?.Invoke($"⚠️ [BRIDGE FALLBACK] Local Agent returned HTTP {(int)resp.StatusCode} for '{cleanPath}'. Retrying on Server...");
            }
            catch (Exception ex)
            {
                HandleDisconnect();
                OnLog?.Invoke($"⚠️ [BRIDGE FALLBACK] Local Agent failed on '{cleanPath}' ({ex.Message}). Retrying on Server...");
            }
        }
        else if (!silentFallback)
        {
            OnLog?.Invoke($"🌐 [SERVER FALLBACK] Desktop Bridge is offline — executing '{cleanPath}' on Server.");
        }

        return await _http.PostAsJsonAsync(cleanPath, payload);
    }

    public async Task StopAgentAsync()
    {
        if (!IsConnected) return;
        int port = CurrentPort;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/shutdown");
            req.Headers.TryAddWithoutValidation(BridgeSecurityHeader, "1");
            await _http.SendAsync(req, cts.Token);
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
