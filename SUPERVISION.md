# Opt-in user-session host supervision (Windows)

The headless API is normally started manually. On Windows with a signed-in
interactive desktop session, Neko can instead register an **opt-in per-user**
logon launcher. It is not a Windows service, does not run before login, does
not require administrator privileges, and cannot recover a computer that is
powered off or a user session that has not started.

## Prepare and install

In the checkout that will remain installed, with a private `secrets.json`
(or an explicit `--config` path):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-headless.ps1
dotnet run --no-build -c Release --project src/Neko.Headless -- supervisor-install --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- supervisor-status --config secrets.json
```

The prepare script builds the host and transport and stages the native runtime
dependency **without launching a host**. The installer registers a single entry
for this state directory under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` containing only
the tracked PowerShell launcher path and a short registration key. An ignored
`scripts/supervisor-<key>.local.json` manifest retains the absolute executable,
assembly and private config paths plus the pinned instance identity. The manifest
contains no access token. Windows limits Run-key commands to 260 characters;
installation fails safely if the checkout path makes even this short entry too
long. Installation does **not** start, stop, restart or inspect a live host.
The launcher runs at the next supported interactive user logon.

To run supervision interactively, instead of installing automatic startup:

```powershell
dotnet run --no-build -c Release --project src/Neko.Headless -- supervise --config secrets.json
```

This remains in the terminal until stopped. Use Ctrl+C to stop supervision;
it does **not** deliberately terminate or interrupt a running host. If a
manually started host already owns the configured state directory, the
supervisor observes it and does not start a duplicate. After that host exits,
the supervisor may start a replacement.

## Status, logs and uninstall

```powershell
dotnet run --no-build -c Release --project src/Neko.Headless -- supervisor-status --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- supervisor-logs --config secrets.json
dotnet run --no-build -c Release --project src/Neko.Headless -- supervisor-remove --config secrets.json
```

`supervisor-status` reports whether the exclusive supervisor lock is actively
held, whether a Run entry exists, the last recorded status, and the result of
an **authenticated, read-only** localhost health probe. The last status is
historical if the supervisor lock is not held. `supervisor-logs` prints the
last 60 supervisor events; it never reads transcripts or talks to a provider.
Local operator-only files live under `stateDirectory/supervision/status.json`
and `stateDirectory/supervision/supervisor.log`. Existing conversation,
manager, inbox, review and creation checkpoints remain where they already
were. Logs and status may contain local lifecycle metadata, not bearer tokens
or prompt text. Protect the state directory as private data.

Removal deletes the matching per-user Run entry and its ignored local launch
manifest only. It does not remove host state, pending markers, credentials,
private browser profiles, an active supervisor or its child. An installation from another checkout is
not silently overwritten or removed; use that original checkout to remove
it first. Keep the checkout and compiled host executable in place while the
Run entry is installed.

## Recovery rules

The supervisor opens `stateDirectory/supervisor.lock` with exclusive
sharing. The host continues to own its existing exclusive `host.lock`;
the host now acquires that lease **before** initializing the provider/browser.
Two supervisor processes, two hosts, or a manual host plus a supervised host
therefore cannot simultaneously own one state directory. If the host lease is
busy, supervision waits for the existing owner rather than killing it. If the
localhost port is occupied without the state lease, or a health check returns
a different authenticated instance identity, supervision blocks rather than
attaching to an unknown host.

When a **child process actually exits**, supervision relaunches the normal
`serve` command with the same config. It backs off 1, 2, 4, 8 and 16
seconds for up to five restarts in a rolling 15-minute window, then stops
with `restart_budget_exhausted`. If a process remains alive but is not
ready after 30 seconds, it is reported as `host_alive_but_not_ready`, but
it is **not** killed or blindly restarted. Startup, crash, port contention,
health failure and mismatched authentication have distinct status reasons.

The installed launch manifest pins a digest of the original state directory,
provider configuration, scope, port and access token. If its configuration
file is later changed, startup refuses to access the newly configured state.
Remove the old registration and deliberately reinstall after validating
the new state and copying/backing up evidence. A running supervisor also
refuses changed configuration on the next launch. Different Windows path
capitalization does not change identity.

Authenticated `GET /health` returns `status: "ready"`, `instanceId`,
`uncertainConversations`, `pendingCreation`, and
`blockedManagerInboxEntries`. **Ready only means the API is listening.**
It does not mean every conversation is complete or every queued message has
been submitted. The counts expose locally unresolved conditions without
releasing guards or reading provider history. The separate conversation
and manager endpoints remain authoritative for individual operation state.

**Never delete `creation.pending`, clear pending evidence, reset a journal
or repeat an ambiguous remote operation to make the health check green.**
On restart the existing runtime marks interrupted sends and generations
uncertain. Only never-dispatched manager inbox messages remain eligible for
the existing guarded queue worker. The supervisor itself sends no messages,
creates no conversations, performs no reconciliation, and makes no PR writes.
Inconclusive operations remain blocked for explicit, evidence-based
reconciliation. A fatal startup/configuration mismatch requires operator
attention, not a state reset.

## Verification and limitations

The automated tests exercise exclusive ownership, bounded restarts,
authentication and identity mismatch, simulated process exits, and preservation
of queued/accepted/uncertain checkpoints. CI never uses a real browser session
or sends a remote operation. Actual login autostart and WebView2 readiness
must be smoke-tested with a **separate, explicitly authorized** user profile
and state directory, not a production live instance. If a background host
exits while no user session exists, HKCU Run cannot start supervision until
the next supported login. Sudden termination of the supervisor can leave
its child serving; the next supervisor observes the existing host lease and
does not start a duplicate.
