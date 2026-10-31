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
  "reviewGate": {
    "requiredChecks": ["build-and-test", "static-analysis"],
    "automatedCheck": "configured-review-check",
    "automatedReviewer": "configured-review-account",
    "requireNoUnresolvedThreads": true
  }
}
```

These names are examples, not hardcoded rules. The deployment owner must set
the actual required CI names and automated review account/check name for the
repository. All configured CI must have a conclusive successful run at the
exact PR head. The automated reviewer must additionally have a substantive
posted review against that head. Skipped, neutral, pending, rejected, cancelled,
and check-only automated responses cannot pass.

## Explicit operations

With a separately configured running host (do not edit an active host while
developing), the authenticated local CLI exposes:

```text
pr-register <owner/repo> <number> <creator-conversation> <developer-conversation> <manager-conversation>
pr-status <owner/repo> <number>
pr-refresh <owner/repo> <number>  # explicit new head/base review cycle
pr-reviewer <owner/repo> <number> <reviewer-account> <review-task>
pr-review-proof <owner/repo> <number> <posted-review-url> approved|commented
pr-merge <owner/repo> <number>
```

The matching API is `POST /pr-workflows`, `GET
/pr-workflows/{owner}/{repo}/{number}`, and `POST` to the `/reviewer`,
`/review-proof`, or `/merge` suffixes. The host's existing bearer
authentication protects all routes. The review worker is created by the host
specifically for the recorded head after a durable launch reservation. The
accepted creation is **not** completion. The review-proof operation verifies a
locally completed, readable assistant turn and an actual source-side posted
review with matching account, URL, verdict, and head.

The external adapter uses an authenticated local `gh` executable for
authoritative PR state, check runs, posted reviews, and unresolved threads.
Only a narrowly scoped infrastructure adapter contains repository-service
calls; policy evaluation and journal records are service-neutral. The adapter
fails closed on incomplete paginated results; currently it deliberately
rejects >100 checks, reviews, comments, or review threads until exhaustive
pagination is implemented.

For merge, the server re-fetches authoritative state, evaluates all blockers,
durably reserves a one-use attempt, re-fetches state, then submits a merge
with the **expected head SHA as a server-side precondition**. An ambiguous
merge response is journalled `uncertain`, never automatically retried. A
storage failure before reservation prevents any merge write. Failure to
persist success after a remote merge leaves the original attempt reservation
on disk, so subsequent requests cannot repeat the merge.

Changing the head or base closes this workflow's review proof. The explicit
`pr-refresh` command rebinds the head/base only after fetching their current
source values, resets reviewer evidence and creation reservation, and retains
the prior revision in the audit. It refuses if a previous review creation or
merge attempt is unresolved. This stage does not offer a hidden override or
a human waiver endpoint.

The durable `stateDirectory/pr-workflows/` journal is local and excluded from
version control. Each entry includes reviewer binding, terminal proof, source
review URL/verdict, attempted merge and audit events. Live check snapshots are
read from the external source for each inspection, not trusted from manager
claims. The evidence report enumerates blockers. Keep this directory protected
and backed up with the other local state journals.

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
