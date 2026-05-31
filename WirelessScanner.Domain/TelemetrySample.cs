using System;

namespace WirelessScanner.Domain;

public record TelemetrySample(
    DateTime Timestamp,
    string SurveyPoint,
    string BSSID,
    string SSID,
    string Band,
    int Channel,
    int RSSI,
    int SNR,
    int Quality,
    double Jitter,
    int? NoiseFloor
);
