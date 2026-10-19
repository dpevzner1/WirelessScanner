using System.Threading.Tasks;

namespace WirelessScanner.Domain;

public interface ISettingsRepository
{
    Task<string?> GetSettingAsync(string key);
    Task SaveSettingAsync(string key, string value);
}
