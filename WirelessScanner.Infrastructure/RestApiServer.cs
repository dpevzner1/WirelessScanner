using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

public class RestApiServer : IRestApiServer
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly IDbContextFactory<WlanDbContext> _contextFactory;
    private readonly IAppLogger _logger;

    private int _port = 5005;
    private string _apiKey = string.Empty;
    private bool _isRunning;
    
    private readonly object _liveLock = new();
    private List<AccessPoint> _liveAccessPoints = new();

    public bool IsRunning => _isRunning;
    public int Port => _port;
    public string ApiKey => _apiKey;

    public RestApiServer(
        IDbContextFactory<WlanDbContext> contextFactory,
        IAppLogger logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task StartAsync(int port, string apiKey)
    {
        if (_isRunning)
        {
            await StopAsync();
        }

        _port = port;
        _apiKey = apiKey;
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");

        try
        {
            _listener.Start();
            _isRunning = true;
            _logger.Info($"REST API Server started on http://localhost:{_port}/");
            
            _ = AcceptConnectionsAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to start REST API Server on port {_port}: {ex.Message}", ex);
            _isRunning = false;
            _listener = null;
            throw;
        }
    }

    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;

        _isRunning = false;
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception ex)
        {
            _logger.Error($"Error stopping REST API Server: {ex.Message}", ex);
        }

        _listener = null;
        _logger.Info("REST API Server stopped.");
        return Task.CompletedTask;
    }

    public void UpdateLiveAccessPoints(IEnumerable<AccessPoint> accessPoints)
    {
        lock (_liveLock)
        {
            _liveAccessPoints = accessPoints.ToList();
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken token)
    {
        while (_isRunning && !token.IsCancellationRequested)
        {
            try
            {
                var context = await _listener!.GetContextAsync();
                _ = ProcessRequestAsync(context, token);
            }
            catch (HttpListenerException)
            {
                // Listener stopped, exit loop
                break;
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    _logger.Error($"Error accepting connection: {ex.Message}", ex);
                }
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken token)
    {
        var request = context.Request;
        var response = context.Response;

        // Apply CORS headers
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Headers", "X-API-Key, Content-Type");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.Close();
            return;
        }

        try
        {
            // Authenticate API Key
            string? providedKey = request.Headers["X-API-Key"];
            if (string.IsNullOrEmpty(providedKey))
            {
                // Fallback to query param
                providedKey = request.QueryString["api_key"];
            }

            if (string.IsNullOrEmpty(_apiKey) || providedKey != _apiKey)
            {
                await WriteJsonResponseAsync(response, HttpStatusCode.Unauthorized, new { error = "Unauthorized. Invalid or missing API Key." });
                return;
            }

            if (request.HttpMethod != "GET")
            {
                await WriteJsonResponseAsync(response, HttpStatusCode.MethodNotAllowed, new { error = "Only GET requests are supported." });
                return;
            }

            string path = request.Url?.AbsolutePath.ToLower() ?? "";
            if (path == "/api/status")
            {
                await WriteJsonResponseAsync(response, HttpStatusCode.OK, new { status = "Healthy", version = "1.0", port = _port });
            }
            else if (path == "/api/live")
            {
                List<AccessPoint> apList;
                lock (_liveLock)
                {
                    apList = _liveAccessPoints.ToList();
                }
                await WriteJsonResponseAsync(response, HttpStatusCode.OK, apList);
            }
            else if (path == "/api/sessions")
            {
                using var db = _contextFactory.CreateDbContext();
                var sessions = await db.Sessions.OrderByDescending(s => s.StartTime).ToListAsync(token);
                await WriteJsonResponseAsync(response, HttpStatusCode.OK, sessions);
            }
            else if (path.StartsWith("/api/sessions/"))
            {
                string idStr = path.Replace("/api/sessions/", "").Trim('/');
                if (Guid.TryParse(idStr, out Guid sessionId))
                {
                    using var db = _contextFactory.CreateDbContext();
                    var session = await db.Sessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, token);
                    if (session == null)
                    {
                        await WriteJsonResponseAsync(response, HttpStatusCode.NotFound, new { error = $"Session {sessionId} not found." });
                        return;
                    }
                    var samples = await db.Samples.Where(s => s.SessionId == sessionId).OrderBy(s => s.Timestamp).ToListAsync(token);
                    await WriteJsonResponseAsync(response, HttpStatusCode.OK, new { session, samples });
                }
                else
                {
                    await WriteJsonResponseAsync(response, HttpStatusCode.BadRequest, new { error = "Invalid Session ID format." });
                }
            }
            else if (path == "/api/stats")
            {
                using var db = _contextFactory.CreateDbContext();
                var totalSessions = await db.Sessions.CountAsync(token);
                var totalSamples = await db.Samples.CountAsync(token);
                
                double dbSizeMB = 0;
                try
                {
                    var fileInfo = new FileInfo("wireless_scanner.db");
                    if (fileInfo.Exists)
                    {
                        dbSizeMB = fileInfo.Length / (1024.0 * 1024.0);
                    }
                }
                catch {}

                await WriteJsonResponseAsync(response, HttpStatusCode.OK, new {
                    totalSessions,
                    totalSamples,
                    databaseSizeMB = dbSizeMB,
                    timestamp = DateTime.UtcNow
                });
            }
            else
            {
                await WriteJsonResponseAsync(response, HttpStatusCode.NotFound, new { error = "Endpoint not found." });
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error processing API request: {ex.Message}", ex);
            await WriteJsonResponseAsync(response, HttpStatusCode.InternalServerError, new { error = ex.Message });
        }
    }

    private async Task WriteJsonResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, object data)
    {
        try
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = "application/json";
            
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
        catch (Exception ex)
        {
            _logger.Error($"Error writing HTTP response: {ex.Message}", ex);
        }
    }
}
