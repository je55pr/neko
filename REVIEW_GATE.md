# Explicit PR review and merge gate (first stage)

This is a **guarded workflow**, not an autonomous review/merge agent. Reviewer job
scheduling, manager inbox notifications, reconciliation of uncertain PR actions,
and automatic re-review loops remain dependencies of the worker-job work (#33).
The gate does not start or merge its *own* implementation PR.

## External policy

Set optional `reviewGate` in the local ignored host configuration. There is
**no default permit policy**; with missing/empty configuration, merge blocks.

```json
{
  "externalPrCliExecutable": "C:\\Program Files\\GitHub CLI\\gh.exe",
  "reviewGate": {
    "requiredChecks": ["build-and-test", "static-analysis"],
    "automatedCheck": "configured-review-check",
    "automatedReviewer": "configured-review-account",
    "automatedCheckProducer": "app:12345",
    "automatedCheckWorkflow": "workflow:400010385",
    "automatedNoFindingsMarker": "No actionable findings",
    "trustedCheckProducers": {
      "build-and-test": "app:23456",
      "static-analysis": "app:34567"
    },
    "trustedCheckWorkflows": {
      "build-and-test": "workflow:400010385"
    },
    "requireNoUnresolvedThreads": true
  }
}
```

These names are examples, not hardcoded rules. The deployment owner must set
the actual required CI names and automated review account/check name for the
repository. All configured CI must have a conclusive successful run at the
exact PR head. The automated reviewer must additionally have a substantive
posted review against that head. An automated review also needs a trusted,
configured no-findings line in the **latest bot review body**. A successful
check run does not mean the bot found no problems. Unknown body formats and
reviews with actionable findings block merge until fixes and a new clear
review have been obtained on the current head. The configured marker is a
literal line (optionally Markdown-bold), not an arbitrary substring; use a
positive phrase emitted by the repository's actual reviewer. No marker is
assumed by default. Skipped, neutral, pending, rejected, cancelled, and
check-only automated responses cannot pass.

## Explicit operations

With a separately configured running host (do not edit an active host while
developing), the authenticated local CLI exposes:

```text
pr-register <owner/repo> <number> <creator-conversation> <developer-conversation> <manager-conversation>
pr-status <owner/repo> <number>
pr-refresh <owner/repo> <number>  # explicit new head/base review cycle
pr-reviewer <owner/repo> <number> <reviewer-account> <review-task>
pr-publish-review <owner/repo> <number>
pr-merge <owner/repo> <number>
```

The matching API is `POST /pr-workflows`, `GET
/pr-workflows/{owner}/{repo}/{number}`, and `POST` to the `/reviewer`,
`/publish-review`, or `/merge` suffixes. The host's existing bearer
authentication protects all routes. The host creates a fresh reviewer worker
for the recorded head after a durable launch reservation; acceptance is **not**
completion. The reviewer must provide a substantive terminal response with
`REVIEW_VERDICT: NO_BLOCKERS` or `REVIEW_VERDICT: BLOCKERS` on its first line.
Only the original creation turn is eligible: the prompt hash, single user
message, durable terminal proof, assistant message ID and dispatch count are
checked. A later conversation follow-up can never change that verdict.
After observing authoritative completion, the host durably reserves a one-use
review post and submits the **actual worker response**. The source review
receipt must match the response's SHA-256 digest, the current head, the
configured publishing account and the subsequently verified source-side review.
The host checks the currently authenticated source account **before** reserving
the post. An ambiguous accepted POST remains blocked until manually reconciled.
An echoed review URL or an uncertain publication cannot pass. This publisher
may use the same repository account as the developer; independence comes from
the distinct provider conversation and its correlated output, not the Git
account signing the host transport.

The external adapter requires an explicitly configured, fully qualified
`gh` executable path, and does not use a shell or search PATH. It reads
authoritative PR state, check runs, posted reviews, and unresolved threads.
Check policies must include trusted producer IDs such as `app:<numeric-id>`
or `user:<numeric-id>`, not only the reported check names; those numeric
identities must be set from verified source metadata. Policy evaluation and
journal records are service-neutral. GitHub Actions checks are additionally
bound to their source workflow IDs from authenticated run metadata and the
newest run from that exact producer/workflow identity; same-app checks from
other workflows cannot satisfy the gate. Repository identities use the
canonical name returned by the source, and the durable PR journal treats
repository casing aliases as the same identifier. Pagination remains deliberately
fail-closed: 100 or more statuses, review comments, or reviews are rejected;
more than 100 check runs or review threads are rejected until exhaustive
pagination is implemented.

For merge, the server re-fetches authoritative state, evaluates all blockers,
durably reserves a one-use attempt, re-fetches state, then submits a merge
with the **expected head SHA as a server-side precondition**. The source
merge endpoint does not atomically compare the base SHA, CI or review status:
repository branch protection/rulesets must independently enforce those
requirements, and the adapter only narrows the remaining time-of-check/use
window. An ambiguous merge response is journalled `uncertain`, never
automatically retried. A provable pre-dispatch rejection is recorded separately
and may be explicitly rearmed using `pr-refresh` after authoritative source
inspection. A storage failure before reservation prevents any merge write.
Failure to persist success after a remote merge leaves the original attempt
reservation on disk, so subsequent requests cannot repeat the merge.

Changing the head or base closes this workflow's review proof. The explicit
`pr-refresh` command rebinds the head/base only after fetching their current
source values, resets reviewer evidence and creation reservation, and retains
the prior revision in the audit. It refuses if a previous review creation or
merge attempt is unresolved. This stage does not offer a hidden override or
a human waiver endpoint.

The durable `stateDirectory/pr-workflows/` journal is local and excluded from
version control. Each entry includes reviewer binding, terminal proof,
one-use review-post and merge-attempt markers, content digests, source review
URL/verdict and audit events. A review-post attempt with an uncertain external
outcome cannot be automatically retried. Live check snapshots are read from
the external source for each inspection, not trusted from manager claims.
The evidence report enumerates blockers. Keep this directory protected and
backed up with the other local state journals.

## Current limitations

This foundation does not yet launch an independent reviewer autonomously when
a developer finishes, track worker jobs with #33, notify an idle manager through
the inbox, triage general discussion comments, or reconcile uncertain external
merge outcomes. A successful policy result is not authorization to merge this
feature PR without an external fresh-conversation review and completed automated
review. Requiring a substantive posted source review (not merely a bot comment)
is intentionally strict; adapters can later add authenticated, head-correlated
review-summary evidence for repositories whose automated reviewers publish only
discussion comments.
