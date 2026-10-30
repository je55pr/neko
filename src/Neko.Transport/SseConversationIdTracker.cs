using System.IO;
using System.Text;
using System.Text.Json;

namespace Neko.Transport;

/// <summary>
/// Observes the ID in an authenticated stream already initiated by the
/// site's own submission function. Does not send requests or persist frames.
/// Bounded buffers, UTF-8 decoder retained across CDP chunks.
/// </summary>
internal sealed class SseConversationIdTracker
{
    private readonly Decoder _decoder=Encoding.UTF8.GetDecoder();
    private readonly SseEventParser _parser=new();
    private long _totalBytes;
    private bool _unusable;
    private const long MaxObservedBytes=16L*1024*1024;

    public string? PushBase64(string encoded)
    {
        if(_unusable || string.IsNullOrWhiteSpace(encoded) ||
           encoded.Length>1_000_000)return null;
        try
        {
            var bytes=Convert.FromBase64String(encoded);
            _totalBytes+=bytes.Length;
            if(_totalBytes>MaxObservedBytes){_unusable=true;return null;}
            var chars=new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var count=_decoder.GetChars(bytes,0,bytes.Length,chars,0,flush:false);
            var events=_parser.Push(new string(chars,0,count));
            foreach(var item in events)
            {
                var found=FindId(item.Data);
                if(found is not null)return found;
            }
        }
        catch(Exception ex) when(ex is FormatException or
            JsonException or InvalidDataException or ArgumentException)
        {
            _unusable=true;
        }
        return null;
    }

    internal static string? FindId(string? payload)
    {
        if(string.IsNullOrWhiteSpace(payload) || payload.Length>250000 ||
           !payload.TrimStart().StartsWith('{'))return null;
        try
        {
            using var json=JsonDocument.Parse(payload);
            var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return null;
            if(TryId(root,"conversation_id",out var direct))
                return direct;
            if(root.TryGetProperty("v",out var nested) &&
               nested.ValueKind==JsonValueKind.Object &&
               TryId(nested,"conversation_id",out var inside))
                return inside;
        }
        catch(JsonException){}
        return null;
    }

    private static bool TryId(JsonElement element,string key,out string? id)
    {
        id=null;
        if(!element.TryGetProperty(key,out var property) ||
           property.ValueKind!=JsonValueKind.String ||
           !Guid.TryParse(property.GetString(),out _))
            return false;
        id=property.GetString();
        return true;
    }
}
