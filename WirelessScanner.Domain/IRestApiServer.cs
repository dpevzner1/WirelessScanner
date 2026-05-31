using System.Collections.Generic;
using System.Threading.Tasks;

namespace WirelessScanner.Domain;

public interface IRestApiServer
{
    bool IsRunning { get; }
    int Port { get; }
    string ApiKey { get; }
    Task StartAsync(int port, string apiKey);
    Task StopAsync();
    void UpdateLiveAccessPoints(IEnumerable<AccessPoint> accessPoints);
}
