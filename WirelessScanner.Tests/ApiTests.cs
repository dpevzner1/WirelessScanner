using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

[TestFixture]
public class ApiTests
{
    private Microsoft.EntityFrameworkCore.DbContextOptions<WlanDbContext> _options = null!;
    private MockDbContextFactory _factory = null!;
    private RestApiServer _apiServer = null!;
    private HttpClient _httpClient = null!;
    private string _testApiKey = "ws_live_testkey1234567890abcdefabcdef";
    private int _testPort = 5099;

    [SetUp]
    public async Task Setup()
    {
        _options = new DbContextOptionsBuilder<WlanDbContext>()
            .UseSqlite($"Data Source=test_api_scanner.db")
            .Options;

        _factory = new MockDbContextFactory(_options);

        using (var context = new WlanDbContext(_options))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
        }

        _apiServer = new RestApiServer(_factory, new NullLogger());
        _httpClient = new HttpClient();
        
        await _apiServer.StartAsync(_testPort, _testApiKey);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _apiServer.StopAsync();
        _httpClient.Dispose();

        using (var context = new WlanDbContext(_options))
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task Server_StartsAndStopsCorrectly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_apiServer.IsRunning, Is.True);
            Assert.That(_apiServer.Port, Is.EqualTo(_testPort));
            Assert.That(_apiServer.ApiKey, Is.EqualTo(_testApiKey));
        });

        await _apiServer.StopAsync();
        Assert.That(_apiServer.IsRunning, Is.False);
    }

    [Test]
    public async Task Request_WithoutApiKey_ReturnsUnauthorized()
    {
        var response = await _httpClient.GetAsync($"http://localhost:{_testPort}/api/status");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        string body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Unauthorized"));
    }

    [Test]
    public async Task Request_WithInvalidApiKey_ReturnsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{_testPort}/api/status");
        request.Headers.Add("X-API-Key", "invalid_key");

        var response = await _httpClient.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Request_WithValidHeaderApiKey_ReturnsStatusOk()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{_testPort}/api/status");
        request.Headers.Add("X-API-Key", _testApiKey);

        var response = await _httpClient.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
            Assert.That(doc.RootElement.GetProperty("port").GetInt32(), Is.EqualTo(_testPort));
        });
    }

    [Test]
    public async Task Request_WithQueryStringApiKey_ReturnsStatusOk()
    {
        var response = await _httpClient.GetAsync($"http://localhost:{_testPort}/api/status?api_key={_testApiKey}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Request_LiveAccessPoints_ReturnsScannedData()
    {
        var testAPs = new List<AccessPoint>
        {
            new("00:11:22:33:44:55", "Test AP 1", null, "5 GHz", 36, 80, -50, -95, 45, 90, "WPA2-PSK", "Infrastructure", "Intel", new[] { "300" }, DateTime.UtcNow)
        };

        _apiServer.UpdateLiveAccessPoints(testAPs);

        var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{_testPort}/api/live");
        request.Headers.Add("X-API-Key", _testApiKey);

        var response = await _httpClient.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        var apList = JsonSerializer.Deserialize<List<AccessPoint>>(body);
        
        Assert.Multiple(() =>
        {
            Assert.That(apList, Is.Not.Null);
            Assert.That(apList!.Count, Is.EqualTo(1));
            Assert.That(apList[0].SSID, Is.EqualTo("Test AP 1"));
            Assert.That(apList[0].BSSID, Is.EqualTo("00:11:22:33:44:55"));
        });
    }

    [Test]
    public async Task Request_Stats_ReturnsDbSummaries()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{_testPort}/api/stats");
        request.Headers.Add("X-API-Key", _testApiKey);

        var response = await _httpClient.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("totalSessions").GetInt32(), Is.EqualTo(0));
            Assert.That(doc.RootElement.GetProperty("totalSamples").GetInt32(), Is.EqualTo(0));
            Assert.That(doc.RootElement.TryGetProperty("databaseSizeMB", out _), Is.True);
        });
    }
}
