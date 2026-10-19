namespace WirelessScanner.Infrastructure;

using WirelessScanner.Domain;

internal static class AccessPointExtensions
{
    public static AccessPoint WithBssLoad(this AccessPoint ap, (int Stations, double UtilizationPct, int AdmissionCapacity)? load) =>
        load is { } l ? ap with { StationCount = l.Stations, ChannelUtilizationPct = l.UtilizationPct, AdmissionCapacity = l.AdmissionCapacity } : ap;

    public static AccessPoint WithWps(this AccessPoint ap, WlanAdapter.WpsInfo? wps) =>
        wps is null ? ap : ap with { WpsManufacturer = wps.Manufacturer, WpsModel = wps.Model, WpsModelNumber = wps.ModelNumber, WpsDeviceName = wps.DeviceName, WpsUuid = wps.Uuid };
}
