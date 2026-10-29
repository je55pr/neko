using System.IO;
using System.Text.Json;

namespace Neko;

internal sealed record OfflineBoard(string Id,string Name,List<string>? ConversationIds=null);

internal sealed class OfflineBoardStore
{
    internal const int MaxBoards=24;
    internal const int MaxPanelsPerBoard=8;
    private sealed record SavedBoards(string ActiveBoardId,List<OfflineBoard> Boards);
    private readonly string _path;
    private List<OfflineBoard> _boards=[];
    public IReadOnlyList<OfflineBoard> Boards=>_boards;
    public string ActiveBoardId {get;private set;}="";
    public OfflineBoardStore(string directory)
    {
        _path=Path.Combine(directory,"boards.json");
        Load();
    }
    private void Load()
    {
        try
        {
            if(File.Exists(_path))
            {
                var data=JsonSerializer.Deserialize<SavedBoards>(File.ReadAllText(_path));
                if(data?.Boards is {Count:>0 and <=MaxBoards} &&
                   data.Boards.All(x=>Guid.TryParse(x.Id,out _) &&
                       !string.IsNullOrWhiteSpace(x.Name) && x.Name.Length<=60 &&
                       (x.ConversationIds is null ||
                        (x.ConversationIds.Count<=MaxPanelsPerBoard &&
                         x.ConversationIds.All(id=>!string.IsNullOrWhiteSpace(id) && id.Length<=256) &&
                         x.ConversationIds.Distinct(StringComparer.Ordinal).Count()==x.ConversationIds.Count))) &&
                   data.Boards.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()==data.Boards.Count &&
                   data.Boards.Any(x=>x.Id==data.ActiveBoardId))
                {
                    _boards=data.Boards;
                    ActiveBoardId=data.ActiveBoardId;
                    return;
                }
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException){}
        var first=new OfflineBoard(Guid.NewGuid().ToString("D"),"Workspace 1");
        _boards=[first];
        ActiveBoardId=first.Id;
    }
    public bool Create()
    {
        if(_boards.Count>=MaxBoards)return false;
        var board=new OfflineBoard(Guid.NewGuid().ToString("D"),"Workspace "+(_boards.Count+1));
        _boards.Add(board);
        ActiveBoardId=board.Id;
        Save();
        return true;
    }
    public bool Switch(string? id)
    {
        if(!_boards.Any(x=>x.Id==id))return false;
        ActiveBoardId=id!;
        Save();
        return true;
    }
    public bool Rename(string? id,string? name)
    {
        name=name?.Trim();
        if(string.IsNullOrWhiteSpace(name)||name.Length>60)return false;
        var index=_boards.FindIndex(x=>x.Id==id);
        if(index<0)return false;
        _boards[index]=_boards[index] with {Name=name};
        Save();
        return true;
    }
    public bool Delete(string? id)
    {
        if(_boards.Count<=1)return false;
        if(_boards.RemoveAll(x=>x.Id==id)!=1)return false;
        if(ActiveBoardId==id)ActiveBoardId=_boards[0].Id;
        Save();
        return true;
    }
    public IReadOnlyList<string> ConversationsForActiveBoard()=>
        _boards.First(x=>x.Id==ActiveBoardId).ConversationIds?.ToArray() ?? [];

    public bool AddConversation(string id)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>256)return false;
        var index=_boards.FindIndex(x=>x.Id==ActiveBoardId);
        if(index<0)return false;
        var ids=_boards[index].ConversationIds?.ToList() ?? [];
        if(ids.Contains(id,StringComparer.Ordinal))return true;
        if(ids.Count>=MaxPanelsPerBoard)return false;
        ids.Add(id);
        _boards[index]=_boards[index] with {ConversationIds=ids};
        Save();
        return true;
    }

    public bool RemoveConversation(string id)
    {
        var index=_boards.FindIndex(x=>x.Id==ActiveBoardId);
        if(index<0)return false;
        var ids=_boards[index].ConversationIds?.ToList() ?? [];
        if(!ids.Remove(id))return false;
        _boards[index]=_boards[index] with {ConversationIds=ids};
        Save();
        return true;
    }

    private void Save()
    {
        var directory=Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp=Path.Combine(directory,Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            File.WriteAllText(temp,JsonSerializer.Serialize(
                new SavedBoards(ActiveBoardId,_boards)));
            File.Move(temp,_path,true);
        }
        finally
        {
            if(File.Exists(temp))File.Delete(temp);
        }
    }
}
