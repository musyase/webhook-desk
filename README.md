# Webhook Desk

A small local service for capturing a webhook and replaying its exact body to a development server. It is useful when a provider will not resend a failed event or when a handler needs the same input several times during debugging.

The inbox stores UTF-8 payloads in SQLite. If the sender supplies `X-Event-Id`, a second request with the same source and ID returns the original record. Replays go only to a loopback HTTP address. Each attempt records the target's status code or the connection error.

## Try it locally

Requires .NET 10. Set a token and a replay target, then start the service in PowerShell:

```powershell
$env:HOOKDESK_TOKEN = 'local-demo-token-123456'
$env:HOOKDESK_DATA_DIR = "$PWD/data"
$env:HOOKDESK_REPLAY_URL = 'http://127.0.0.1:9001/hook'
dotnet run --project src/WebhookDesk --urls http://127.0.0.1:5166
```

In a second terminal, start the sample receiver:

```powershell
py scripts/receiver.py
```

In a third terminal, submit an example event. The response contains an `id` for the replay request:

```powershell
$headers = @{ 'X-Hookdesk-Token' = 'local-demo-token-123456'; 'X-Event-Id' = 'order-1042' }
$event = Invoke-RestMethod -Uri http://127.0.0.1:5166/api/inbox/shop -Method Post -Headers $headers -ContentType 'application/json' -Body '{"order":1042,"state":"paid"}'
$event
Invoke-RestMethod -Uri "http://127.0.0.1:5166/api/events/$($event.id)/replay" -Method Post -Headers $headers
Invoke-RestMethod -Uri "http://127.0.0.1:5166/api/events/$($event.id)/attempts" -Headers $headers
```

The receiver prints the original JSON. To test idempotency, send the same request again. The response has `duplicate: true` and the same event ID. Omit `X-Event-Id` to store each delivery separately.

## API

| Method | Path | Result |
| --- | --- | --- |
| `GET` | `/health` | Process health; no token required |
| `POST` | `/api/inbox/{source}` | Store a UTF-8 payload up to 256 KiB |
| `GET` | `/api/events?source=shop&limit=50` | List metadata, without bodies |
| `GET` | `/api/events/{id}` | Inspect one payload |
| `POST` | `/api/events/{id}/replay` | Post the stored body to the configured target |
| `GET` | `/api/events/{id}/attempts` | Inspect replay outcomes |

All `/api` routes require `X-Hookdesk-Token`. The token must contain at least 16 characters. Event bodies are not redacted, so use a disposable local database for examples and avoid storing real secrets. The service is intended to bind to localhost. It does not provide user accounts, TLS, or retention controls for a public deployment.

The replay URL must use `http` and a loopback host. HTTP redirects are not followed. This keeps a captured payload from being forwarded to another host through a redirect.

## Implementation

The API uses ASP.NET Core minimal routes. `EventStore` opens a SQLite connection per operation and keeps a unique index on `(source, external_id)` for idempotency. The original bytes are stored as a BLOB, so replay does not reserialize JSON. The test suite exercises HTTP requests through `WebApplicationFactory`, including deduplication and a fake replay receiver.

Run the tests with `dotnet test WebhookDesk.slnx`. GitHub Actions runs them on Windows and Ubuntu.
