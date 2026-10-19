using System;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
using WirelessScanner.Domain;

namespace WirelessScanner.Presentation;

/// <summary>
/// Reads the PC's position from the Windows Location service (Settings → Privacy → Location must allow desktop apps).
/// Accuracy depends on the source Windows uses (GNSS, Wi-Fi positioning, IP); it is always reported with the value.
/// Failures return a reason instead of throwing, so the operator can enter coordinates manually.
/// </summary>
public sealed class WindowsGeoLocationProvider : IGeoLocationProvider
{
    public async Task<(GeoTag? Tag, string? Error)> GetCurrentAsync(TimeSpan timeout)
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                return (null, $"Windows Location access is {access}. Turn on Settings → Privacy & security → Location (and 'Let desktop apps access your location'), or enter coordinates manually.");
            }

            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.High };
            var task = locator.GetGeopositionAsync(TimeSpan.FromMinutes(1), timeout).AsTask();
            var position = await task;
            var c = position.Coordinate;
            return (new GeoTag
            {
                Latitude = c.Point.Position.Latitude,
                Longitude = c.Point.Position.Longitude,
                AccuracyMeters = c.Accuracy,
                Source = $"windows-location ({c.PositionSource})",
                CapturedUtc = c.Timestamp.UtcDateTime
            }, null);
        }
        catch (Exception ex)
        {
            return (null, $"Could not read the location: {ex.Message}. Enter coordinates manually.");
        }
    }
}
