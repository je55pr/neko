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

| `/conversations/{id}` | GET | 200: transcript (refreshed from provider, or last saved checkpoint); 202: known ID but no history yet; 404: unknown ID |
| `/conversations/{id}/status` | GET | Current local operation status and public-event progress metadata |

## Architecture and reliability

The single WebView2 page supplies the signed-in execution environment. The adapter invokes the site's existing submission function with explicit conversation, parent and scope values. It does not navigate to individual conversations or interact with composer buttons. Read-only network observation captures live response updates. Server history verifies completion.

The transport is deliberately isolated in `src/Neko.Transport`. Its configured origin, module export name, route patterns, headers, scope format and other service identifiers are read from `secrets.json`. The public host consumes only the neutral conversation contract in `src/Neko.Core`.

- An `Accepted` submission is **not** a completed response. The conversation remains active until its terminal event or authoritative server history confirms completion.
- Ambiguous sends and creations are **never** automatically retried. A crash can leave a pending checkpoint that requires reconciliation before another request.
- A temporary server read failure must not be mistaken for successful completion.
- Creation verifies remote membership of the required scope; an unverified new conversation is not reported as accepted.
- The local journal under `state/` stores the latest conversation snapshots and operation states. Session files, local checkpoints and secrets are ignored by Git.

### Operator recovery after uncertain delivery

If a submission is marked `uncertain`, **do not resend the same request** just because the local API did not receive confirmation. First stop Neko, back up the configured `stateDirectory`, and compare the recorded conversation ID, latest visible prompt and provider-side history with the intended project. A `creation.pending` file is a fail-closed guard for a possibly delivered creation; its presence alone does not indicate rejection. Keep that marker and the archive until the remote outcome has been independently established.

There is currently no automated reconciliation or safe unlock endpoint. If the remote outcome is positively established by an operator, record the evidence in the local backup and deliberately reconcile the matching local journal/checkpoint while the service is stopped. If the outcome cannot be established, preserve the uncertain state and avoid another send or creation. Never blindly delete the guard or replay an ambiguous prompt.

Keep the authenticated browser profile **outside this repository**. The example `local-session-profile/` directory is ignored only to prevent accidental staging during local experiments.

## Reconciliation of uncertain operations

An `Uncertain` receipt means delivery cannot safely be repeated. After
stopping the service, inspect `state/creation.pending` for a blocked creation
or `state/<conversation-id>.json` for a blocked follow-up. Check the remote
conversation history and project membership **before** taking any action.
If a matching request exists, record its server conversation ID and reconcile
the local state with that authoritative record. If the result remains unknown,
leave the checkpoint intact and do not replay the message. Archive the original
checkpoint and your evidence before any manual resolution. Never simply remove
the checkpoint to make a retry possible.

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

**Known gaps:** remote generation cancellation, multi-client live streaming under load, automatic reconciliation of uncertain requests, archive browsing/export API, and non-text response preservation.
