using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebhookDesk;

namespace WebhookDesk.Tests;

public sealed class ApiTests
{
    private const string Token = "test-token-123456789";

    [Fact]
    public async Task Inbox_requires_token_and_deduplicates_only_when_event_id_is_given()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        using var unauth = await client.PostAsync("/api/inbox/shop", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        client.DefaultRequestHeaders.Add("X-Hookdesk-Token", Token);
        using var firstRequest = Post("/api/inbox/shop", "{\"order\":1}", "order-1");
        using var first = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await first.Content.ReadFromJsonAsync<JsonElement>();
        var id = original.GetProperty("id").GetString();

        using var duplicateRequest = Post("/api/inbox/shop", "{\"order\":999}", "order-1");
        using var duplicate = await client.SendAsync(duplicateRequest);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var duplicateBody = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, duplicateBody.GetProperty("id").GetString());
        Assert.True(duplicateBody.GetProperty("duplicate").GetBoolean());

        using var noId = await client.PostAsync("/api/inbox/shop", new StringContent("{\"order\":1}"));
        Assert.Equal(HttpStatusCode.Created, noId.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/events?source=shop");
        Assert.Equal(2, list.GetArrayLength());
        Assert.False(list[0].TryGetProperty("body", out _));
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/events/{id}");
        Assert.Equal("{\"order\":1}", detail.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Inbox_rejects_bad_source_empty_body_and_oversized_payload()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Hookdesk-Token", Token);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync("/api/inbox/Bad.Source", new StringContent("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync("/api/inbox/shop", new ByteArrayContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await client.PostAsync("/api/inbox/shop", new ByteArrayContent(new byte[256 * 1024 + 1]))).StatusCode);
    }

    [Fact]
    public async Task Replay_records_target_status_and_preserves_payload()
    {
        var handler = new CaptureHandler();
        using var factory = NewFactory("http://127.0.0.1:9001/hook", handler);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Hookdesk-Token", Token);
        using var request = Post("/api/inbox/shop", "{\"order\":1}", "order-1");
        using var created = await client.SendAsync(request);
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = item.GetProperty("id").GetString();

        using var replay = await client.PostAsync($"/api/events/{id}/replay", null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("{\"order\":1}", handler.Body);
        Assert.Equal(id, handler.EventId);
        var attempts = await client.GetFromJsonAsync<JsonElement>($"/api/events/{id}/attempts");
        Assert.Equal(202, attempts[0].GetProperty("statusCode").GetInt32());
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook", false)]
    [InlineData("http://example.com/hook", false)]
    [InlineData("http://127.0.0.1/hook", true)]
    [InlineData("http://localhost:8080/hook", true)]
    public void Replay_target_is_loopback_http_only(string value, bool allowed)
    {
        Assert.Equal(allowed, ReplayTarget.TryParse(value, out _));
    }

    [Fact]
    public void Event_store_survives_reopening_the_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), "webhook-desk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "events.db");
        var first = new EventStore(path);
        first.Initialize();
        var saved = first.Add("shop", "order-2", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));

        var reopened = new EventStore(path);
        reopened.Initialize();
        Assert.Equal(saved.Body, reopened.Get(saved.Id)!.Body);
        Assert.Single(reopened.List("shop", 10));
    }

    private static HttpRequestMessage Post(string url, string body, string eventId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Event-Id", eventId);
        return request;
    }

    private static WebApplicationFactory<Program> NewFactory(string? replayUrl = null, CaptureHandler? handler = null)
    {
        var data = Path.Combine(Path.GetTempPath(), "webhook-desk-tests", Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["HOOKDESK_TOKEN"] = Token,
                    ["HOOKDESK_DATA_DIR"] = data,
                    ["HOOKDESK_REPLAY_URL"] = replayUrl
                }));
            if (handler is not null)
                builder.ConfigureTestServices(services => services.AddHttpClient("replay")
                    .ConfigurePrimaryHttpMessageHandler(() => handler));
        });
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? EventId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            EventId = request.Headers.GetValues("X-Hookdesk-Event-Id").Single();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
