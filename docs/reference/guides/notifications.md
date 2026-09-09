---
id: guide-notifications
title: "Failure notifications: email and Slack alerts with digest and anti-spam controls"
type: guide
summary: Opt-in email (SMTP or Microsoft Graph) and Slack alerts for failed runs and assertions, immediate or digest, with anti-spam coalescing and lifecycle gating.
keywords:
  - notifications
  - alerts
  - email
  - smtp
  - microsoft graph
  - slack
  - digest
  - subscriptions
  - lifecycle
  - anti-spam
related:
  - flow-overview
  - guide-deployment
  - concept-connections-and-secrets
sourceRefs:
  - src/SqlFlow.ControlPlane/Background/NotificationService.cs
  - src/SqlFlow.ControlPlane/Notifications/NotificationComposer.cs
  - src/SqlFlow.ControlPlane/Api/NotificationEndpoints.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.Catalog/NotificationStore.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
---

# Failure notifications: email and Slack alerts with digest and anti-spam controls

The control plane watches the run history and tells the users who asked when something goes wrong: a run failed, was cancelled, was skipped because an upstream dependency broke, or finished green while one of its data-quality assertions failed to evaluate (assertions are log-only, so that last case is invisible in the run status and used to be easy to miss). Notifications are strictly opt-in and per user: nobody receives anything until they create a subscription, and every subscription belongs to exactly one account.

## The pipeline at a glance

Every poll tick (30 seconds by default) the notification service in the control plane runs three claim-based phases over the shadow catalog, so any number of control-plane replicas can run it without double-sending, and a restart never loses a message:

1. **Detect.** Non-success terminal runs and green-runs-with-failed-assertions are turned into rows in `catalog.NotificationEvent`, deduplicated by a unique (run, kind) index and bounded by a watermark over `Run.WrittenUtc`. Runs of pipelines declared `lifecycle: development` are skipped here, at the source.
2. **Dispatch.** Each due subscription is claimed (compare-and-swap on its next-due instant, the same discipline the scheduler uses) and everything pending since its private cursor is composed into ONE message: grouped per flow with repeat counts ("failed x12"), the latest error excerpt, and links into the GUI. The cursor advances, so no event is ever reported twice to the same subscription.
3. **Send.** Composed messages sit in a durable outbox (`catalog.NotificationDelivery`). The send loop claims a row, hands it to the channel (email or Slack), and records the outcome. Transient failures retry with backoff (1, 5, 15, then 60 minutes, 5 attempts by default); permanent ones (an unknown Slack user, a rejected recipient) are recorded as failed immediately. A row stuck mid-send by a dying node is requeued automatically.

## Pacing: right away, or as a digest

A subscription chooses one of two modes; both deliver ONE combined message per window, never one message per event:

- **`immediate`**: the first matching event after a quiet period alerts within one poll tick. After a message goes out, the subscription holds for its **cooldown** (5 minutes by default, 0 to 1440): everything that happens inside the cooldown is coalesced into the next message, never dropped. A failure storm therefore caps at one message per cooldown window per subscription.
- **`digest`**: one summary per fixed interval (six hours by default, 5 minutes to one week), covering everything since the previous digest. Empty windows send nothing, and windows missed while the host was down are folded into the next message rather than backfilled one by one.

## What a subscription selects

- **Channel**: `email` or `slack` (one per subscription; create several subscriptions to combine, for example an immediate Slack DM plus a daily email digest).
- **Event kinds**: any of `run_failed`, `run_cancelled`, `run_skipped`, `assertion_failed`. New subscriptions default to `run_failed` + `assertion_failed`; skipped-run echoes of an upstream failure are opt-in by design.
- **Flow filter**: optional comma-separated wildcard patterns over flow names (`sales_*, finance_??_load`), matched case-insensitively. Empty means every flow.
- **Destination**: email subscriptions use the account email unless an override address is set (so a directory-driven address change follows automatically); Slack subscriptions post to an explicit channel id (`C0123ABCD`) or, when none is set, direct-message the user (their Slack account is resolved from their email, which needs the `users:read.email` and `im:write` bot scopes).
- **Enabled**: a disabled subscription keeps its settings but sends nothing; re-enabling fast-forwards past everything that happened while it was off, so resuming never floods.

Opting in never replays history: a new subscription's cursor starts at the newest existing event.

## Lifecycle gating: development pipelines never alert

Every flow document can declare a top-level `lifecycle:` (see the flow-overview page). `production` (the default) alerts subscribers on failure; `development` runs, schedules, and records history identically but generates no notification events at all, so iterating on a half-built flow cannot page anyone:

```yaml
flowType: ing
name: orders-load
lifecycle: development   # remove (or set to production) when the flow goes live
```

The gate is applied at detection time against the synced catalog pipeline, so it takes effect on the next catalog sync after the YAML changes. A run whose pipeline has left the estate entirely still alerts: that failure is real and nobody declared it development.

## Configuration

Everything lives under `ControlPlane:Notifications` (appsettings or `ControlPlane__Notifications__...` environment variables). The service always runs; a channel is offered to users only when its section is configured, and the GUI hides what cannot send. Secrets are `${env:...}` / `${keyvault:...}` references resolved at send time (never literals), so rotating a password or token heals in-flight retries without a restart.

```jsonc
"Notifications": {
  "Enabled": true,                  // master switch for the whole pipeline
  "PollSeconds": 30,                // tick cadence = the immediate-mode latency bound
  "DetectionOverlapMinutes": 30,    // re-scan window absorbing writer clock skew (dedup makes it free)
  "MaxDeliveryAttempts": 5,
  "EventRetentionDays": 30,         // detected events (audit) are pruned after this
  "DeliveryRetentionDays": 90,      // sent/failed messages are pruned after this
  "GuiBaseUrl": "https://sqlflow.example.com",   // enables "Open run" links in messages
  "Email": {
    "Provider": "none",             // none | smtp | graph
    "FromAddress": "sqlflow@example.com",
    "FromDisplayName": "SQLFlow",
    "Smtp": {
      "Host": "smtp.example.com",
      "Port": 587,
      "SslMode": "starttls",        // starttls | ssl | auto | none
      "UsernameReference": "${env:SQLFLOW_SMTP_USER}",
      "PasswordReference": "${env:SQLFLOW_SMTP_PASSWORD}"
    },
    "Graph": {
      "SenderId": "alerts@contoso.com",          // the sending mailbox (UPN or object id)
      "TenantId": null,                          // set all three for an explicit app registration,
      "ClientId": null,                          // or leave null to use the ambient Azure credential
      "ClientSecretReference": null              // (managed identity in Azure)
    }
  },
  "Slack": {
    "BotTokenReference": "${env:SQLFLOW_SLACK_BOT_TOKEN}"   // xoxb-..., scope chat:write
  }
}
```

### Email via SMTP

Any relay works: set `Provider` to `smtp`, the host/port/TLS mode, and (for an authenticating relay) the credential references. Messages are multipart/alternative (plain text plus HTML), sent through MailKit.

### Email via Microsoft Graph

For Microsoft 365 estates, `Provider: graph` sends through `POST /users/{SenderId}/sendMail`: no SMTP relay, no mailbox password. The identity calling Graph needs the **application** permission `Mail.Send` (admin-consented), ideally bounded to the sending mailbox with an Exchange application access policy. Identity options:

- **Managed identity / ambient credential** (recommended in Azure): leave `TenantId`/`ClientId`/`ClientSecretReference` null; the control plane's own Azure credential (the same `SQLFLOW_AZURE_AUTH` intent the engine uses) calls Graph.
- **Explicit app registration**: set all three values; the client secret is a secret reference.

### Slack

Create (or reuse) a Slack app with a bot token. Required scopes: `chat:write` to post; `users:read.email` and `im:write` if subscribers will use direct messages; and invite the bot to any explicit channel it should post into. The same Slack app the SQLFlow Slack assistant uses works: this adds outbound posting alongside its inbound Q&A.

## Using it

In the GUI, the account menu's **Notification settings** page (`/settings/notifications`) creates and edits subscriptions, shows the recent delivery history (including why a message failed), and sends a test message per subscription. The same surface is available on the API for automation, all self-service under the caller's own account:

| Endpoint | What it does |
| --- | --- |
| `GET /api/v1/me/notifications/options` | Channel availability, valid kinds/modes, defaults, and your account email |
| `GET/POST /api/v1/me/notifications/subscriptions` | List / create subscriptions |
| `PUT/DELETE /api/v1/me/notifications/subscriptions/{id}` | Edit / remove one |
| `POST /api/v1/me/notifications/subscriptions/{id}/test` | Queue a test message through the real pipeline |
| `GET /api/v1/me/notifications/deliveries` | Recent messages: status, attempts, and the last error |

A test send exercises the exact production path (composition, destination resolution, credentials, transport), so a green test means real alerts will arrive.

## Operational notes

- **Multi-replica safe**: detection is serialized by a lock on the single watermark row; subscription windows and outbox sends are claimed by compare-and-swap. Running several control-plane replicas neither duplicates nor drops messages.
- **At-least-once**: a crash between composing and recording can repeat a window in rare cases; a message is never silently lost. The delivery history is the audit trail.
- **Retention**: events and terminal deliveries are pruned on the configured horizons; queued work is never pruned.
- **Late-synced history never alerts**: a run artifact synced in days after the fact (CLI runs imported by `db sync`) falls outside the detection window by design; notifications are about what just went wrong.
