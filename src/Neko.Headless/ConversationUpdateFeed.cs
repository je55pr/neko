using System.Runtime.CompilerServices;
using Neko;

namespace Neko.Headless;

/// <summary>
/// One turn's in-memory event log. Multiple subscribers can independently replay
/// the events, including a terminal result delivered before they connected.
/// History persists in the journal; this feed is only for the current process.
/// </summary>
internal sealed class ConversationUpdateFeed
{
    private readonly object _gate = new();
    private readonly List<PublicConversationUpdate> _events = [];
    private TaskCompletionSource _changed = NewSignal();
    private bool _finished;

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Publish(PublicConversationUpdate update)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_finished) return;
            _events.Add(update);
            if (update.Kind == PublicUpdateKind.Final &&
                update.Operation is PublicUpdateOperation.Complete or PublicUpdateOperation.Fail)
                _finished = true;
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
