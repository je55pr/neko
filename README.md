# Neko

A headless, provider-neutral conversation host with a localhost JSON API and CLI.
No desktop frontend is required.

## Setup

Requires .NET 10. A provider adapter is built separately and kept out of this
repository. The adapter must implement `IConversationProviderFactory`, returning
an `IScopedConversationProvider`. Neko refuses startup if it cannot guarantee
that new conversations use the configured creation scope.

1. Copy `secrets.example.json` to `secrets.json` (ignored by Git).
2. Set an absolute or relative adapter assembly path and its factory type.
3. Set an ignored adapter settings path and the required creation scope.
4. Set a random local access token (at least 24 characters).
5. Start the server:

```powershell
dotnet run --project src/Neko.Headless -- serve --config secrets.json
```

From another terminal, including via Remote Desktop Commander:

```powershell
dotnet run --project src/Neko.Headless -- health --config secrets.json
dotnet run --project src/Neko.Headless -- create "Hello" --wait --config secrets.json
dotnet run --project src/Neko.Headless -- list --config secrets.json
dotnet run --project src/Neko.Headless -- send <conversation-id> "Continue" --wait --config secrets.json
dotnet run --project src/Neko.Headless -- read <conversation-id> --config secrets.json
dotnet run --project src/Neko.Headless -- status <conversation-id> --config secrets.json
```

The HTTP API binds to `127.0.0.1` only, requires a bearer token for every
request, and supports `/health` and `/conversations` routes.
`GET /conversations/{id}/events` streams server-sent JSON updates.
No provider credentials, session URLs, scope IDs, or provider-specific protocol
code belong in Git.

## Reliability rules

- Accepted means the provider accepted a submission, **not** that the response
  finished. Only a terminal stream event marks completion.
- A send, create or stream with an uncertain outcome is **never** automatically
  retried. Its local checkpoint remains unresolved after restart.
- Conversation snapshots are stored locally in `state/`, outside version control.
  Scope enforcement applies to every new chat; existing IDs are not re-created.
- A private adapter is responsible for authenticated delivery, authoritative
  history, and ensuring the scope is honoured. The public host does not guess.
- The current host is an initial working slice, **not** yet unlimited conversation
  context. Cross-session summarisation and safe recovery require additional work.

## Development

```powershell
dotnet test tests/Neko.Core.Tests/Neko.Core.Tests.csproj
dotnet test tests/Neko.Headless.Tests/Neko.Headless.Tests.csproj
node scripts/check-public-source.cjs
```
