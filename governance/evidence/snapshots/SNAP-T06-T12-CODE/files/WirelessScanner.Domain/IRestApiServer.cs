using System.Collections.Generic;
using System.Threading.Tasks;

namespace WirelessScanner.Domain;

public interface IRestApiServer
{
    bool IsRunning { get; }
    int Port { get; }
    string ApiKey { get; }
    // Last start/listen failure in operator language (e.g. port in use), or null (F05/F13).
    string? LastError { get; }
    Task StartAsync(int port, string apiKey);
    Task StopAsync();
    void UpdateLiveAccessPoints(IEnumerable<AccessPoint> accessPoints);
}
