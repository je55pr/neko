using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Neko;

namespace Neko.Transport;

public sealed class Factory : IConversationProviderFactory
{
    public IConversationProvider Create(string settingsPath)
    {
        var settings=TransportSettings.Read(settingsPath);
        return new ProjectProvider(settings);
    }
}

internal sealed class ProjectProvider : IScopedConversationProvider, IProviderRateLimitStatus,
    IAuthoritativeReconciliationProvider, IDisposable
{
    // Project-scoped creation was verified by an independent saved-history
    // read on 2026-10-30, without any per-chat WebView2 navigation.
    private static readonly bool EnableNativeCreation=true;
    private sealed record RemoteSnapshot(
        VisibleConversation Conversation, string LastAssistantId, string LastAssistantStatus,
        string LastAssistantText, string CurrentNode);

    private readonly BrowserSession _browser;
    private readonly TransportSettings _settings;
    private readonly NativeDispatchSender _sender;
    private readonly NativeLiveStreamHub _live;
    private readonly SemaphoreSlim _sendGate = new(1,1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string,Channel<PublicConversationUpdate>> _streams = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset CheckedAt, RemoteSnapshot Value)> _snapshots = new();
    private readonly object _cooldownGate = new();
    private DateTimeOffset _readCooldownUntil = DateTimeOffset.MinValue;

    private bool IsRateLimited()
    {
        lock (_cooldownGate) return DateTimeOffset.UtcNow < _readCooldownUntil;
    }

    internal static int? RemainingCooldownSeconds(DateTimeOffset until,DateTimeOffset now)
    {
        var seconds=(until-now).TotalSeconds;
        return seconds>0?(int)Math.Min(600,Math.Ceiling(seconds)):null;
    }

    public int? RateLimitRetryAfterSeconds
    {
        get
        {
            lock(_cooldownGate)
                return RemainingCooldownSeconds(_readCooldownUntil,DateTimeOffset.UtcNow);
        }
    }

    private SendReceipt RateLimitedReceipt() =>
        new(SendDisposition.Rejected,"provider_rate_limited")
        { Retryable=true, RetryAfterSeconds=RateLimitRetryAfterSeconds };

    // Only a correlated, definitively rejected HTTP response is safe to retry.
    // A timeout or lost native response still has ambiguous delivery.
    internal static SendReceipt? ConfirmedNative429Receipt(
        NativeDispatchSender.Result result,int? retryAfterSeconds) =>
        result.Dispatched && !result.SseAccepted && result.HttpStatus==429
            ? new SendReceipt(SendDisposition.Rejected,result.State)
              { Retryable=true, RetryAfterSeconds=retryAfterSeconds }
            : null;

    // A correlated 429 is known rejected even when the native POST ran.
    // An attempted send without that confirmation may have been delivered.
    internal static string SendArchiveOutcome(bool attempted,bool confirmedRejected) =>
        attempted && !confirmedRejected?"submission_uncertain":"rejected";

    private void ObserveHttpRateLimit(int? status)
    {
        if(status!=429)return;
        lock(_cooldownGate)
        {
            var until=DateTimeOffset.UtcNow.AddSeconds(60);
            if(until>_readCooldownUntil)_readCooldownUntil=until;
        }
    }

    internal static TimeSpan? RetryAfter(JsonElement root)
    {
        if(!root.TryGetProperty("status",out var code) ||
           code.ValueKind!=JsonValueKind.Number ||
           !code.TryGetInt32(out var status) || status!=429)return null;
        var seconds=root.TryGetProperty("retryAfterSeconds",out var retry) &&
            retry.ValueKind==JsonValueKind.Number &&
            retry.TryGetInt32(out var value)?value:0;
        return TimeSpan.FromSeconds(Math.Clamp(seconds,60,600));
    }

    internal static DateTimeOffset ExtendedCooldown(DateTimeOffset previous,
        JsonElement response,DateTimeOffset now)
    {
        var delay=RetryAfter(response);
        if(delay is null)return previous;
        var until=now+delay.Value;
        return until>previous?until:previous;
    }

    private void ObserveReadThrottle(JsonElement root)
    {
        lock(_cooldownGate)
            _readCooldownUntil=ExtendedCooldown(
                _readCooldownUntil,root,DateTimeOffset.UtcNow);
    }

    public ProjectProvider(TransportSettings settings)
    {
        _settings=settings;
        _browser = new BrowserSession(settings.ProfileDirectory,settings);
        _sender = new NativeDispatchSender(_browser);
        _live = new NativeLiveStreamHub(_browser);
    }

    private bool ValidScope(string id)
    {
        try
        {
            return Regex.IsMatch(id,_settings.ScopePattern,
                RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }
        catch(RegexMatchTimeoutException) { return false; }
    }

    private async Task<HashSet<string>?> ProjectIdsAsync(string scope, CancellationToken cancellation)
    {
        if (!ValidScope(scope) || IsRateLimited()) return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string cursor = "0";
        for (var page = 0; page < 12; page++)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = _settings.Format(_settings.ProjectListingPath,"scope",scope)
                .Replace("{cursor}",Uri.EscapeDataString(cursor),
                    StringComparison.Ordinal);
            using var result=await _browser.ReadAsync(path,"project");
            var root=result.RootElement;
            if(!root.TryGetProperty("ok",out var ok) ||
               ok.ValueKind!=JsonValueKind.True)
            {
                ObserveReadThrottle(root);
                return null;
            }
            if(!root.TryGetProperty("valid",out var valid) ||
               valid.ValueKind!=JsonValueKind.True ||
               !root.TryGetProperty("truncated",out var clipped) ||
               clipped.ValueKind!=JsonValueKind.False ||
               !root.TryGetProperty("items",out var items) ||
               items.ValueKind!=JsonValueKind.Array)return null;
            foreach(var entry in items.EnumerateArray())
            {
                if(!entry.TryGetProperty("id",out var value) ||
                   value.ValueKind!=JsonValueKind.String ||
                   !Guid.TryParse(value.GetString(),out _))return null;
                seen.Add(value.GetString()!);
            }
            if(!root.TryGetProperty("cursor",out var next) ||
               next.ValueKind!=JsonValueKind.String ||
               string.IsNullOrWhiteSpace(next.GetString()))
                return seen;
            cursor = next.GetString()!;
            if (!cursors.Add(cursor)) return null;
        }
        // Never accept incomplete project membership enumeration.
        return null;
    }

    private async Task<RemoteSnapshot?> SnapshotAsync(string id, bool allowRecent = false)
    {
        if (!Guid.TryParse(id, out _)) return null;
        if (allowRecent && _snapshots.TryGetValue(id, out var cached) &&
            DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(8))
            return cached.Value;
        if (IsRateLimited()) return null;
        using var result = await _browser.ReadAsync(
            _settings.Format(_settings.ConversationHistoryPath,"id",id), "conversation");
        var root = result.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var status = root.TryGetProperty("status", out var code) &&
                code.ValueKind==JsonValueKind.Number ? code.GetInt32() : 0;
            ObserveReadThrottle(root);
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "neko-private-adapter.log"),
                DateTimeOffset.UtcNow.ToString("O") + " snapshot_http " + status +
                Environment.NewLine); }
            catch (Exception)
            {
                // Logging is best effort. Never mask the original read result.
                System.Diagnostics.Trace.TraceWarning(
                    "Neko transport snapshot diagnostic unavailable.");
            }
            return null;
        }

        var messages = new List<VisibleConversationMessage>();
        string latestAssistantId = "", latestAssistantStatus = "", latestAssistantText = "";
        foreach (var item in root.GetProperty("messages").EnumerateArray())
        {
            var role = item.GetProperty("role").GetString();
            var idValue = item.GetProperty("id").GetString() ?? "";
            var text = item.GetProperty("text").GetString() ?? "";
            if (role == "user")
                messages.Add(new VisibleConversationMessage(idValue, VisibleMessageRole.User, text));
            else if (role == "assistant")
            {
                messages.Add(new VisibleConversationMessage(idValue, VisibleMessageRole.Assistant, text));
                latestAssistantId = idValue;
                latestAssistantText = text;
                latestAssistantStatus = item.GetProperty("status").GetString() ?? "";
            }
        }
        var title = root.GetProperty("title").GetString() ?? "Conversation";
        var currentNode=root.TryGetProperty("currentNode",out var node)
            && node.ValueKind==JsonValueKind.String ? node.GetString()??"" : "";
        var snapshot = new RemoteSnapshot(new VisibleConversation(id, title, messages),
            latestAssistantId, latestAssistantStatus, latestAssistantText,currentNode);
        _snapshots[id] = (DateTimeOffset.UtcNow, snapshot);
        return snapshot;
    }

    public async Task<bool> BelongsToScopeAsync(
        string scope, string conversationId, CancellationToken cancellation)
    {
        try
        {
            var ids = await ProjectIdsAsync(scope, cancellation);
            return ids is not null && ids.Contains(conversationId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    internal static bool MatchesCreationReceipt(bool accepted,string? correlatedId,
        IEnumerable<string> newlyListedIds) =>
        accepted && Guid.TryParse(correlatedId,out _) &&
        newlyListedIds.Contains(correlatedId,StringComparer.OrdinalIgnoreCase);

    // The signed-in application's own submission function performs request
    // preparation and verification. We never interact with a composer.
    public async Task<SendReceipt> CreateInScopeAsync(
        string scope,PreparedOutboundMessage submission,CancellationToken cancellation)
    {
        if(!ValidScope(scope) || !OutboundMessagePreparation.IsValid(submission) ||
           !submission.CreatesConversation)
            return new SendReceipt(SendDisposition.Rejected,"invalid_submission");
        if(!EnableNativeCreation)
            return new SendReceipt(SendDisposition.Rejected,
                "native_creation_not_verified");
        if(IsRateLimited())return RateLimitedReceipt();

        await _sendGate.WaitAsync(cancellation);
        var dispatched=false;
        var confirmedRejected=false;
        NativeLiveStreamHub.Attempt? capture=null;
        var stage="scope_preflight";
        try
        {
            // Another concurrent read may have received 429 while waiting.
            if(IsRateLimited())return RateLimitedReceipt();
            // BrowserSession loads one authenticated page at startup.
            // Do not navigate to a project to create its conversation.
            // Pass the requested scope explicitly to the native operation.
            var existing=await ProjectIdsAsync(scope,cancellation);
            if(existing is null)
                return IsRateLimited()?RateLimitedReceipt():
                    new SendReceipt(SendDisposition.Rejected,"project_unavailable")
                    { Retryable=true };

            if(IsRateLimited())return RateLimitedReceipt();
            stage="native_dispatch";
            var parentId=Guid.NewGuid().ToString("D");
            capture=await _live.BeginAsync(parentId,null,scope,submission.Text);
            var result=await _sender.SubmitOnceAsync(null,
                parentId,submission.Text,scope,cancellation,
                externalStreamObserver:true);
            dispatched=result.Dispatched;
            ObserveHttpRateLimit(result.HttpStatus);
            if(!dispatched)
            {
                return new SendReceipt(SendDisposition.Rejected,result.State);
            }
            var throttled=ConfirmedNative429Receipt(result,RateLimitRetryAfterSeconds);
            if(throttled is not null)
            {
                confirmedRejected=true;
                return throttled;
            }

            // A page-side submission can succeed even if WebView2 loses
            // the script's response during route changes. Reconcile the
            // project and exact user message without ever sending again.
            stage="scope_reconciliation";
            var resolvedId=Guid.TryParse(result.ServerConversationId,out _)?
                result.ServerConversationId:null;
            if(!result.SseAccepted || resolvedId is null)
                return new SendReceipt(SendDisposition.Uncertain,
                    result.State,ConversationId:resolvedId);

            for(var attempt=0;attempt<5;attempt++)
            {
                if(attempt>0)
                    await Task.Delay(TimeSpan.FromSeconds(3),cancellation);
                if(IsRateLimited())break;
                var after=await ProjectIdsAsync(scope,cancellation);
                if(after is null)break;
                var additions=after.Except(existing).Where(id=>
                    Guid.TryParse(id,out _)).Take(12).ToArray();
                // A same-text message is not a unique operation identity.
                if(MatchesCreationReceipt(result.SseAccepted,resolvedId,additions))
                {
                    StartPolling(resolvedId!,"",capture);
                    capture=null; // Polling now owns the live observer.
                    return new SendReceipt(SendDisposition.Accepted,
                        ConversationId:resolvedId);
                }
                // Never choose a conversation based only on prompt text.
                // Two distinct creations may have identical first messages.
                if(IsRateLimited())break;
            }
            return new SendReceipt(SendDisposition.Uncertain,
                result.HttpStatus is >=400?"submission_rejected_but_scope_unverified":
                "project_creation_pending_verification",
                ConversationId:resolvedId);
        }
        catch(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(),
                    "neko-private-adapter.log"),
                    DateTimeOffset.UtcNow.ToString("O")+" native_create_"+
                    stage+" "+ex.GetType().Name+Environment.NewLine);
            }
            catch(Exception)
            {
                // Diagnostics are best effort. The original failure remains
                // represented by the sanitized receipt below.
                System.Diagnostics.Trace.TraceWarning(
                    "Neko transport creation diagnostic unavailable.");
            }
            return new SendReceipt(dispatched?SendDisposition.Uncertain:
                SendDisposition.Rejected,"adapter_"+stage+"_failed");
        }
        finally
        {
            if(capture is not null)
                await _live.FinishAsync(capture,
                    dispatched && !confirmedRejected?"submission_uncertain":"rejected",null);
            _sendGate.Release();
        }
    }

    public Task<SendReceipt> CreateAsync(string text, CancellationToken cancellation) =>
        Task.FromResult(new SendReceipt(SendDisposition.Rejected, "scope_required"));

    public async Task<SendReceipt> SendAsync(
        string id, string text, CancellationToken cancellation)
    {
        if(!Guid.TryParse(id,out _) || string.IsNullOrWhiteSpace(text) ||
           text.Length>24000)
            return new SendReceipt(SendDisposition.Rejected,"invalid_submission");
        await _sendGate.WaitAsync(cancellation);
        var stage="preflight";
        var attempted=false;
        var confirmedRejected=false;
        NativeLiveStreamHub.Attempt? capture=null;
        try
        {
            // Cooldown is provider-wide; do not poll again on an active 429.
            if(IsRateLimited())return RateLimitedReceipt();
            // The local journal is never an authoritative parent.
            var before=await SnapshotAsync(id,allowRecent:true);
            if(before is null)
                return IsRateLimited()?RateLimitedReceipt():
                    new SendReceipt(SendDisposition.Rejected,"history_unavailable")
                    { Retryable=true };
            if(!Guid.TryParse(before.CurrentNode,out _) ||
               before.CurrentNode!=before.LastAssistantId ||
               before.LastAssistantStatus!="finished_successfully")
                return new SendReceipt(SendDisposition.Rejected,"parent_not_complete");
            if(IsRateLimited())return RateLimitedReceipt();
            using(var status=await _browser.ReadAsync(
                _settings.Format(_settings.StreamStatusPath,"id",id),"status"))
            {
                var root=status.RootElement;
                ObserveReadThrottle(root);
                if(IsRateLimited())return RateLimitedReceipt();
                if(!root.TryGetProperty("ok",out var ok)||!ok.GetBoolean()||
                   !root.TryGetProperty("streamState",out var state)||
                   state.GetString()!="COMPLETE")
                    return new SendReceipt(SendDisposition.Rejected,
                        "stream_not_verified_complete");
            }
            // Use the already-loaded authenticated runtime. The existing
            // conversation and authoritative parent are explicit arguments.
            if(IsRateLimited())return RateLimitedReceipt();
            stage="native_dispatch";
            // The native sender makes exactly one invocation. HTTP 200 SSE
            // is required before reporting acceptance to the headless host.
            capture=await _live.BeginAsync(before.CurrentNode,id,null,text);
            var result=await _sender.SubmitOnceAsync(
                id,before.CurrentNode,text,null,cancellation,
                externalStreamObserver:true);
            attempted=result.Dispatched;
            ObserveHttpRateLimit(result.HttpStatus);
            if(!result.Dispatched)
            {
                return new SendReceipt(SendDisposition.Rejected,result.State);
            }
            if(!result.SseAccepted)
            {
                var throttled=ConfirmedNative429Receipt(result,RateLimitRetryAfterSeconds);
                if(throttled is not null)
                {
                    confirmedRejected=true;
                    return throttled;
                }
                return new SendReceipt(
                    result.HttpStatus is >=400 ? SendDisposition.Rejected:
                    SendDisposition.Uncertain,result.State);
            }
            StartPolling(id,before.LastAssistantId,capture);
            capture=null; // Polling now owns the live observer.
            return new SendReceipt(SendDisposition.Accepted);
        }
        catch(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(),
                    "neko-private-adapter.log"),
                    DateTimeOffset.UtcNow.ToString("O")+" native_send_"+
                    stage+" "+ex.GetType().Name+Environment.NewLine);
            }
            catch(Exception)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Neko transport send diagnostic unavailable.");
            }
            return new SendReceipt(attempted?SendDisposition.Uncertain:
                SendDisposition.Rejected,"adapter_"+stage+"_failed");
        }
        finally
        {
            if(capture is not null)
                await _live.FinishAsync(capture,
                    SendArchiveOutcome(attempted,confirmedRejected),id);
            _sendGate.Release();
        }
    }

    internal static PublicConversationUpdate? PollUpdate(
        string messageId,string newText,string published)
    {
        if(newText==published)return null;
        var append=newText.StartsWith(published,StringComparison.Ordinal);
        return new PublicConversationUpdate(messageId,PublicUpdateKind.Final,
            append && published.Length>0?PublicUpdateOperation.Append:
                PublicUpdateOperation.Replace,
            append && published.Length>0?newText[published.Length..]:newText);
    }

    // A finished assistant message can be an intermediate tool/activity step.
    // Only the completed remote stream and its current, visible assistant
    // message together establish that a turn has finished successfully.
    internal static bool IsVerifiedVisibleCompletion(string assistantId,
        string assistantStatus,string assistantText,string currentNode,
        string streamState) =>
        !string.IsNullOrWhiteSpace(assistantId) &&
        string.Equals(assistantId,currentNode,StringComparison.OrdinalIgnoreCase) &&
        assistantStatus=="finished_successfully" &&
        !string.IsNullOrWhiteSpace(assistantText) &&
        streamState=="COMPLETE";

    private void StartPolling(string id,string previousAssistant,
        NativeLiveStreamHub.Attempt? capture=null)
    {
        var channel=capture?.Updates??Channel.CreateUnbounded<PublicConversationUpdate>(
            new UnboundedChannelOptions{SingleReader=true,SingleWriter=true});
        _streams[id]=channel;
        _=Task.Run(async()=>{
            var updateId=Guid.NewGuid().ToString("D");
            var last="";
            var pollingPublished="";
            string? finalText=null;
            var completedMessageId="";
            var complete=false;
            var observerExpired=false;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(12));
            try
            {
                while(!deadline.IsCancellationRequested)
                {
                    await Task.Delay(3500,deadline.Token);
                    RemoteSnapshot? snapshot;
                    try { snapshot=await SnapshotAsync(id); }
                    catch(OperationCanceledException) when(deadline.IsCancellationRequested)
                    { break; }
                    catch(Exception) { continue; } // A transient read is not terminal.
                    if(snapshot is null ||
                       snapshot.LastAssistantId==previousAssistant ||
                       string.IsNullOrWhiteSpace(snapshot.LastAssistantId))
                        continue;
                    var finished=false;
                    if(snapshot.LastAssistantStatus=="finished_successfully" &&
                       !string.IsNullOrWhiteSpace(snapshot.LastAssistantText))
                    {
                        // The history endpoint can contain already-finished
                        // intermediate assistant messages while the response
                        // is still running. Check the independent stream state
                        // before publishing a terminal receipt.
                        try
                        {
                            if(IsRateLimited())continue;
                            using var state=await _browser.ReadAsync(
                                _settings.Format(_settings.StreamStatusPath,"id",id),
                                "status");
                            var root=state.RootElement;
                            ObserveReadThrottle(root);
                            var streamState=root.TryGetProperty("ok",out var ok) &&
                                ok.ValueKind==JsonValueKind.True &&
                                root.TryGetProperty("streamState",out var value) &&
                                value.ValueKind==JsonValueKind.String
                                    ?value.GetString()??"":"";
                            finished=IsVerifiedVisibleCompletion(
                                snapshot.LastAssistantId,snapshot.LastAssistantStatus,
                                snapshot.LastAssistantText,snapshot.CurrentNode,streamState);
                        }
                        catch(OperationCanceledException) when(deadline.IsCancellationRequested)
                        { break; }
                        catch(Exception) { /* Status unavailable; keep observing. */ }
                    }
                    // Reconcile against the actual last published text while
                    // holding the native publication gate. At completion it
                    // excludes delayed CDP callbacks before the final update.
                    if(snapshot.LastAssistantText!=last || finished)
                    {
                        last=snapshot.LastAssistantText;
                        if(!string.IsNullOrWhiteSpace(last))
                        {
                            if(capture is not null)
                                capture.PublishPolling(snapshot.LastAssistantId,
                                    last,finished);
                            else
                            {
                                var update=PollUpdate(snapshot.LastAssistantId,
                                    last,pollingPublished);
                                if(update is not null)
                                {
                                    channel.Writer.TryWrite(update);
                                    pollingPublished=last;
                                }
                            }
                        }
                    }
                    if(!finished)continue;
                    complete=true;
                    finalText=last;
                    completedMessageId=snapshot.LastAssistantId;
                    break;
                }
                observerExpired=deadline.IsCancellationRequested &&
                    !_lifetime.IsCancellationRequested && !complete;
            }
            catch(OperationCanceledException) when(
                deadline.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { observerExpired=true; }
            catch(Exception){}
            finally
            {
                if(capture is not null)
                    await _live.FinishAsync(capture,
                        complete?"server_verified_complete":"stream_uncertain",
                        id,finalText);
                channel.Writer.TryWrite(new PublicConversationUpdate(
                    complete?completedMessageId:updateId,
                    PublicUpdateKind.Final,
                    ObserverTerminalOperation(complete,observerExpired)));
                channel.Writer.TryComplete();
            }
        });
    }

    internal static PublicUpdateOperation ObserverTerminalOperation(
        bool verifiedComplete, bool deadlineExpired) =>
        verifiedComplete ? PublicUpdateOperation.Complete :
            deadlineExpired ? PublicUpdateOperation.ObservationExpired :
                PublicUpdateOperation.Fail;

    public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        string id, [EnumeratorCancellation] CancellationToken cancellation)
    {
        if(!_streams.TryGetValue(id,out var channel))yield break;
        await foreach(var update in channel.Reader.ReadAllAsync(cancellation))
        {
            yield return update;
            if(update.Operation is PublicUpdateOperation.Complete or
               PublicUpdateOperation.Fail or
               PublicUpdateOperation.ObservationExpired)yield break;
        }
    }
    internal static ReconciliationInspection VerifyCompletedTurn(
        ReconciliationProbe probe, VisibleConversation conversation,
        string assistantId, string assistantStatus, string assistantText,
        string currentNode, string streamState)
    {
        if (conversation.Id != probe.ConversationId ||
            (!probe.AcceptedSubmission && !probe.CorrelatedCreation &&
             string.IsNullOrWhiteSpace(probe.CorrelatedUserMessageId)))
            return new(false, "missing_dispatch_correlation");
        var messages=conversation.Messages;
        var baseline=probe.BaselineMessageIds;
        if (baseline is null || messages.Count < baseline.Count + 2 ||
            baseline.Count > 100000)
            return new(false, "history_not_correlated");
        for(var i=0;i<baseline.Count;i++)
            if(string.IsNullOrWhiteSpace(baseline[i]) ||
               messages[i].Id!=baseline[i])
                return new(false, "history_not_correlated");
        var suffix=messages.Skip(baseline.Count).ToArray();
        if(suffix[0].Role!=VisibleMessageRole.User ||
           suffix.Count(m=>m.Role==VisibleMessageRole.User)!=1 ||
           suffix.Skip(1).Any(m=>m.Role!=VisibleMessageRole.Assistant))
            return new(false, "ambiguous_remote_turn");
        var user=suffix[0];
        var hash=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(user.Text)));
        if(!string.Equals(hash,probe.PromptSha256,StringComparison.Ordinal) ||
           string.IsNullOrWhiteSpace(user.Id) ||
           (probe.CorrelatedUserMessageId is not null &&
            user.Id!=probe.CorrelatedUserMessageId))
            return new(false, "turn_mismatch");
        if(streamState is "FAILED" or "ERROR" or "CANCELLED")
            return new(false, "remote_terminal_failure");
        if(!IsVerifiedVisibleCompletion(assistantId,assistantStatus,
                assistantText,currentNode,streamState) ||
           suffix[^1].Id!=assistantId ||
           messages.Count(m=>m.Id==user.Id)!=1 ||
           messages.Count(m=>m.Id==assistantId)!=1)
            return new(false, "remote_not_verified_complete");
        return new(true,"authoritative_turn_complete",conversation,user.Id,assistantId);
    }

    public async Task<ReconciliationInspection> InspectAsync(
        ReconciliationProbe probe, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!Guid.TryParse(probe.ConversationId,out _))
            return new(false,"invalid_conversation_id");
        if(IsRateLimited())return new(false,"provider_rate_limited");
        try
        {
            // Force fresh independent reads: a locally cached transcript
            // cannot release a durable delivery guard.
            var snapshot=await SnapshotAsync(probe.ConversationId);
            if(snapshot is null)
                return new(false,IsRateLimited()?"provider_rate_limited":
                    "history_unavailable");
            if(IsRateLimited())return new(false,"provider_rate_limited");
            using var response=await _browser.ReadAsync(
                _settings.Format(_settings.StreamStatusPath,"id",probe.ConversationId),
                "status");
            var root=response.RootElement;
            ObserveReadThrottle(root);
            if(IsRateLimited())return new(false,"provider_rate_limited");
            var state=root.TryGetProperty("ok",out var ok) &&
                ok.ValueKind==JsonValueKind.True &&
                root.TryGetProperty("streamState",out var value) &&
                value.ValueKind==JsonValueKind.String
                    ?value.GetString()??"":"";
            if(state.Length==0)return new(false,"status_unavailable");
            return VerifyCompletedTurn(probe,snapshot.Conversation,
                snapshot.LastAssistantId,snapshot.LastAssistantStatus,
                snapshot.LastAssistantText,snapshot.CurrentNode,state);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { throw; }
        catch(Exception) { return new(false,"verification_unavailable"); }
    }

    public async Task<VisibleConversation?> ReadAsync(string id, CancellationToken cancellation) =>
        (await SnapshotAsync(id, allowRecent: true))?.Conversation;

    // Listing the entire remote account is not supported by this adapter.
    // Returning an empty list would falsely imply that no conversations exist.
    public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(CancellationToken cancellation) =>
        Task.FromException<IReadOnlyList<ConversationSummary>>(
            new NotSupportedException("Remote conversation browsing is not implemented."));

    public bool SupportsStop => false;

    public Task<StopReceipt> StopAsync(string id, CancellationToken cancellation) =>
        Task.FromResult(new StopReceipt(StopDisposition.Unsupported, "stop_not_supported"));

    public void Dispose()
    {
        _lifetime.Cancel();
        _live.Dispose();
        _browser.Dispose();
        _lifetime.Dispose();
        _sendGate.Dispose();
    }
}
