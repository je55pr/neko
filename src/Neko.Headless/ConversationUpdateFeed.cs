using System.Runtime.CompilerServices;
using Neko;

namespace Neko.Headless;

/// <summary>Content-free, per-turn metrics for updates published to the public feed.</summary>
internal sealed record ConversationProgressSnapshot(
    DateTimeOffset StartedAt,
    long ElapsedMilliseconds,
    DateTimeOffset? LastPublicUpdateAt,
    long PublicEventCount,
    string? LastPublicUpdateType,
    bool IsTerminal,
    string? TerminalOperation);

/// <summary>
/// One turn's in-memory event log. Multiple subscribers can independently replay
/// the events, including a terminal result delivered before they connected.
/// History persists in the journal; progress metrics are process-local only.
/// </summary>
internal sealed class ConversationUpdateFeed
{
    private readonly object _gate = new();
    private readonly List<PublicConversationUpdate> _events = [];
    private readonly TimeProvider _time;
    private readonly long _startedTicks;
    private readonly DateTimeOffset _startedAt;
    private TaskCompletionSource _changed = NewSignal();
    private DateTimeOffset? _lastPublicUpdateAt;
    private string? _lastPublicUpdateType;
    private string? _terminalOperation;
    private long? _terminalTicks;
    private bool _finished;

    public string TurnId { get; } = Guid.NewGuid().ToString("N");

    public ConversationUpdateFeed(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _startedTicks = _time.GetTimestamp();
        _startedAt = _time.GetUtcNow();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConversationProgressSnapshot Snapshot()
    {
        lock (_gate)
        {
            // A monotonic clock keeps elapsed time stable across system clock changes.
            var elapsed = _time.GetElapsedTime(
                _startedTicks, _terminalTicks ?? _time.GetTimestamp());
            return new ConversationProgressSnapshot(
                _startedAt,
                Math.Max(0, (long)elapsed.TotalMilliseconds),
                _lastPublicUpdateAt,
                _events.Count,
                _lastPublicUpdateType,
                _finished,
                _terminalOperation);
        }
    }

    public void Publish(PublicConversationUpdate update)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_finished) return;
            _events.Add(update);
            _lastPublicUpdateAt = _time.GetUtcNow();
            _lastPublicUpdateType = $"{update.Kind}.{update.Operation}";
            if (update.Kind == PublicUpdateKind.Final &&
                update.Operation is PublicUpdateOperation.Complete or PublicUpdateOperation.Fail)
            {
                _finished = true;
                _terminalOperation = update.Operation.ToString();
                _terminalTicks = _time.GetTimestamp();
            }
            signal = _changed;
            _changed = NewSignal();
        }
        signal.TrySetResult();
    }

    public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var index = 0;
        while (true)
        {
            PublicConversationUpdate? update = null;
            Task? next = null;
            bool finished;
            lock (_gate)
            {
                if (index < _events.Count) update = _events[index++];
                else if (!_finished) next = _changed.Task;
                finished = _finished;
            }
            if (update is null && finished) yield break;
            if (update is not null) yield return update;
            else await next!.WaitAsync(cancellation);
        }
    }
}
