using System.Text;
using System.Text.RegularExpressions;
using WebhookDesk;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var dataDir = config["HOOKDESK_DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "data");
    Directory.CreateDirectory(dataDir);
    var store = new EventStore(Path.Combine(dataDir, "events.db"));
    store.Initialize();
    return store;
});
builder.Services.AddHttpClient("replay", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var app = builder.Build();
var token = app.Configuration["HOOKDESK_TOKEN"];
if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
    throw new InvalidOperationException("Set HOOKDESK_TOKEN to at least 16 characters before starting Webhook Desk.");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api");
api.AddEndpointFilter(async (context, next) =>
{
    var supplied = context.HttpContext.Request.Headers["X-Hookdesk-Token"].ToString();
    if (!TokenCheck.Matches(token, supplied)) return Results.Unauthorized();
    return await next(context);
});

api.MapPost("/inbox/{source}", async (string source, HttpRequest request, EventStore events) =>
{
    if (!Regex.IsMatch(source, "^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant))
        return Results.BadRequest(new { error = "Source must use 1-32 lowercase letters, numbers, underscores, or hyphens." });

    const int maxBytes = 256 * 1024;
    await using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    while (true)
    {
        var read = await request.Body.ReadAsync(chunk);
        if (read == 0) break;
        if (buffer.Length + read > maxBytes)
            return Results.Problem("Payload exceeds 256 KiB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        buffer.Write(chunk, 0, read);
    }
    if (buffer.Length == 0) return Results.BadRequest(new { error = "Payload is empty." });

    var bytes = buffer.ToArray();
    try { _ = new UTF8Encoding(false, true).GetString(bytes); }
    catch (DecoderFallbackException) { return Results.BadRequest(new { error = "Payload must be UTF-8 text." }); }

    var remoteId = request.Headers["X-Event-Id"].ToString();
    if (remoteId.Length > 128) return Results.BadRequest(new { error = "X-Event-Id is too long." });
    var item = events.Add(source, string.IsNullOrWhiteSpace(remoteId) ? null : remoteId,
        request.ContentType ?? "text/plain", bytes);
    return Results.Json(new { item.Id, item.Source, item.ReceivedAt, item.Sha256, duplicate = item.Duplicate },
        statusCode: item.Duplicate ? StatusCodes.Status200OK : StatusCodes.Status201Created);
});

api.MapGet("/events", (EventStore events, string? source, int? limit) =>
    Results.Ok(events.List(source, Math.Clamp(limit ?? 50, 1, 200))));

api.MapGet("/events/{id}", (string id, EventStore events) =>
{
    var item = events.Get(id);
    return item is null ? Results.NotFound() : Results.Ok(new
    {
        item.Id, item.Source, item.ExternalId, item.ReceivedAt, item.ContentType, item.Sha256,
        body = Encoding.UTF8.GetString(item.Body)
    });
});

api.MapGet("/events/{id}/attempts", (string id, EventStore events) =>
    events.Get(id) is null ? Results.NotFound() : Results.Ok(events.Attempts(id)));

api.MapPost("/events/{id}/replay", async (string id, EventStore events, IHttpClientFactory clients,
    IConfiguration config, CancellationToken cancellationToken) =>
{
    var item = events.Get(id);
    if (item is null) return Results.NotFound();
    var targetText = config["HOOKDESK_REPLAY_URL"];
    if (!ReplayTarget.TryParse(targetText, out var target))
        return Results.Problem("Set HOOKDESK_REPLAY_URL to a loopback HTTP address before replaying.",
            statusCode: StatusCodes.Status409Conflict);

    try
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, target);
        message.Headers.TryAddWithoutValidation("X-Hookdesk-Event-Id", item.Id);
        message.Content = new ByteArrayContent(item.Body);
        message.Content.Headers.TryAddWithoutValidation("Content-Type", item.ContentType);
        using var response = await clients.CreateClient("replay").SendAsync(message, cancellationToken);
        events.RecordAttempt(id, (int)response.StatusCode, null);
        return Results.Ok(new { status = (int)response.StatusCode });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        events.RecordAttempt(id, null, ex.Message);
        return Results.Problem("Replay failed; the attempt was recorded.", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

public partial class Program { }
