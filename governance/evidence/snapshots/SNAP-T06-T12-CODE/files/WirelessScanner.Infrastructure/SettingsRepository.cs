using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

public class SettingsRepository : ISettingsRepository
{
    private readonly IDbContextFactory<WlanDbContext> _contextFactory;

    public SettingsRepository(IDbContextFactory<WlanDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var record = await context.Settings.FindAsync(key);
        return record?.Value;
    }

    public async Task SaveSettingAsync(string key, string value)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var record = await context.Settings.FindAsync(key);
        if (record == null)
        {
            context.Settings.Add(new DbSettingRecord { Key = key, Value = value });
        }
        else
        {
            record.Value = value;
        }
        await context.SaveChangesAsync();
    }
}
