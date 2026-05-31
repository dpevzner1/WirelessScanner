using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

public class ExportService : IExportService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IStatisticsCalculator _statisticsCalculator;

    static ExportService()
    {
        // Set QuestPDF license type to Community
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public ExportService(ISessionRepository sessionRepository, IStatisticsCalculator statisticsCalculator)
    {
        _sessionRepository = sessionRepository;
        _statisticsCalculator = statisticsCalculator;
    }

    public async Task ExportToCsvAsync(string filePath, Guid sessionId, ExportFields? fields = null)
    {
        var session = await _sessionRepository.GetSessionAsync(sessionId);
        if (session == null) throw new KeyNotFoundException("Session not found.");

        var samples = await _sessionRepository.GetSamplesAsync(sessionId);
        var f = fields ?? new ExportFields();

        if (f.IncludedBssids != null)
        {
            var allowed = f.IncludedBssids.ToHashSet();
            samples = samples.Where(s => allowed.Contains(s.BSSID)).ToList();
        }

        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);

        // Parse Scope & Notes
        string scope = session.Scope;
        string notes = "";
        if (scope.Contains(" | Notes: "))
        {
            var parts = scope.Split(" | Notes: ", 2);
            scope = parts[0];
            notes = parts[1];
        }

        // Session metadata header
        await writer.WriteLineAsync($"# Session Name: {EscapeCsvField(session.Name)}");
        await writer.WriteLineAsync($"# Facility: {EscapeCsvField(session.FacilityName)}");
        await writer.WriteLineAsync($"# Scope: {EscapeCsvField(scope)}");
        await writer.WriteLineAsync($"# Notes: {EscapeCsvField(notes)}");
        await writer.WriteLineAsync($"# Capture Mode: {(session.Mode == CaptureSessionMode.Stationary ? "Stationary Saturation" : "Roaming Survey")}");
        await writer.WriteLineAsync($"# Interface: {EscapeCsvField(session.InterfaceName)}");
        await writer.WriteLineAsync($"# Start Time: {session.StartTime:yyyy-MM-dd HH:mm:ss}");
        await writer.WriteLineAsync($"# End Time: {(session.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Active")}");
        await writer.WriteLineAsync("#");

        // Column headers
        var headers = new List<string>();
        if (f.Time) headers.Add("Timestamp");
        if (f.Zone) headers.Add("SurveyPoint");
        if (f.BSSID) headers.Add("BSSID");
        if (f.SSID) headers.Add("SSID");
        if (f.Band) headers.Add("Band");
        if (f.Channel) headers.Add("Channel");
        if (f.RSSI) headers.Add("RSSI");
        if (f.SNR) headers.Add("SNR");
        if (f.Quality) headers.Add("Quality");
        if (f.Jitter) headers.Add("Jitter");

        await writer.WriteLineAsync(string.Join(",", headers));

        foreach (var s in samples)
        {
            var values = new List<string>();
            if (f.Time) values.Add(s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            if (f.Zone) values.Add(EscapeCsvField(s.SurveyPoint));
            if (f.BSSID) values.Add(EscapeCsvField(s.BSSID));
            if (f.SSID) values.Add(EscapeCsvField(s.SSID));
            if (f.Band) values.Add(EscapeCsvField(s.Band));
            if (f.Channel) values.Add(s.Channel.ToString());
            if (f.RSSI) values.Add(s.RSSI.ToString());
            if (f.SNR) values.Add(s.SNR.ToString());
            if (f.Quality) values.Add(s.Quality.ToString());
            if (f.Jitter) values.Add(s.Jitter.ToString("F3"));

            await writer.WriteLineAsync(string.Join(",", values));
        }
    }

    public async Task ExportToJsonAsync(string filePath, Guid sessionId, ExportFields? fields = null)
    {
        var session = await _sessionRepository.GetSessionAsync(sessionId);
        if (session == null) throw new KeyNotFoundException("Session not found.");

        var samples = (await _sessionRepository.GetSamplesAsync(sessionId)).ToList();
        var f = fields ?? new ExportFields();

        if (f.IncludedBssids != null)
        {
            var allowed = f.IncludedBssids.ToHashSet();
            samples = samples.Where(s => allowed.Contains(s.BSSID)).ToList();
        }

        double duration = 0.0;
        if (samples.Any())
        {
            duration = ((session.EndTime ?? samples.Last().Timestamp) - session.StartTime).TotalSeconds;
        }

        var uniqueAps = samples.Select(s => s.BSSID).Distinct().Count();
        var uniqueSsids = samples.Select(s => s.SSID).Distinct().Count();
        var avgRssi = samples.Any() ? samples.Average(s => s.RSSI) : 0.0;
        var minRssi = samples.Any() ? samples.Min(s => s.RSSI) : 0;
        var maxRssi = samples.Any() ? samples.Max(s => s.RSSI) : 0;

        var summary = new JsonExportSummary(
            DurationSeconds: duration,
            TotalSamples: samples.Count,
            UniqueAPs: uniqueAps,
            UniqueSSIDs: uniqueSsids,
            AvgRSSI: avgRssi,
            WeakestRSSI: minRssi,
            StrongestRSSI: maxRssi
        );

        var filteredSamples = samples.Select(s =>
        {
            var dict = new Dictionary<string, object?>();
            if (f.Time) dict["Timestamp"] = s.Timestamp;
            if (f.Zone) dict["SurveyPoint"] = s.SurveyPoint;
            if (f.BSSID) dict["BSSID"] = s.BSSID;
            if (f.SSID) dict["SSID"] = s.SSID;
            if (f.Band) dict["Band"] = s.Band;
            if (f.Channel) dict["Channel"] = s.Channel;
            if (f.RSSI) dict["RSSI"] = s.RSSI;
            if (f.SNR) dict["SNR"] = s.SNR;
            if (f.Quality) dict["Quality"] = s.Quality;
            if (f.Jitter) dict["Jitter"] = s.Jitter;
            return dict;
        }).ToList();

        // Parse Scope & Notes
        string scope = session.Scope;
        string notes = "";
        if (scope.Contains(" | Notes: "))
        {
            var parts = scope.Split(" | Notes: ", 2);
            scope = parts[0];
            notes = parts[1];
        }

        var sessionDict = new Dictionary<string, object?>
        {
            ["SessionId"] = session.SessionId,
            ["Name"] = session.Name,
            ["Scope"] = scope,
            ["Notes"] = notes,
            ["FacilityName"] = session.FacilityName,
            ["InterfaceName"] = session.InterfaceName,
            ["StartTime"] = session.StartTime,
            ["EndTime"] = session.EndTime,
            ["Mode"] = session.Mode == CaptureSessionMode.Stationary ? "Stationary Saturation" : "Roaming Survey",
            ["Status"] = session.Status.ToString()
        };

        var data = new JsonExportData(sessionDict, filteredSamples, summary);

        var options = new JsonSerializerOptions { WriteIndented = true };
        using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, data, options);
    }

    public async Task ExportToPdfAsync(string filePath, Guid sessionId, ExportMetadata metadata, ExportFields? fields = null)
    {
        var session = await _sessionRepository.GetSessionAsync(sessionId);
        if (session == null) throw new KeyNotFoundException("Session not found.");

        var samples = (await _sessionRepository.GetSamplesAsync(sessionId)).ToList();
        var f = fields ?? new ExportFields();

        if (f.IncludedBssids != null)
        {
            var allowed = f.IncludedBssids.ToHashSet();
            samples = samples.Where(s => allowed.Contains(s.BSSID)).ToList();
        }

        await Task.Run(() =>
        {
            var document = new ReportDocument(session, samples, metadata, _statisticsCalculator, f);
            document.GeneratePdf(filePath);
        });
    }

    public async Task ExportToCsvAsync(string filePath, IEnumerable<Guid> sessionIds, ExportFields? fields = null)
    {
        var f = fields ?? new ExportFields();
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        await writer.WriteLineAsync("# Multi-Session Export");
        await writer.WriteLineAsync($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        await writer.WriteLineAsync($"# Sessions Included: {sessionIds.Count()}");
        await writer.WriteLineAsync("#");

        var headers = new List<string> { "SessionName", "Facility", "Scope", "Notes", "CaptureMode" };
        if (f.Time) headers.Add("Timestamp");
        if (f.Zone) headers.Add("SurveyPoint");
        if (f.BSSID) headers.Add("BSSID");
        if (f.SSID) headers.Add("SSID");
        if (f.Band) headers.Add("Band");
        if (f.Channel) headers.Add("Channel");
        if (f.RSSI) headers.Add("RSSI");
        if (f.SNR) headers.Add("SNR");
        if (f.Quality) headers.Add("Quality");
        if (f.Jitter) headers.Add("Jitter");

        await writer.WriteLineAsync(string.Join(",", headers));

        foreach (var sessionId in sessionIds)
        {
            var session = await _sessionRepository.GetSessionAsync(sessionId);
            if (session == null) continue;

            var samples = await _sessionRepository.GetSamplesAsync(sessionId);
            if (f.IncludedBssids != null)
            {
                var allowed = f.IncludedBssids.ToHashSet();
                samples = samples.Where(s => allowed.Contains(s.BSSID));
            }

            string scope = session.Scope;
            string notes = "";
            if (scope.Contains(" | Notes: "))
            {
                var parts = scope.Split(" | Notes: ", 2);
                scope = parts[0];
                notes = parts[1];
            }
            string modeText = session.Mode == CaptureSessionMode.Stationary ? "Stationary Saturation" : "Roaming Survey";

            foreach (var s in samples)
            {
                var values = new List<string>
                {
                    EscapeCsvField(session.Name),
                    EscapeCsvField(session.FacilityName),
                    EscapeCsvField(scope),
                    EscapeCsvField(notes),
                    EscapeCsvField(modeText)
                };
                if (f.Time) values.Add(s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                if (f.Zone) values.Add(EscapeCsvField(s.SurveyPoint));
                if (f.BSSID) values.Add(EscapeCsvField(s.BSSID));
                if (f.SSID) values.Add(EscapeCsvField(s.SSID));
                if (f.Band) values.Add(EscapeCsvField(s.Band));
                if (f.Channel) values.Add(s.Channel.ToString());
                if (f.RSSI) values.Add(s.RSSI.ToString());
                if (f.SNR) values.Add(s.SNR.ToString());
                if (f.Quality) values.Add(s.Quality.ToString());
                if (f.Jitter) values.Add(s.Jitter.ToString("F3"));

                await writer.WriteLineAsync(string.Join(",", values));
            }
        }
    }

    public async Task ExportToJsonAsync(string filePath, IEnumerable<Guid> sessionIds, ExportFields? fields = null)
    {
        var sessionsData = new List<JsonExportData>();
        var f = fields ?? new ExportFields();

        foreach (var sessionId in sessionIds)
        {
            var session = await _sessionRepository.GetSessionAsync(sessionId);
            if (session == null) continue;

            var samples = (await _sessionRepository.GetSamplesAsync(sessionId)).ToList();
            if (f.IncludedBssids != null)
            {
                var allowed = f.IncludedBssids.ToHashSet();
                samples = samples.Where(s => allowed.Contains(s.BSSID)).ToList();
            }

            double duration = 0.0;
            if (samples.Any())
            {
                duration = ((session.EndTime ?? samples.Last().Timestamp) - session.StartTime).TotalSeconds;
            }

            var uniqueAps = samples.Select(s => s.BSSID).Distinct().Count();
            var uniqueSsids = samples.Select(s => s.SSID).Distinct().Count();
            var avgRssi = samples.Any() ? samples.Average(s => s.RSSI) : 0.0;
            var minRssi = samples.Any() ? samples.Min(s => s.RSSI) : 0;
            var maxRssi = samples.Any() ? samples.Max(s => s.RSSI) : 0;

            var summary = new JsonExportSummary(duration, samples.Count, uniqueAps, uniqueSsids, avgRssi, minRssi, maxRssi);

            var filteredSamples = samples.Select(s =>
            {
                var dict = new Dictionary<string, object?>();
                if (f.Time) dict["Timestamp"] = s.Timestamp;
                if (f.Zone) dict["SurveyPoint"] = s.SurveyPoint;
                if (f.BSSID) dict["BSSID"] = s.BSSID;
                if (f.SSID) dict["SSID"] = s.SSID;
                if (f.Band) dict["Band"] = s.Band;
                if (f.Channel) dict["Channel"] = s.Channel;
                if (f.RSSI) dict["RSSI"] = s.RSSI;
                if (f.SNR) dict["SNR"] = s.SNR;
                if (f.Quality) dict["Quality"] = s.Quality;
                if (f.Jitter) dict["Jitter"] = s.Jitter;
                return dict;
            }).ToList();

            string scope = session.Scope;
            string notes = "";
            if (scope.Contains(" | Notes: "))
            {
                var parts = scope.Split(" | Notes: ", 2);
                scope = parts[0];
                notes = parts[1];
            }

            var sessionDict = new Dictionary<string, object?>
            {
                ["SessionId"] = session.SessionId,
                ["Name"] = session.Name,
                ["Scope"] = scope,
                ["Notes"] = notes,
                ["FacilityName"] = session.FacilityName,
                ["InterfaceName"] = session.InterfaceName,
                ["StartTime"] = session.StartTime,
                ["EndTime"] = session.EndTime,
                ["Mode"] = session.Mode == CaptureSessionMode.Stationary ? "Stationary Saturation" : "Roaming Survey",
                ["Status"] = session.Status.ToString()
            };

            sessionsData.Add(new JsonExportData(sessionDict, filteredSamples, summary));
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, sessionsData, options);
    }

    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;
        if (field.Contains("\"") || field.Contains(",") || field.Contains("\n") || field.Contains("\r"))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }
}

public record JsonExportData(
    object Session,
    IEnumerable<object> Samples,
    JsonExportSummary Summary
);

public record JsonExportSummary(
    double DurationSeconds,
    int TotalSamples,
    int UniqueAPs,
    int UniqueSSIDs,
    double AvgRSSI,
    int WeakestRSSI,
    int StrongestRSSI
);

public class ReportDocument : IDocument
{
    private readonly CaptureSession _session;
    private readonly List<TelemetrySample> _samples;
    private readonly ExportMetadata _metadata;
    private readonly IStatisticsCalculator _calculator;
    private readonly ExportFields _fields;

    public ReportDocument(CaptureSession session, List<TelemetrySample> samples, ExportMetadata metadata, IStatisticsCalculator calculator, ExportFields? fields = null)
    {
        _session = session;
        _samples = samples;
        _metadata = metadata;
        _calculator = calculator;
        _fields = fields ?? new ExportFields();
    }

    public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

    public void Compose(IDocumentContainer container)
    {
        container
            .Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Arial"));

                // Header
                page.Header().Element(ComposeHeader);

                // Content
                page.Content().Element(ComposeContent);

                // Footer
                page.Footer().Element(ComposeFooter);
            });
    }

    private void ComposeHeader(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text("WIRELESS SITE SURVEY REPORT").FontSize(18).Bold().FontColor("#00F2FE");
                column.Item().Text($"Session: {_session.Name}").FontSize(10).FontColor("#9CA3AF");
            });
            row.ConstantItem(100).AlignRight().Text("WirelessScanner").FontSize(12).Bold().FontColor("#4FACFE");
        });
    }

    private void ComposeContent(IContainer container)
    {
        container.PaddingTop(10).Column(column =>
        {
            // Session Metadata Block
            column.Item().Border(1).BorderColor("#E5E7EB").Padding(10).Column(subCol =>
            {
                subCol.Item().Row(r =>
                {
                    r.RelativeItem().Text($"Client Name: {_metadata.ClientName}").Bold();
                    r.RelativeItem().Text($"Surveyor Name: {_metadata.SurveyorName}").Bold();
                });
                
                string scope = _session.Scope;
                string sessionNotes = "";
                if (scope.Contains(" | Notes: "))
                {
                    var parts = scope.Split(" | Notes: ", 2);
                    scope = parts[0];
                    sessionNotes = parts[1];
                }

                subCol.Item().PaddingTop(5).Text($"Notes (Export): {_metadata.Notes}");
                subCol.Item().PaddingTop(5).Text($"Facility Name: {_session.FacilityName}");
                subCol.Item().Text($"Scope: {scope}");
                subCol.Item().Text($"Capture Mode: {(_session.Mode == CaptureSessionMode.Stationary ? "Stationary Saturation" : "Roaming Survey")}");
                if (!string.IsNullOrWhiteSpace(sessionNotes))
                {
                    subCol.Item().Text($"Session Notes: {sessionNotes}");
                }
                subCol.Item().Text($"Time: {_session.StartTime.ToLocalTime():yyyy-MM-dd HH:mm:ss} to {(_session.EndTime.HasValue ? _session.EndTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "Active")}");
            });

            // Key Metrics Summary
            column.Item().PaddingTop(15).Text("Executive Summary").FontSize(14).Bold().FontColor("#00F2FE");
            column.Item().PaddingTop(5).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                });

                table.Header(header =>
                {
                    header.Cell().Background("#00F2FE").Padding(5).Text("Total Samples").Bold().FontColor(Colors.White);
                    header.Cell().Background("#00F2FE").Padding(5).Text("Unique SSIDs").Bold().FontColor(Colors.White);
                    header.Cell().Background("#00F2FE").Padding(5).Text("Unique APs").Bold().FontColor(Colors.White);
                    header.Cell().Background("#00F2FE").Padding(5).Text("Duration").Bold().FontColor(Colors.White);
                });

                var duration = (_session.EndTime ?? DateTime.UtcNow) - _session.StartTime;
                var uniqueSsids = _samples.Select(s => s.SSID).Distinct().Count();
                var uniqueAps = _samples.Select(s => s.BSSID).Distinct().Count();

                table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).Text(_samples.Count.ToString());
                table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).Text(uniqueSsids.ToString());
                table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).Text(uniqueAps.ToString());
                table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(5).Text($"{duration.TotalMinutes:F1} min");
            });

            // AP Statistics Table
            column.Item().PaddingTop(20).Text("Access Point Telemetry Analysis").FontSize(14).Bold().FontColor("#00F2FE");
            column.Item().PaddingTop(5).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(3); // SSID
                    columns.RelativeColumn(2.5f); // BSSID
                    columns.RelativeColumn(1.2f); // Band
                    columns.RelativeColumn(1f); // Channel
                    columns.RelativeColumn(1f); // Min RSSI
                    columns.RelativeColumn(1f); // Max RSSI
                    columns.RelativeColumn(1f); // Mean RSSI
                    columns.RelativeColumn(1.2f); // StdDev
                    columns.RelativeColumn(1f); // Jitter
                });

                table.Header(header =>
                {
                    header.Cell().Background("#16161A").Padding(3).Text("SSID").Bold().FontColor(Colors.White).FontSize(8);
                    header.Cell().Background("#16161A").Padding(3).Text("BSSID").Bold().FontColor(Colors.White).FontSize(8);
                    header.Cell().Background("#16161A").Padding(3).Text("Band").Bold().FontColor(Colors.White).FontSize(8);
                    header.Cell().Background("#16161A").Padding(3).Text("Ch").Bold().FontColor(Colors.White).FontSize(8);
                    header.Cell().Background("#16161A").Padding(3).Text("Min").Bold().FontColor(Colors.White).FontSize(8);
                    header.Cell().Background("#16161A").Padding(3).Text("Max").Bold().FontColor(Colors.White).FontSize(8);
                    header.Cell().Background("#16161A").Padding(3).Text("Mean").Bold().FontColor(Colors.White).FontSize(8);
                    header.Cell().Background("#16161A").Padding(3).Text("StdDev").Bold().FontColor(Colors.White).FontSize(8);
                    header.Cell().Background("#16161A").Padding(3).Text("Jitt").Bold().FontColor(Colors.White).FontSize(8);
                });

                var grouped = _samples.GroupBy(s => s.BSSID);
                foreach (var g in grouped)
                {
                    var first = g.First();
                    var stats = _calculator.CalculateStats(g.Select(s => s.RSSI), g.Select(s => s.Jitter));

                    table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(first.SSID).FontSize(8);
                    table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(first.BSSID).FontSize(8);
                    table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(first.Band).FontSize(8);
                    table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(first.Channel.ToString()).FontSize(8);
                    table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(stats.MinRssi.ToString()).FontSize(8);
                    table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(stats.MaxRssi.ToString()).FontSize(8);
                    table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(stats.MeanRssi.ToString("F1")).FontSize(8);
                    table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(stats.StdDevRssi.ToString("F1")).FontSize(8);
                    table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(stats.AvgJitter.ToString("F1")).FontSize(8);
                }
            });

            // Timeline Graph
            if (_samples.Any())
            {
                column.Item().PaddingTop(20).Text("Signal Strength Timeline (Top 5 APs)").FontSize(14).Bold().FontColor("#00F2FE");
                column.Item().PaddingTop(10).Height(250).Image((ImageSize size) =>
                {
                    var scale = 2f;
                    var w = (int)(size.Width * scale);
                    var h = (int)(size.Height * scale);

                    using var bitmap = new SKBitmap(w, h);
                    using var canvas = new SKCanvas(bitmap);
                    
                    canvas.Scale(scale);
                    DrawTimelineGraph(canvas, size.Width, size.Height);

                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    return data.ToArray();
                });

                // LEGEND for the top 5 APs matching the graph
                column.Item().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });

                    var topAps = _samples.GroupBy(s => s.BSSID)
                                         .OrderByDescending(g => g.Count())
                                         .Take(5)
                                         .ToList();

                    foreach (var group in topAps)
                    {
                        var first = group.First();
                        string ssid = string.IsNullOrEmpty(first.SSID) ? "<Hidden SSID>" : first.SSID;
                        string bssid = first.BSSID;

                        var sortedSamplesForLegend = group.OrderBy(s => s.Timestamp).ToList();
                        double avgRssi = sortedSamplesForLegend.Any() ? sortedSamplesForLegend.Average(x => x.RSSI) : -100;

                        string color;
                        if (avgRssi >= -55)
                        {
                            color = "#00FF66"; // Excellent: Emerald Green
                        }
                        else if (avgRssi >= -70)
                        {
                            color = "#FFBB33"; // Fair/Good: Vibrant Amber
                        }
                        else
                        {
                            color = "#FF3300"; // Weak/Poor: Crimson Red
                        }

                        table.Cell().PaddingBottom(4).Row(row =>
                        {
                            row.ConstantItem(12).Height(12).Background(color);
                            row.ConstantItem(5);
                            row.RelativeItem().Text($"{ssid} ({bssid})").FontSize(9).FontColor("#16161A");
                        });
                    }
                });
            }

            // Detailed Telemetry Log Table
            if (_samples.Any())
            {
                column.Item().PageBreak();
                column.Item().PaddingTop(10).Text("Detailed Telemetry Log").FontSize(14).Bold().FontColor("#00F2FE");
                column.Item().PaddingTop(5).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        if (_fields.Time) columns.RelativeColumn(2.2f); // Timestamp
                        if (_fields.SSID) columns.RelativeColumn(2.8f); // SSID
                        if (_fields.BSSID) columns.RelativeColumn(2.2f); // BSSID
                        if (_fields.Band) columns.RelativeColumn(1f);   // Band
                        if (_fields.Channel) columns.RelativeColumn(0.8f); // Ch
                        if (_fields.RSSI) columns.RelativeColumn(1f);   // RSSI
                        if (_fields.SNR) columns.RelativeColumn(0.8f); // SNR
                        if (_fields.Quality) columns.RelativeColumn(0.8f); // Qual
                        if (_fields.Jitter) columns.RelativeColumn(1f);   // Jitter
                        if (_fields.Zone) columns.RelativeColumn(1.8f); // Zone
                    });

                    table.Header(header =>
                    {
                        if (_fields.Time) header.Cell().Background("#16161A").Padding(3).Text("Timestamp").Bold().FontColor(Colors.White).FontSize(8);
                        if (_fields.SSID) header.Cell().Background("#16161A").Padding(3).Text("SSID").Bold().FontColor(Colors.White).FontSize(8);
                        if (_fields.BSSID) header.Cell().Background("#16161A").Padding(3).Text("BSSID").Bold().FontColor(Colors.White).FontSize(8);
                        if (_fields.Band) header.Cell().Background("#16161A").Padding(3).Text("Band").Bold().FontColor(Colors.White).FontSize(8);
                        if (_fields.Channel) header.Cell().Background("#16161A").Padding(3).Text("Ch").Bold().FontColor(Colors.White).FontSize(8);
                        if (_fields.RSSI) header.Cell().Background("#16161A").Padding(3).Text("RSSI").Bold().FontColor(Colors.White).FontSize(8);
                        if (_fields.SNR) header.Cell().Background("#16161A").Padding(3).Text("SNR").Bold().FontColor(Colors.White).FontSize(8);
                        if (_fields.Quality) header.Cell().Background("#16161A").Padding(3).Text("Qual").Bold().FontColor(Colors.White).FontSize(8);
                        if (_fields.Jitter) header.Cell().Background("#16161A").Padding(3).Text("Jitt").Bold().FontColor(Colors.White).FontSize(8);
                        if (_fields.Zone) header.Cell().Background("#16161A").Padding(3).Text("Zone").Bold().FontColor(Colors.White).FontSize(8);
                    });

                    foreach (var sample in _samples.OrderBy(s => s.Timestamp))
                    {
                        string ssid = string.IsNullOrEmpty(sample.SSID) ? "<Hidden SSID>" : sample.SSID;
                        if (_fields.Time) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(sample.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).FontSize(8);
                        if (_fields.SSID) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(ssid).FontSize(8);
                        if (_fields.BSSID) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(sample.BSSID).FontSize(8);
                        if (_fields.Band) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(sample.Band).FontSize(8);
                        if (_fields.Channel) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(sample.Channel.ToString()).FontSize(8);
                        if (_fields.RSSI) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text($"{sample.RSSI} dBm").FontSize(8);
                        if (_fields.SNR) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text($"{sample.SNR} dB").FontSize(8);
                        if (_fields.Quality) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text($"{sample.Quality}%").FontSize(8);
                        if (_fields.Jitter) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text($"{sample.Jitter:F1} ms").FontSize(8);
                        if (_fields.Zone) table.Cell().BorderBottom(1).BorderColor("#E5E7EB").Padding(3).Text(sample.SurveyPoint).FontSize(8);
                    }
                });
            }
        });
    }

    private void DrawTimelineGraph(SKCanvas canvas, float width, float height)
    {
        // Background
        using var bgPaint = new SKPaint { Color = SKColor.Parse("#16161A") };
        canvas.DrawRect(0, 0, width, height, bgPaint);

        // Group by BSSID to get top 5 APs
        var grouped = _samples.GroupBy(s => s.BSSID)
                              .OrderByDescending(g => g.Count())
                              .Take(5)
                              .ToList();

        if (!grouped.Any()) return;

        var startTime = _session.StartTime;
        var endTime = _session.EndTime ?? _samples.Max(s => s.Timestamp);
        var totalDuration = (endTime - startTime).TotalSeconds;
        if (totalDuration <= 0) totalDuration = 1;

        float marginLeft = 40;
        float marginRight = 60; // Make margin larger to fit the direct labels on the right
        float marginTop = 15;
        float marginBottom = 30;

        float plotWidth = width - marginLeft - marginRight;
        float plotHeight = height - marginTop - marginBottom;

        // Draw Grid and Y labels
        using var gridPaint = new SKPaint { Color = SKColor.Parse("#333333"), StrokeWidth = 1, IsStroke = true };
        using var textPaint = new SKPaint
        {
            Color = SKColor.Parse("#9CA3AF"),
            TextSize = 10,
            IsAntialias = true
        };

        // Draw vertical grid lines at Start, Mid, and End
        using var vertGridPaint = new SKPaint
        {
            Color = SKColor.Parse("#252528"),
            StrokeWidth = 1,
            IsStroke = true,
            PathEffect = SKPathEffect.CreateDash(new float[] { 3, 3 }, 0)
        };
        canvas.DrawLine(marginLeft, marginTop, marginLeft, height - marginBottom, vertGridPaint);
        if (totalDuration > 10)
        {
            canvas.DrawLine(marginLeft + plotWidth / 2f, marginTop, marginLeft + plotWidth / 2f, height - marginBottom, vertGridPaint);
            canvas.DrawLine(marginLeft + plotWidth, marginTop, marginLeft + plotWidth, height - marginBottom, vertGridPaint);
        }

        int[] rssiLevels = { -100, -90, -80, -70, -60, -50, -40, -30 };
        foreach (var r in rssiLevels)
        {
            var normalizedRssi = Math.Max(0, Math.Min(80, r + 100)); // 0 to 80 range
            var y = height - marginBottom - (float)((normalizedRssi / 80.0) * plotHeight);
            
            canvas.DrawLine(marginLeft, y, marginLeft + plotWidth, y, gridPaint);
            canvas.DrawText($"{r}", 10, y + 4, textPaint);
        }

        // Draw bottom axis line
        canvas.DrawLine(marginLeft, height - marginBottom, marginLeft + plotWidth, height - marginBottom, gridPaint);

        // Draw time labels
        string startStr = startTime.ToLocalTime().ToString("HH:mm:ss");
        canvas.DrawText(startStr, marginLeft, height - marginBottom + 16, textPaint);

        if (totalDuration > 10)
        {
            string midStr = startTime.AddSeconds(totalDuration / 2).ToLocalTime().ToString("HH:mm:ss");
            canvas.DrawText(midStr, marginLeft + plotWidth / 2f - 20, height - marginBottom + 16, textPaint);

            string endStr = endTime.ToLocalTime().ToString("HH:mm:ss");
            canvas.DrawText(endStr, marginLeft + plotWidth - 45, height - marginBottom + 16, textPaint);
        }

        // Draw X Axis label
        using var axisTitlePaint = new SKPaint
        {
            Color = SKColor.Parse("#9CA3AF"),
            TextSize = 8,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center,
            FakeBoldText = true
        };
        canvas.DrawText("CAPTURE TIMELINE (HH:MM:SS)", marginLeft + plotWidth / 2f, height - 4, axisTitlePaint);

        foreach (var group in grouped)
        {
            var sortedSamples = group.OrderBy(s => s.Timestamp).ToList();
            double avgRssi = sortedSamples.Any() ? sortedSamples.Average(x => x.RSSI) : -100;
            
            SKColor color;
            if (avgRssi >= -55)
            {
                color = SKColor.Parse("#00FF66"); // Excellent: Emerald Green
            }
            else if (avgRssi >= -70)
            {
                color = SKColor.Parse("#FFBB33"); // Fair/Good: Vibrant Amber
            }
            else
            {
                color = SKColor.Parse("#FF3300"); // Weak/Poor: Crimson Red
            }

            using var linePaint = new SKPaint
            {
                Color = color,
                StrokeWidth = 2,
                IsStroke = true,
                IsAntialias = true
            };

            var path = new SKPath();
            bool isFirst = true;

            int step = 1;
            if (sortedSamples.Count > 1000)
            {
                step = sortedSamples.Count / 1000;
                if (step < 1) step = 1;
            }

            float lastX = marginLeft;
            float lastY = height - marginBottom;
            float firstX = marginLeft;

            for (int i = 0; i < sortedSamples.Count; i += step)
            {
                var sample = sortedSamples[i];
                var timeOffset = (sample.Timestamp - startTime).TotalSeconds;
                var x = marginLeft + (float)(timeOffset / totalDuration * plotWidth);
                
                // Map RSSI (-100 to -20) to Y
                var normalizedRssi = Math.Max(0, Math.Min(80, sample.RSSI + 100)); // 0 to 80
                var y = height - marginBottom - (float)((normalizedRssi / 80.0) * plotHeight);

                if (isFirst)
                {
                    path.MoveTo(x, y);
                    firstX = x;
                    isFirst = false;
                }
                else
                {
                    path.LineTo(x, y);
                }
                lastX = x;
                lastY = y;
            }

            // Draw semi-transparent area fill underneath the curve
            if (!isFirst)
            {
                using var fillPath = new SKPath(path);
                fillPath.LineTo(lastX, height - marginBottom);
                fillPath.LineTo(firstX, height - marginBottom);
                fillPath.Close();

                using var fillPaint = new SKPaint
                {
                    Color = color.WithAlpha(0x15),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };
                canvas.DrawPath(fillPath, fillPaint);
            }

            canvas.DrawPath(path, linePaint);

            // Draw direct SSID label at the end of the line
            var firstSample = sortedSamples.FirstOrDefault();
            if (firstSample != null)
            {
                string labelText = string.IsNullOrEmpty(firstSample.SSID) ? "<Hidden SSID>" : firstSample.SSID;
                
                using var labelPaint = new SKPaint
                {
                    Color = color,
                    TextSize = 7.5f,
                    IsAntialias = true,
                    FakeBoldText = true
                };

                float drawX = lastX + 4;
                if (drawX + 50 > width) drawX = width - 55; // clamp to keep it on canvas

                // Draw background box for text readability
                using var bgTextPaint = new SKPaint
                {
                    Color = SKColor.Parse("#16161A"),
                    IsStroke = false
                };
                var textRect = new SKRect();
                labelPaint.MeasureText(labelText, ref textRect);
                textRect.Offset(drawX - 2, lastY - textRect.Height / 2 - 2);
                textRect.Right += 4;
                textRect.Bottom += 4;
                canvas.DrawRoundRect(textRect, 2, 2, bgTextPaint);

                canvas.DrawText(labelText, drawX, lastY + 3, labelPaint);
            }
        }
    }

    private void ComposeFooter(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Text($"Report generated on {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(8).FontColor("#9CA3AF");
            row.RelativeItem().AlignRight().Text(x =>
            {
                x.Span("Page ").FontSize(8).FontColor("#9CA3AF");
                x.CurrentPageNumber().FontSize(8).FontColor("#9CA3AF");
                x.Span(" of ").FontSize(8).FontColor("#9CA3AF");
                x.TotalPages().FontSize(8).FontColor("#9CA3AF");
            });
        });
    }
}
