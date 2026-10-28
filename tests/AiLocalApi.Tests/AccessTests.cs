using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using AiLocalApi.Contracts;
using AiLocalApi.Core;
using AiLocalApi.Desktop;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiLocalApi.Tests;

public class AccessTests
{
    private static AppPaths Paths() => new(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }

    [Fact] public void LegacyKeyMigratesWithoutEnablingAnonymousAccess()
    {
        var paths = Paths(); paths.Initialize();
        using (var db = new SqliteConnection($"Data Source={paths.Database}"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE settings(id INTEGER PRIMARY KEY,json TEXT); INSERT INTO settings VALUES(1,$settings); PRAGMA user_version=2";
            command.Parameters.AddWithValue("$settings", Wire.Encode(new Settings(ApiKey: "old-secret"))); command.ExecuteNonQuery();
        }
        var store = new StateStore(paths);
        Assert.Null(store.Settings.ApiKey); Assert.False(store.AllowAnonymous);
        Assert.Single(store.ApiKeys()); Assert.True(store.Authorize("Bearer old-secret")); Assert.False(store.Authorize(""));
        Assert.Single(Directory.GetFiles(paths.Data, "state.db.v2.*.bak"));
        Assert.Single(new StateStore(paths).ApiKeys());
    }

    [Fact] public void KeysAreIndependentRevocableAndNeverStoredAsSecrets()
    {
        var paths = Paths(); var store = new StateStore(paths);
        var one = store.CreateApiKey("客户端一"); var two = store.CreateApiKey("客户端二");
        Assert.True(store.AllowAnonymous); Assert.True(store.Authorize("")); Assert.False(store.Authorize("Bearer wrong"));
        store.SetAllowAnonymous(false); Assert.False(store.Authorize(""));
        Assert.True(store.Authorize("bearer " + one.Secret)); Assert.True(store.Authorize("Bearer " + two.Secret));
        Assert.Throws<AppException>(() => store.CreateApiKey("重复", two.Secret));
        store.SetApiKeyEnabled(one.Info.Id, false); Assert.False(store.Authorize("Bearer " + one.Secret)); Assert.True(store.Authorize("Bearer " + two.Secret));
        store.SetApiKeyEnabled(one.Info.Id, true); Assert.True(store.Authorize("Bearer " + one.Secret));
        store.DeleteApiKey(one.Info.Id); Assert.False(store.Authorize("Bearer " + one.Secret));
        Assert.True(new StateStore(paths).Authorize("Bearer " + two.Secret));
        using var db = new SqliteConnection($"Data Source={paths.Database}"); db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM api_keys";
        Assert.DoesNotContain(two.Secret, (string)command.ExecuteScalar()!);
    }

    [Fact] public async Task LiveAuthAndPortSwitchRetainServiceOnBindFailure()
    {
        await using var service = new ApplicationService(Paths());
        service.Paused = true;
        var key = service.Store.CreateApiKey("HTTP test"); service.Store.SetAllowAnonymous(false);
        await using var host = new ApiHost(service);
        var first = FreePort(); await host.RestartAsync(first);
        using var http = new HttpClient();
        Assert.True((await http.GetAsync($"http://localhost:{first}/health")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://localhost:{first}/v1/models")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        Assert.True((await http.GetAsync($"http://localhost:{first}/v1/models")).IsSuccessStatusCode);
        service.Store.SetApiKeyEnabled(key.Info.Id, false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://localhost:{first}/v1/models")).StatusCode);
        service.Store.SetAllowAnonymous(true); http.DefaultRequestHeaders.Authorization = null;
        Assert.True((await http.GetAsync($"http://localhost:{first}/v1/models")).IsSuccessStatusCode);
        using var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        await Assert.ThrowsAsync<AppException>(() => host.RestartAsync(((IPEndPoint)occupied.LocalEndpoint).Port));
        Assert.Equal(first, host.Port); Assert.Equal(first, service.Store.Settings.Port);
        Assert.True((await http.GetAsync($"http://localhost:{first}/health")).IsSuccessStatusCode);
        var second = FreePort(); await host.RestartAsync(second);
        Assert.True(service.Paused); Assert.Equal(second, host.Port); Assert.Equal(second, service.Store.Settings.Port);
        Assert.True((await http.GetAsync($"http://localhost:{second}/health")).IsSuccessStatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://localhost:{first}/health"));
        await host.RestartAsync(second);
        Assert.True((await http.GetAsync($"http://localhost:{second}/health")).IsSuccessStatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceProbeChecksModelBytesAndUsesBoundedRange(bool valid)
    {
        using var http = new HttpClient(new ProbeHandler(valid));
        if (valid) Assert.Contains("可用", await ConnectionDiagnostics.TestSourceAsync("hf-mirror.com", default, http));
        else await Assert.ThrowsAsync<IOException>(() => ConnectionDiagnostics.TestSourceAsync("hf-mirror.com", default, http));
    }
    private sealed class ProbeHandler(bool valid) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("bytes=0-3", request.Headers.Range!.ToString()); Assert.Equal("hf-mirror.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(valid ? [0x6c, 0x6d, 0x67, 0x67] : [60, 104, 116, 109]) });
        }
    }
}
