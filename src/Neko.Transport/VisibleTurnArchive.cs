using System.IO;
using System.Text;
using System.Text.Json;
using Neko;

namespace Neko.Transport;

/// <summary>
/// Append-only, user-visible record of one attempted turn. The text file
/// retains prior drafts even when the provider replaces its output. The
/// accompanying JSONL records append/replace semantics for accurate replay.
/// No internal reasoning, raw network frames, credentials or cookies.
/// </summary>
internal sealed class VisibleTurnArchive : IDisposable
{
    private readonly StreamWriter _human;
    private readonly StreamWriter _events;
    private readonly string _folder;
    private readonly object _gate=new();
    private bool _closed;
    public string AttemptId { get; }
    public string TextPath { get; }
    public string JsonlPath { get; }
    public string LastVisibleText { get; private set; }="";

    public VisibleTurnArchive(string userText,string? conversationId,string? projectId,
        string? rootDirectory=null)
    {
        AttemptId=Guid.NewGuid().ToString("N");
        var folder=rootDirectory??Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Neko","conversation-archives");
        Directory.CreateDirectory(folder);
        _folder=folder;
        TextPath=Path.Combine(folder,AttemptId+".txt");
        JsonlPath=Path.Combine(folder,AttemptId+".jsonl");
        _human=new StreamWriter(new FileStream(TextPath,FileMode.CreateNew,
            FileAccess.Write,FileShare.Read),new UTF8Encoding(false)){AutoFlush=true};
        _events=new StreamWriter(new FileStream(JsonlPath,FileMode.CreateNew,
            FileAccess.Write,FileShare.Read),new UTF8Encoding(false)){AutoFlush=true};
        var stamp=DateTimeOffset.UtcNow.ToString("O");
        _human.WriteLine("Neko conversation archive | "+stamp);
        _human.WriteLine("Attempt: "+AttemptId);
        _human.WriteLine("Conversation: "+(conversationId??"pending"));
        _human.WriteLine("Project: "+(projectId??"inherited"));
        _human.WriteLine();
        _human.WriteLine("USER MESSAGE");
        _human.WriteLine(userText);
        _human.WriteLine();
        Emit("request","",userText,"",conversationId);
    }

    private void Emit(string type,string operation,string text,string messageId,
        string? conversationId=null)
    {
        _events.WriteLine(JsonSerializer.Serialize(new {
            atUtc=DateTimeOffset.UtcNow,
            type,operation,messageId,conversationId,text
        }));
    }

    public void RecordVisible(PublicConversationUpdate update,string snapshot)
    {
        lock(_gate)
        {
            if(_closed || snapshot==LastVisibleText)return;
            var append=snapshot.StartsWith(LastVisibleText,StringComparison.Ordinal);
            var operation=append?"append":"replace";
            var piece=append?snapshot[LastVisibleText.Length..]:snapshot;
            LastVisibleText=snapshot;
            _human.WriteLine("["+DateTimeOffset.UtcNow.ToString("O")+"] "+
                operation.ToUpperInvariant()+" | "+update.MessageId);
            _human.WriteLine(piece);
            _human.WriteLine();
            Emit("visible_output",operation,piece,update.MessageId);
        }
    }

    public void Finish(string status,string? conversationId=null,
        string? authoritativeText=null)
    {
        lock(_gate)
        {
            if(_closed)return;
            if(authoritativeText is not null &&
               authoritativeText!=LastVisibleText)
            {
                var update=new PublicConversationUpdate("server_snapshot",
                    PublicUpdateKind.Final,PublicUpdateOperation.Replace,
                    authoritativeText);
                // Record without recursive lock assumptions outside this class.
                var append=authoritativeText.StartsWith(LastVisibleText,
                    StringComparison.Ordinal);
                var piece=append?
                    authoritativeText[LastVisibleText.Length..]:authoritativeText;
                var op=append?"append":"replace";
                LastVisibleText=authoritativeText;
                _human.WriteLine("["+DateTimeOffset.UtcNow.ToString("O")+
                    "] SERVER "+op.ToUpperInvariant());
                _human.WriteLine(piece);
                _human.WriteLine();
                Emit("server_visible_snapshot",op,piece,update.MessageId,
                    conversationId);
            }
            _human.WriteLine("["+DateTimeOffset.UtcNow.ToString("O")+"] "+
                "END "+status+" | conversation "+(conversationId??"unknown"));
            Emit("terminal",status,"","",conversationId);
            _closed=true;
            _human.Dispose();
            _events.Dispose();
            // A convenient complete, append-only transcript per conversation.
            // Individual turn files remain the primary crash-safe record.
            if(Guid.TryParse(conversationId??"",out _))
            {
                try
                {
                    var aggregate=Path.Combine(_folder,
                        "conversation-"+conversationId+".txt");
                    using var stream=new FileStream(aggregate,FileMode.Append,
                        FileAccess.Write,FileShare.Read);
                    using var writer=new StreamWriter(stream,new UTF8Encoding(false));
                    writer.WriteLine();
                    writer.WriteLine(new string('=',64));
                    writer.Write(File.ReadAllText(TextPath));
                    writer.Flush();
                    stream.Flush(flushToDisk:true);
                }
                catch(IOException){} // Per-turn archive remains authoritative.
            }
        }
    }

    public void Dispose()=>Finish("observer_disposed");
}
