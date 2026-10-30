using System.IO;
using System.Text;

namespace Neko.Transport;

/// <summary>
/// Incremental UTF-8 SSE framing, independent of network chunk boundaries.
/// This parser never decides that an HTTP 200 means generation completed.
/// </summary>
internal sealed class SseEventParser
{
    internal sealed record Event(string Name, string Data, string? Id);
    private const int MaxFrame = 1024 * 1024;
    private readonly StringBuilder _pending = new();
    private readonly List<string> _data = [];
    private string _name = "", _id = "";

    public IReadOnlyList<Event> Push(string text)
    {
        _pending.Append(text);
        if (_pending.Length > MaxFrame) throw new InvalidDataException("SSE frame exceeds limit.");
        var emitted = new List<Event>();
        while (true)
        {
            var raw = _pending.ToString();
            var newline = raw.IndexOf('\n');
            if (newline < 0) break;
            var line = raw[..newline].TrimEnd('\r');
            _pending.Remove(0,newline+1);
            if (line.Length == 0)
            {
                if (_data.Count != 0)
                    emitted.Add(new Event(_name, string.Join("\n",_data), _id.Length==0?null:_id));
                _name=""; _id=""; _data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon+1)..].TrimStart(' ');
            switch(field)
            {
                case "data": _data.Add(value); break;
                case "event": _name = value; break;
                case "id": if (!value.Contains('\0')) _id = value; break;
            }
        }
        return emitted;
    }
}
