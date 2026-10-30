using System.IO;
using System.Text;

namespace Neko.Transport;

/// <summary>
/// Incremental SSE framing. Limits apply to an incomplete line or event,
/// not to the size of a network chunk containing many valid events.
/// </summary>
internal sealed class SseEventParser
{
    internal sealed record Event(string Name,string Data,string? Id);
    private const int MaxFrame=1024*1024;
    private readonly StringBuilder _line=new();
    private readonly List<string> _data=[];
    private int _eventSize;
    private string _name="",_id="";

    public IReadOnlyList<Event> Push(string text)
    {
        var emitted=new List<Event>();
        for(var pos=0;pos<text.Length;)
        {
            var end=text.IndexOf('\n',pos);
            if(end<0)
            {
                _line.Append(text,pos,text.Length-pos);
                if(_line.Length>MaxFrame)
                    throw new InvalidDataException("SSE line exceeds limit.");
                break;
            }
            _line.Append(text,pos,end-pos);
            if(_line.Length>MaxFrame)
                throw new InvalidDataException("SSE line exceeds limit.");
            var line=_line.ToString().TrimEnd('\r');
            _line.Clear();
            pos=end+1;
            if(line.Length==0)
            {
                if(_data.Count>0)
                    emitted.Add(new Event(_name,string.Join("\n",_data),
                        _id.Length==0?null:_id));
                _name="";_id="";_eventSize=0;_data.Clear();
                continue;
            }
            if(line.StartsWith(':'))continue;
            var colon=line.IndexOf(':');
            var field=colon<0?line:line[..colon];
            var value=colon<0?"":line[(colon+1)..].TrimStart(' ');
            switch(field)
            {
                case "data":
                    _eventSize+=value.Length+1;
                    if(_eventSize>MaxFrame)
                        throw new InvalidDataException("SSE event exceeds limit.");
                    _data.Add(value);
                    break;
                case "event":
                    _name=value;
                    break;
                case "id":
                    if(!value.Contains('\0'))_id=value;
                    break;
                default:
                    // Unknown fields are valid SSE extensions.
                    break;
            }
        }
        return emitted;
    }
}
