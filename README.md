# Neko

A local, headless conversation host with a JSON API, command-line client, and an authenticated single-page transport. The host and transport are part of this repository. No desktop UI is required.

## Requirements

Windows, .NET 10 SDK, WebView2 Runtime, and an existing user-authorized session in the configured browser profile. The service is designed to run on the same computer as that session. Neko does not store or generate credentials for remote services.

## Setup

1. Copy `secrets.example.json` to `secrets.json`. **Only the latter is private**; do not commit it.
2. Supply the locally authorized WebView2 profile directory and the service origin, route templates, module lookup names, and wire property names under `transport`. These values are service-specific and intentionally not hardcoded into the adapter.
3. Supply the scope identifier you are authorized to use under `creationScope`, and generate a random localhost bearer `accessToken` of at least 24 characters.
4. Start Neko:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-headless.ps1
```

This script builds both the headless host and the tracked transport, copies the native WebView2 runtime dependency, and runs the server with the ignored local configuration.

### CLI

From another terminal:

```powershell
dotnet run --no-build -c Release --project src/Neko.Headless -- health --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- create "Hello" --wait --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- list --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- send <conversation-id> "Continue" --wait --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- read <conversation-id> --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- status <conversation-id> --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- watch <conversation-id> --config secrets.json
```

### HTTP API

The server binds to `127.0.0.1` on the configured port. Every endpoint requires the locally configured bearer token.

| Endpoint | Method | Description |
| --- | --- | --- |
| `/health` | GET | Health check |
| `/conversations` | GET | Locally tracked conversations |
| `/conversations` | POST | Create a conversation in the configured scope |
| `/conversations/{id}` | GET | 200: transcript (refreshed from provider, or last saved checkpoint); 202: known ID but no history yet; 404: unknown ID |
| `/conversations/{id}/status` | GET | Current local operation status and public-event progress metadata |
| `/conversations/{id}/messages` | POST | Continue an existing conversation |
| `/conversations/{id}/events` | GET | Server-sent incremental text updates |
| `/conversations/{id}/stop` | POST | Returns `Unsupported` / `stop_not_supported` without changing conversation state; remote stop is **not implemented** |

A known conversation whose provider history and local checkpoint are both unavailable returns HTTP **202 Accepted** with `{"status":"pending","reason":"provider_history_unavailable","conversationId":"..."}`. This means no transcript is available yet, **not** that generation has completed or that messages are empty. Retry the read later; `GET /conversations/{id}/status` separately reports generation state. When history becomes available, the same GET returns its original 200 transcript shape. Unknown IDs remain HTTP 404. The CLI prints pending JSON and an explanatory diagnostic with success exit status; unknown IDs produce a specific error and a nonzero exit status.

Create/send JSON receipts (also printed unchanged by the CLI) now include optional `retryable` and `retryAfterSeconds` fields. `retryable: true` means the particular request was definitively rejected and a **new, explicitly requested** guarded attempt is safe; it does not promise eventual success. `retryAfterSeconds` is the bounded, rounded-up **remaining local provider cooldown** when known, not a guarantee of availability. Null means unknown or inapplicable, **not** retry immediately. `Accepted` and `Uncertain` must never be automatically resent, regardless of those fields. Existing `disposition`, `reason`, and operation state meanings are unchanged.

### Observing long-running generations

`neko status <conversation-id>` prints the same JSON as authenticated
`GET /conversations/{id}/status`. Existing `id`, `status`, and `updatedAt`
fields are unchanged. Additive fields:

| Field | Meaning |
| --- | --- |
| `startedAt` | UTC time when this host started tracking the current local turn attempt (for follow-ups, includes the `sending` phase) |
| `elapsedMilliseconds` | Monotonic duration since `startedAt`, frozen when the public feed terminates |
| `lastPublicUpdateAt` | UTC time the host most recently published a public update |
| `publicEventCount` | Number of accepted public-feed events this process observed for this turn, including a host-generated terminal `Fail` when a stream ends unexpectedly |
| `lastPublicUpdateType` | Public event kind and operation, e.g. `Final.Append` or `Activity.Start`; no text, title, message ID, or tool details |
| `isTerminal` | Whether the public event feed ended with `Final.Complete`, `Final.Fail`, or `Final.ObservationExpired` |
| `terminalOperation` | `Complete`, `Fail`, or `ObservationExpired` when such a terminal update was published |

For a live turn with **zero public events**, `publicEventCount` is `0`;
the last-update fields are `null`, and elapsed time still increases.
After a host restart, no in-memory event feed remains: progress fields are
`null`, **not** an invented count of zero, even if the journal retains the
conversation's lifecycle status. Each accepted new turn resets the metrics;
a follow-up's `sending` attempt starts a fresh feed before remote submission.
A rejected or unconfirmed send may therefore have a locally terminal `Fail`
while the journal separately reports `idle` or `uncertain`.
If terminal journal persistence fails, Neko closes the public feed with
`Final.Fail` instead of falsely acknowledging a durable completion. The
journal is then marked `uncertain` when possible; if storage remains unavailable,
the persisted `status` may be stale even though `isTerminal=true` reports that
the local public feed has ended. Operators must reconcile the remote outcome
before retrying.

These metrics describe **only public updates that Neko has received and
published**, plus explicit local failure signals. They cannot determine
whether the upstream model is thinking, using unseen tools, stalled, or
making progress when no public events arrive. No extra upstream polling is
performed. `isTerminal=true` means the **local public feed** ended, not
necessarily remote success; `Fail` and `uncertain` never confirm completion.
No event contents, credentials, or private reasoning are exposed by the new
status fields.

## Persistent manager identities

A manager has a stable, caller-chosen **logical ID** such as `manager/main`,
independent of its underlying conversation ID. The URL uses the name portion
(`main`); all manager records and responses retain the full logical ID.
Manager names consist of lowercase letters, digits, underscores and hyphens,
starting with a letter or digit, up to 64 characters.

| Endpoint | Method | Description |
| --- | --- | --- |
| `/managers` | POST | `{"id":"manager/main","text":"Initial instructions"}`; durably reserve identity before the remote creation |
| `/managers` | GET | List persisted manager metadata, without probing the provider |
| `/managers/main` | GET | Lookup stable identity and probe current provider readability; 404 means unknown |
| `/managers/main/messages` | POST | `{"text":"Next action"}`; submit through the existing guarded conversation runtime |
| `/managers/main/history` | GET | Read the mapped transcript; 202 means no readable history yet, 404 means unknown |

Creation returns `manager` metadata and the original `receipt`. Repeating
creation for an existing or uncertain manager returns 409
(`manager_already_exists`) without another provider call. A **definitively
rejected** creation leaves the same manager ID in `rejected`; a new,
explicit POST may try creation again. An `Uncertain` result cannot be retried
automatically or by a repeated create request. An accepted remote creation
without a valid, durably confirmed conversation binding is instead returned as
`Uncertain`, preserving the logical manager for manual reconciliation.
If a request is already cancelled, manager creation throws before reserving
an ID and creates no registry record or receipt. Cancellation while waiting for
the local creation gate after reservation instead produces a retryable
`Rejected` receipt for the same manager ID. Cancellation during send history
preflight can restore the manager to `idle` with a retryable rejection.
Cancellation after remote dispatch remains `Uncertain`, never an automatic
retry signal.

Registry records live under the configured, untracked
`stateDirectory/managers/`, protected by the existing host lease. Each
contains the logical ID, mapped remote conversation ID when known, last
locally verified lifecycle status, a relative hashed checkpoint filename
pointing into the existing conversation journal, an empty reserved array for
future job references, and creation/update timestamps. No credentials,
transport settings, or provider implementation details are written. The
conversation transcript remains in the existing journal, not in the registry.

A manager GET returns `availability: "available"` only after a successful
provider history read. `temporarily_unavailable` means **not verified by
this read**; it does not prove the remote conversation is permanently gone.
`unverified` on list is not a health claim, and `not_bound` means a
conversation ID has not been safely established. The `lifecycleState`
describes durable local operation state, not hidden remote progress.
A temporary read failure never rotates the conversation and never erases its
mapping. The existing per-conversation history preflight still governs sends.

Interrupted `creating` or `sending` records become `uncertain` on
restart, with no automatic replay. A crash after successful remote creation
but before persisting its manager binding can leave an unresolved manager
alongside a separately journaled conversation; reconciliation requires
operator evidence, not an automatic guess. Existing conversation endpoints,
CLI responses, and their at-most-once safeguards are unchanged. Manager
rollover and automatic uncertain recovery are not implemented.

### Durable manager inbox

Use the inbox to accept work when a manager is busy, offline, or still being
created. An HTTP **202** enqueue response means *durably queued locally*,
not submitted remotely. Each message has a stable ID and creation sequence.
An optional `idempotencyKey` is unique within that manager; resubmitting
identical text with the same key returns the same entry (HTTP 200), while
a different payload with that key returns HTTP 409. Keys contain up to 128
ASCII letters, digits, `-`, `_`, `.`, or `:`.

| Endpoint | Method | Description |
| --- | --- | --- |
| `/managers/main/inbox` | POST | `{"text":"Next task","idempotencyKey":"request-42"}`; persist before acknowledgement |
| `/managers/main/inbox` | GET | List ordered delivery metadata without message text |
| `/managers/main/inbox/{messageId}` | GET | Inspect one message's state, timestamps, correlation, and receipt evidence |
| `/managers/main/inbox/{messageId}/cancel` | POST | Cancel only while still queued; otherwise 409 |
| `/managers/main/inbox/{messageId}/retry` | POST | Explicitly requeue only a definitively rejected, retryable submission; otherwise 409 |

The CLI equivalents are `manager-queue manager/main "text" --key request-42`,
`manager-messages manager/main`, `manager-message manager/main <id>`,
`manager-cancel manager/main <id>`, and `manager-retry manager/main <id>`.

State progression is `queued` -> `dispatching` -> `accepted` ->
`generating` -> `completed` for a verified successful turn. Other outcomes
are `rejected` (definitive, no provider acceptance), `uncertain` (blocked
until exact authoritative reconciliation), and `cancelled` (never sent).
The single in-flight worker dispatches only entries still definitively queued;
later queued messages wait for the preceding accepted turn to finish.
A definitive rejection terminates that attempt without permanently stalling
later messages. Manual retry rearms that same logical message and retains its
previous receipt evidence. Known cooldowns prevent premature submission; an
unreadable remote history requeues without claiming it was sent.

A crash between dispatch reservation and verified receipt, or failure of
receipt persistence, leaves an uncertainty guard, **not** a replayable queue
entry. The existing `reconcile-manager manager/main` command and
`POST /managers/main/reconcile` are the only remote reconciliation path.
Only a durable verification of the exact operation ID can unblock the inbox.
No background reconciliation or resubmission of accepted/uncertain work occurs.

The journal resides under untracked `stateDirectory/manager-inbox/`,
protected by the existing exclusive host lease. It retains message text,
including completed entries, and metadata locally; authenticated API
list/inspect responses exclude the text. Retention and bounded archive
compaction are not yet implemented, so very large journals require monitoring.
The older `/managers/main/messages` endpoint remains available for immediate
guarded sends, but returns `manager_inbox_pending` while an inbox message
is outstanding, protecting its unconsumed completion evidence.

## Architecture and reliability

The single WebView2 page supplies the signed-in execution environment. The adapter invokes the site's existing submission function with explicit conversation, parent and scope values. It does not navigate to individual conversations or interact with composer buttons. Read-only network observation captures live response updates. Server history verifies completion.

The transport is deliberately isolated in `src/Neko.Transport`. Its configured origin, module export name, route patterns, headers, scope format and other service identifiers are read from `secrets.json`. The public host consumes only the neutral conversation contract in `src/Neko.Core`.

- An `Accepted` submission is **not** a completed response. The conversation remains active until its terminal event or authoritative server history confirms completion.
- Ambiguous sends and creations are **never** automatically retried. A crash can leave a pending checkpoint that requires reconciliation before another request.
- A temporary server read failure must not be mistaken for successful completion.
- Creation verifies remote membership of the required scope; an unverified new conversation is not reported as accepted.
- The local journal under `state/` stores the latest conversation snapshots and operation states. Session files, local checkpoints and secrets are ignored by Git.

### Explicit reconciliation of uncertain operations

Reconciliation is a **read-only remote verification**, invoked explicitly by an
operator. No uncertain or accepted submission is automatically retried. The
host retains the original prompt digest, ordered pre-send message IDs, any
confirmed admission evidence and the final reconciliation decision in durable
checkpoints. It does not choose a remote conversation by matching prompt text.

With the existing authenticated local service running, use:

```powershell
dotnet run --no-build -c Release --project src/Neko.Headless -- reconcile <conversation-id> --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- reconcile-manager manager/main --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- reconcile-creation --config secrets.json
```

Corresponding authenticated JSON API operations are `POST
/conversations/{id}/reconcile`, `POST /managers/{name}/reconcile`, and
`POST /conversations/creation/reconcile`. These return `outcome` and
`reason` (`restored`, `blocked`, `not_applicable`, or
`guard_cleared`). Manager reconciliation may return `guard_cleared`
when a durable, server-correlated manager binding safely releases a
stranded global creation guard after a crash. This does **not** certify
that its active turn finished. Manager results also include the registry
snapshot. Only an exact newly observed user
message, proven submission correlation, fresh authoritative history, and an
independent remote terminal-success state can restore `idle`; the durable
journal update must succeed first. The provider must implement the optional
read-only reconciliation contract. Legacy uncorrelated turns cannot be
unlocked by a mere transcript match, even when the apparent text matches.

A `creation.pending` marker is a global fail-closed guard. Creation
reconciliation needs a conversation ID **correlated by the original
submission**, a matching first turn and verified completion. Manager-owned
creation cannot be reconciled through the unowned creation endpoint; its
registry binding is committed before the global creation guard is removed.
Missing IDs, transient history failures, rate limiting, ambiguous histories,
and terminal failures all stay blocked. A confirmed rejected send was never
ambiguous and remains `idle`, requiring a new explicit send to try again.

The 12-minute observation deadline is **not** a remote failure. It now emits
`ObservationExpired`, leaving the conversation `uncertain` until a later
explicit reconciliation establishes actual completion. The legacy `Fail`
terminal also does not prove a remote failure. No event or GET read alone
releases a blocked send.

Before attempting recovery, back up the ignored `stateDirectory` and
preserve any archives. Do not delete `creation.pending`, alter hashed
journal checkpoint files, or replay a request to bypass a safety guard. If
evidence remains inconclusive, preserve the block for investigation.

Keep the authenticated browser profile **outside this repository**. The
example `local-session-profile/` directory is ignored only to prevent
accidental staging during local experiments.

## Explicit PR review gate (foundational stage)

The opt-in manager PR evidence workflow and guarded merge command are documented
in [REVIEW_GATE.md](REVIEW_GATE.md). No merge is permitted with incomplete or
self-certified review evidence; this is not yet autonomous worker orchestration.

## Permanent output archives

New turns are archived in `%LOCALAPPDATA%\Neko\conversation-archives`, with the following outputs:

- `<attempt-id>.txt`: a readable, timestamped history of received visible text including earlier versions overwritten by later replacements.
- `<attempt-id>.jsonl`: an append-only sequence of `append` / `replace` changes suitable for exact replay.
- `conversation-<id>.txt`: completed turns accumulated for convenient reading.

The archive preserves **observed, user-visible output**, including the **full user prompts**, not hidden internal reasoning or raw protocol traffic. If a user types passwords, tokens or other sensitive information into a prompt, those strings will be preserved in plaintext too. Prior overwritten drafts cannot be recreated retroactively. Local files are never automatically deleted or uploaded; apply filesystem access controls and backups as appropriate.

## Development

```powershell
dotnet test tests/Neko.Core.Tests/Neko.Core.Tests.csproj -c Release
dotnet test tests/Neko.Headless.Tests/Neko.Headless.Tests.csproj -c Release
dotnet test tests/Neko.Transport.Tests/Neko.Transport.Tests.csproj -c Release
node scripts/check-public-source.cjs
```

Unit tests verify streaming updates, archive revisions, scope correlation, replay, and headless recovery semantics. Full live testing requires a separately configured, authorized browser session; CI does not make network calls or send messages to external services.

**Known gaps:** remote generation cancellation, multi-client live streaming under load, reconciliation of legacy operations without unique correlation evidence, archive browsing/export API, and non-text response preservation.
