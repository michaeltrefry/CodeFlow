# HITL notifications

**Epic:** [HITL Event Notifications](https://app.shortcut.com/trefry/epic/48) (epic 48)

CodeFlow notifies humans when a HITL task lands by fanning out provider-neutral events to one or many configured providers (Slack, email, SMS). Failures never block HITL task creation; every attempt is recorded in an audit log.

This doc is the operator-facing setup walkthrough plus the contract for adding a new provider channel without touching HITL orchestration code.

## Architecture at a glance

```
HITL task pending ──► HitlTaskPendingEvent ──► NotificationDispatcher
                                                    │
                              ┌─────────────────────┼─────────────────────┐
                              ▼                     ▼                     ▼
                       Slack provider        Email provider          SMS provider
                       (chat.postMessage)    (SES or SMTP)           (Twilio)
                              │                     │                     │
                              └────────────► notification_delivery_attempts (audit)
```

- **Event emit site:** `AgentInvocationConsumer` in `CodeFlow.Orchestration` publishes `HitlTaskPendingEvent` whenever a new HITL task is persisted.
- **Dispatcher:** single-pass fan-out, per-recipient try/catch, dedupe by `(event, provider, destination)` triple, audit row per outcome — `Sent`, `Failed`, `Skipped`, `Retrying`, or `Suppressed`.
- **Providers:** one factory per channel family; the factory turns N stored config rows into N provider instances at dispatch time.
- **Action URLs:** every notification carries a canonical CodeFlow deep-link built from the `PublicBaseUrl` config. No URL means no notifications go out.

## Prerequisite: set the public base URL

The dispatcher refuses to publish if it can't build a working action URL. Set `CodeFlow:Notifications:PublicBaseUrl` to the user-facing CodeFlow URL (no path):

```bash
export CodeFlow__Notifications__PublicBaseUrl="https://codeflow.example.com"
```

Or in `appsettings.*.json`:

```json
{
  "CodeFlow": {
    "Notifications": {
      "PublicBaseUrl": "https://codeflow.example.com"
    }
  }
}
```

The admin diagnostics endpoint (`GET /api/admin/notifications/diagnostics`) reports `actionUrlsConfigured: false` until this is set, and the admin notifications page surfaces a banner. With it set, action URLs look like `{baseUrl}/hitl?task={id}&trace={guid}`.

## Configuring providers

Provider config lives in MariaDB (`notification_providers` table); credentials are AES-GCM encrypted with the same `Secrets:MasterKey` the rest of CodeFlow uses. The recommended path for adding a provider is the admin UI at `/settings/notifications` — every endpoint sits behind the `NotificationsWrite` policy (admin role only).

Each provider config row has:

- `id` — string, used to reference the provider from routes (e.g. `slack-prod`, `email-ops`)
- `displayName` — human-readable label
- `channel` — `Slack`, `Email`, or `Sms`
- `endpointUrl` — optional override (mostly unused; Slack base URL is hard-coded, SES region drives endpoint)
- `fromAddress` — sender for email; from-phone or Messaging Service SID for SMS; ignored for Slack
- `additionalConfigJson` — channel-specific opaque blob (see per-provider tables below)
- `credential` — write-only; never returned by the API; encrypted at rest
- `enabled`, `isArchived` — runtime gating

### Slack

| Field | Value |
| --- | --- |
| `channel` | `Slack` |
| `fromAddress` | (unused, leave blank) |
| `additionalConfigJson` | optional, e.g. `{"workspace":"acme"}` |
| `credential` | bot token starting with `xoxb-` |
| Recipient `address` | channel id like `C012AB3CD` or user id like `U…` |

The Slack provider uses the bot token to call `chat.postMessage` and validates with `auth.test`. Errors namespace as `slack.{slack_error_code}`, plus dispatcher-level codes (`slack.transport_error`, `slack.timeout`, `slack.missing_credential`, `slack.empty_response`).

### Email — Amazon SES

| Field | Value |
| --- | --- |
| `channel` | `Email` |
| `fromAddress` | a verified SES sender, e.g. `ops@example.com` |
| `additionalConfigJson` | `{"engine":"ses","region":"us-east-1"}` |
| `credential` | optional. With creds: `{"access_key":"AKIA…","secret_key":"…"}`. Leave blank to use the default AWS SDK credential chain (IAM role, etc.) |

Errors namespace as `email.ses.{snake_case_aws_error}`, plus `email.ses.transport_error`.

### Email — SMTP

| Field | Value |
| --- | --- |
| `channel` | `Email` |
| `fromAddress` | sender address (must match SMTP relay rules) |
| `additionalConfigJson` | `{"engine":"smtp","host":"smtp.relay.example.com","port":587,"username":"app@example.com","useStartTls":true}` |
| `credential` | SMTP password (plain string, not JSON) |

`port` defaults to 587 and `useStartTls` defaults to true. MailKit opens one connection per send; auth failures, TLS handshake failures, protocol errors, and timeouts each surface as distinct error codes (`email.smtp.auth_failed`, `email.smtp.tls_failed`, `email.smtp.command_NNN`, `email.smtp.protocol_error`, `email.smtp.timeout`, `email.smtp.transport_error`, `email.smtp.invalid_address`).

### SMS — Twilio

| Field | Value |
| --- | --- |
| `channel` | `Sms` |
| `fromAddress` | E.164 phone number (`+15551234567`) or Messaging Service SID (`MG…`) |
| `additionalConfigJson` | none required for v1 |
| `credential` | `{"account_sid":"AC…","auth_token":"…"}` |
| Recipient `address` | E.164 phone number |

Twilio errors map to `sms.twilio.{numeric_error_code}` (e.g. `sms.twilio.21211` for "invalid To phone number"); transport-level failures use `sms.twilio.unauthorized`, `sms.twilio.http_{status}`, `sms.twilio.transport_error`, `sms.twilio.timeout`, `sms.twilio.missing_recipient`, `sms.twilio.missing_from_address`, `sms.twilio.empty_response`.

### Validate + test send

Two admin-only endpoints sit on the provider config:

- `POST /api/admin/notification-providers/{id}/validate` — calls the provider's `ValidateAsync` (Slack `auth.test`, Twilio account-fetch, etc.) without sending any message. Surfaces structured validation errors.
- `POST /api/admin/notification-providers/{id}/test-send` — synthesises a `HitlTaskPendingEvent` with random GUIDs and `HitlTaskId=0`, renders an optional template (or a built-in "[CodeFlow] Test notification" body), and sends through the real provider. **Bypasses the dispatcher** so no audit row is written.

## Routing

Routes are global today (per-workflow scoping is deferred). Each route maps a `NotificationEventKind` to a single provider plus a recipient list and a template ref:

```jsonc
PUT /api/admin/notification-routes/route-hitl-pending-slack
{
  "eventKind": "HitlTaskPending",
  "providerId": "slack-prod",
  "recipients": [
    { "channel": "Slack", "address": "C012AB3CD", "displayName": "#hitl-queue" }
  ],
  "template": { "templateId": "hitl-task-pending/slack/default", "version": 1 },
  "minimumSeverity": "Normal",
  "enabled": true
}
```

- **Recipient channels must match the provider channel.** The API returns a 400 on mismatch.
- **`minimumSeverity`** filters at dispatch time. Events below the threshold record a `Skipped` audit row; nothing fires.
- **`template.version`** is pinned at config time, not resolved-latest at dispatch time, so editing a template never silently changes what production routes send.
- Multiple routes for the same `eventKind` fan out in parallel; one event becomes N audit rows.

## Templates

Templates render via the same Scriban renderer the rest of CodeFlow uses, with a snake_case naming policy applied to the event JSON. `HitlTaskPendingEvent` exposes:

| Variable | Type | Notes |
| --- | --- | --- |
| `event_id` | guid string | Stable per-event id |
| `occurred_at_utc` | ISO 8601 timestamp | |
| `action_url` | string | Required deep-link; safe to embed |
| `severity` | `Info` / `Normal` / `High` / `Urgent` | |
| `hitl_task_id` | long | |
| `trace_id`, `round_id`, `node_id` | guid strings | |
| `workflow_key`, `workflow_version` | string + int | Workflow definition pinned at dispatch |
| `agent_key`, `agent_version` | string + int | Suspended agent |
| `hitl_task_created_at_utc` | ISO 8601 timestamp | |
| `input_preview` | string | ≤ ~2 KiB; may be null |
| `input_ref` | string | Artifact URI; may be null |
| `subflow_path` | string | Slash-delimited; null at top level |

Channel-specific authoring tips:

- **Slack**: keep the body short — `chat.postMessage` accepts up to 40 KB, but mobile previews truncate. Use `*bold*` and link with `<{{ action_url }}|Review now>`.
- **Email**: subject is mandatory (Scriban template); body can be plain text or HTML.
- **SMS**: target ≤ 160 chars to avoid multi-segment billing. Drop `input_preview` for SMS bodies; include `action_url` for the deep-link.

Template authoring + a Scriban-backed editor land in [sc-63](https://app.shortcut.com/trefry/story/63); for now templates seed via the `notification_templates` table directly or through the persistence repository in scripts.

## Delivery audit

Every dispatcher attempt writes one row to `notification_delivery_attempts` with `status`, `attempt_number`, `normalized_destination` (secret-stripped), `provider_message_id` on success, and `error_code` + `error_message` on failure. The unique index on `(event_id, provider_id, normalized_destination, attempt_number)` prevents duplicate audit rows under MassTransit redelivery races.

Read paths:

- `GET /api/admin/notification-delivery-attempts` (sc-59) — filters by `eventId`, `providerId`, `routeId`, `status`, `sinceUtc`; cursor-paginated. Newest-first.
- Admin UI: `/settings/notifications` → "Delivery attempts" panel mirrors the API filters.
- Direct SQL: the table is plain MariaDB, queryable for dashboards or postmortems.

Status meanings:

| Status | Meaning |
| --- | --- |
| `Sent` | Provider accepted the message (Slack `ok=true`, Twilio `queued`, SMTP `250`, SES `MessageId` returned). |
| `Failed` | Provider rejected the message; `error_code` carries the namespaced reason. |
| `Skipped` | Dispatcher intentionally didn't send — dedupe hit (`dispatcher.dedupe_already_sent`), severity below route minimum, route disabled, or admin policy. |
| `Retrying` | Reserved for future retry policy; not emitted by today's single-pass dispatcher. |
| `Suppressed` | Reserved for future do-not-disturb / quiet-hours logic. |

### Retry semantics today

The dispatcher does **single-pass** fan-out per event. Retries come from MassTransit message redelivery (saga retry pipeline) — the dedupe check (`LatestForDestinationAsync`) ensures a successful delivery isn't re-attempted on redelivery. A `Failed` attempt followed by message redelivery results in a second audit row with `attempt_number = 2`. There is no internal exponential-backoff loop inside `NotificationDispatcher`.

## Adding a new provider channel

The cohort of `INotificationProvider` + `INotificationProviderFactory` + per-channel registration is the contract. Adding a new channel does not touch HITL orchestration, the dispatcher, the persistence schema, or the admin UI shell — only the provider extension surface.

### 1. Pick a channel value

Add a new value to `NotificationChannel` in `CodeFlow.Contracts/Notifications/NotificationChannel.cs`. The dispatcher fans out by channel; the registry routes by channel; the UI lets admins pick from `NOTIFICATION_CHANNELS`.

### 2. Implement the provider

Create a new directory under `CodeFlow.Orchestration/Notifications/Providers/<Channel>/` with:

```csharp
public sealed class FooNotificationProvider : INotificationProvider
{
    public string Id { get; }
    public NotificationChannel Channel => NotificationChannel.Foo;

    public Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message, NotificationRoute route, CancellationToken ct);

    public Task<ProviderValidationResult> ValidateAsync(CancellationToken ct);
}
```

Provider implementations **must**:

- Strip secrets before populating `NotificationDeliveryResult.NormalizedDestination`. The dispatcher persists this verbatim into the audit table.
- Catch transport-level exceptions and return a `Failed` result with a namespaced `error_code` (`foo.{specific_code}`, `foo.transport_error`, `foo.timeout`, etc.). Do **not** throw — the dispatcher records `dispatcher.provider_threw` for unhandled exceptions, which is uglier audit than a structured Failed row.
- Treat `additionalConfigJson` as a parsed strongly-typed settings record. Throw `*SettingsException` from a `Parse` method; the factory catches and surfaces it.

### 3. Implement the factory

```csharp
public sealed class FooNotificationProviderFactory : INotificationProviderFactory
{
    public NotificationChannel Channel => NotificationChannel.Foo;

    public Task<INotificationProvider> CreateAsync(
        NotificationProviderConfigWithCredential config, CancellationToken ct);
}
```

The factory turns one stored config row into one provider instance at dispatch time. Use a named `IHttpClientFactory` client if the provider talks HTTP — connection pooling and DNS rotation matter for long-running dispatchers.

### 4. Register in `HostExtensions`

Add to `CodeFlow.Host/HostExtensions.cs` next to the existing factory registrations:

```csharp
services.AddHttpClient(FooNotificationProviderFactory.HttpClientName, client =>
{
    client.BaseAddress = FooNotificationProviderFactory.DefaultBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(10);
});
services.AddSingleton<FooNotificationProviderFactory>();
services.AddSingleton<INotificationProviderFactory>(sp =>
    sp.GetRequiredService<FooNotificationProviderFactory>());
```

### 5. Wire the channel into the admin UI

The admin notifications page (`codeflow-ui/src/app/pages/settings/notifications/notifications.component.ts`) drives `NOTIFICATION_CHANNELS` and `NOTIFICATION_DELIVERY_STATUSES` from `core/models.ts`. Add the channel value plus per-channel placeholders for `recipientPlaceholder`, `fromPlaceholder`, `credentialPlaceholder`, `additionalConfigPlaceholder`, and `credentialHelp`.

### 6. Tests

Mirror the existing test pattern under `CodeFlow.Orchestration.Tests/Notifications/Providers/<Channel>/`:

- A factory test that verifies config parsing.
- Provider tests that mock the wire client and assert the right `error_code` for each transport failure shape.
- An optional integration test that hits the real provider behind an env-var-gated flag.

## Reference

- Domain contracts: `CodeFlow.Contracts/Notifications/`
- Persistence schema + repositories: `CodeFlow.Persistence/Notifications/`
- Dispatcher + factories + providers: `CodeFlow.Orchestration/Notifications/`
- Admin API: `CodeFlow.Api/Endpoints/NotificationsEndpoints.cs`
- Admin UI: `codeflow-ui/src/app/pages/settings/notifications/`
- Tests: `CodeFlow.Api.Tests/Integration/NotificationsEndpointsTests.cs`, `CodeFlow.Orchestration.Tests/Notifications/`
