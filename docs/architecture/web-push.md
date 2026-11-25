# Web Push, and effects with a due time

Design note (Tatalu round 2, not built). Part 1: a package `Tarol.Messaging.WebPush` that sends notifications to closed apps (lock screen)
with VAPID and RFC 8291 encryption from the BCL. Part 2: effects that run later (`dueAt`) and a digest of several events. Decision ids
`WP-*` (proposed). Labels as in the [conventions](conventions.md).

## 1. The need (Tatalu)

* **FB-F17.4-1**: `@clorat/tarol-client/push` is the SignalR channel for open pages; for closed apps VAPID (RFC 8292), RFC 8291
  encryption, subscription management and the service-worker side are missing. Workaround (`origin/integration/welle-2`):
  `backend/src/Tatalu.Plattform/Benachrichtigungen/WebPush.cs` (`PushOptions`, `VapidKey`, `WebPush.Encrypt`, `IPushSender`,
  `WebPushSender`; ECDH P-256, HKDF, AES-GCM from `System.Security.Cryptography`, AOT-safe), subscriptions as a `[PersonalData]` list of
  `PushTarget(Device, Endpoint, P256dh, Auth)` on the per-person aggregate `Recipient` (`Benachrichtigungen/Recipient.cs:13-91`), changes
  `SubscribePush`/`UnsubscribePush`/`DropExpiredPush`/`UnsubscribeEndedDevice` (`Benachrichtigungen/Changes.cs:119-160`), the effect
  `Tatalu.Host/DeliverNotification.cs`; browser: `frontend/apps/web/src/lib/push.ts` (`subscribePush`, `pushBlocker`, base64url) and the
  `push`/`notificationclick` handlers in `frontend/apps/web/src/lib/offline/sw.ts:127-145`.
* **FB-F17.4-3**: effects have no due time (`DueAt` exists only for reactions). Deferring during quiet hours takes
  event → due reaction → second event → effect: `Notification.Deferred` → `ReleaseNotification` (`[OnEvent<Notification.Deferred>(DueAt = …)]`)
  → `Notification.Queued` → `DeliverNotification`.
* **FB-F15.8-1**: no "deliver by the recipient's rules" (quiet hours, bundling, urgency); a summary of several events in one message is not
  expressible. Workaround: aggregate `ParentInbox` (`Tatalu.Eltern/ParentInbox.cs`), `Delivery.Plan`
  (`Tatalu.Eltern/NotificationPreferences.cs:113`), effect `SendParentNotification`.

## 2. What Tarol has today

* Effects: `IEffect<TEvent>.Run(event, EffectContext, ct)`, registered with `MessagingBuilder.Effect<TEvent, THandler>(name)`
  (`src/Messaging/Tarol.Messaging/Effects.cs:34-50`, `MessagingHost.cs:129-148`). `EffectSet.DueAt` returns `null` and `IsScheduled`
  `false` (`Effects.cs:132-135`).
* The outbox already schedules rows: `IOutboxReactions.DueAt(event, reaction)` and `IsScheduled(reaction)`
  (`src/Persistence/Tarol.Persistence/Outbox.cs:13-29`); reactions implement it from `OnEventAttribute<T>.DueAt`
  (`src/Changes/Tarol.Changes/Reactions.cs:47,159-170`). So a due effect needs no store change.
* `Tarol.Messaging.SignalR`: hub, `UiPush`, topic authorization (`ITopicAuthorizer`) for open pages only.

## 3. Web Push: options

| | Option | For | Against |
|---|---|---|---|
| A | Sender only (VAPID, encryption, HTTP), subscriptions stay the app's | small, no opinion on the domain | every app re-models devices, expiry, erasure of endpoints |
| B | **Sender plus a device aggregate and endpoints** (`PushDevice` per person and browser) | the hard parts (expiry on 404/410, erasure, "devices of a person") once | an aggregate shipped by a package (a first; it needs a stream type name that cannot collide) |
| C | A third-party library (WebPush, Lib.Net.Http.WebPush) | less code | a new runtime dependency (an owner decision, `Directory.Packages.props`), reflection/JSON of their own **[unverified]**; Tatalu proved the BCL is enough |

## 4. Recommendation (WP-1 … WP-4)

**WP-1: option B in `Tarol.Messaging.WebPush`** (depends on `Tarol.Messaging`, `Tarol.Changes.AspNetCore`; BCL crypto only).

```csharp
builder.Services.AddTarol(…).AddMessaging(…).AddWebPush(o => o.Subject = "mailto:ops@example.org");   // key: Tarol:WebPush:PrivateKeyPem
app.MapWebPush("/tarol/push");      // GET key, PUT devices/{device}, DELETE devices/{device}, GET devices (own)

public sealed class NotifyGuardian(IWebPush push) : IEffect<Message.Sent>
{
    public async ValueTask<Result> Run(Message.Sent e, EffectContext context, CancellationToken ct)
        => (await push.Send(e.RecipientSubject, new PushPayload(e.Title, e.Link, Tag: e.Thread.ToString()),
            new PushSendOptions(PushUrgency.Normal, Topic: e.Thread.ToString("N")), context, ct)).ToResult();
}
```

* **WP-2: crypto and wire.** `VapidKey` (ES256 JWT, `aud` = origin of the endpoint, `exp` ≤ 24 h, `k` = uncompressed P-256 point),
  `WebPushCipher.Encrypt` (RFC 8291 `aes128gcm`, one record, salt and server key per message, record size 4096), headers `TTL`, `Urgency`
  (`very-low`/`low`/`normal`/`high`), `Topic` (≤ 32 base64url chars, replaces an undelivered message), `Content-Encoding: aes128gcm`.
  Test vectors of RFC 8291 Appendix A and RFC 8292 are the unit tests. Payload ≤ 4078 bytes (refused otherwise).
* **WP-3: devices.** `PushDevice` aggregate, id `Id<PushDevice>.Derive("tarol-push", subject, device)` (see
  [change-reads.md](change-reads.md) CR-4); `[PersonalData]` endpoint and keys with the person's data subject; events `Subscribed`,
  `Renewed` (new endpoint for the same device), `Removed(reason)` with reasons `user`, `expired` (push service 404/410), `ended` (the app
  ends the session). A send to a person fans out to their devices; an `Expired` outcome runs the system change `RemovePushDevice`.
  `IWebPush.Send` returns per device `Sent`, `Expired`, `Retry(after)` (429/5xx with `Retry-After`) or `Failed`; the effect maps a retry
  to `Error.Unavailable` so the outbox retries.
* **WP-4: private key from configuration** (`IConfiguration`/secrets, PEM), never generated at run time in production; without a key
  `MapWebPush` answers `GET key` with 404 and `Send` returns `NotConfigured` (Tatalu: push optional, mail and in-app remain).
* Browser side (Tarol-Svelte): `tarol-client/web-push` with `subscribe()`, `blocker()`, and a service-worker helper for `push` and
  `notificationclick` (Tatalu's `push.ts`, `sw.ts` as the start).

## 5. Effects with a due time, and digests

**WP-5: `dueAt` for effects (FB-F17.4-3)**, small, no store change:

```csharp
m.Effect<Notification.Requested, DeliverNotification>("deliver-notification", dueAt: e => e.NotBefore);   // null = now
```

`EffectSet.Add` keeps a `Func<TEvent, DateTimeOffset?>` per entry; `DueAt` and `IsScheduled` answer from it like reactions do. The due
time is decided in the change (quiet hours read with `context.Now` and the recipient's settings) and stored on the event, so it is replayed
the same way. Removes Tatalu's `Deferred` → `ReleaseNotification` → `Queued` detour.

**Digest (FB-F15.8-1).** Options: (a) a documented pattern: an aggregate per (recipient, window) collects, a due reaction closes the window,
one effect sends (Tatalu's `ParentInbox`, now with WP-5 shorter); (b) `Digest<TKey>` in the outbox: rows carry a digest key and a due time;
at due time the worker leases all pending rows of that key and calls `IDigestEffect<TEvent>.Run(IReadOnlyList<TEvent>, …)` once. (b) needs
an outbox column, an index and a lease over many rows, and a rule for rows that arrive while the batch runs. **Recommend (a) now, (b) only
after a second application needs it.**

## 6. Not part of this

* Native push (APNs, FCM tokens), e-mail, SMS. The rules "who gets what in which category" (Tatalu's `Recipient.Choices`): app domain.
* Delivery receipts from the service worker (a later endpoint).
* Push to anonymous visitors.

## 7. Open questions for the owner

1. WP-1: ship the `PushDevice` aggregate and endpoints (option B), or only the sender (A)?
2. WP-3: subject of a device: the person's subject derived from the IdP `sub` (a string), or a `Guid` the app supplies?
3. Route prefix and policy of `MapWebPush` (default `/tarol/push`, authenticated users)?
4. Digest: pattern only (recommended) or `Digest<TKey>` in `Tarol.Messaging` now?

## 8. Steps (one PR each)

1. **WP-A** `EffectSet` with `dueAt` (Tarol.Messaging), tests with `MessagingTest` and the Npgsql outbox ordering.
2. **WP-B** `Tarol.Messaging.WebPush`: `VapidKey`, `WebPushCipher`, sender over `IHttpClientFactory`, RFC test vectors, AOT smoke app.
3. **WP-C** `PushDevice`, `MapWebPush`, fan-out and expiry, skill `tarol-web-push`; erasure test (endpoint reads `null`).
4. **WP-D** (Tarol-Svelte) `tarol-client/web-push` and the service-worker helper.
