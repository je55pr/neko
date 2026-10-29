using System.Text;

namespace Neko;

/// <summary>
/// Input must be normalised by a provider adapter before reaching this reducer.
/// Hidden content is discarded immediately and never exposed or persisted.
/// </summary>
internal enum StreamContent { Final, PublicProgress, ToolActivity, Hidden }

internal enum StreamOperation { Start, Replace, Append, Complete, Fail }

internal sealed record NormalizedStreamUpdate(
    string MessageId,StreamContent Content,StreamOperation Operation,
    string Text="",int PartIndex=0,string Title="");

internal sealed record StreamActivity(
    string MessageId,StreamContent Content,string Title,string Text,bool Completed);

internal sealed class LiveConversationReducer
{
    private const int MaxFinalChars=1_000_000;
    private const int MaxActivityChars=18_000;
    private const int MaxActivityItems=48;
    private const int MaxParts=100;
    private const int MaxHiddenIds=256;

    private sealed class Activity(StreamContent content,string title)
    {
        public StreamContent Content=content;
        public string Title=title;
        public readonly SortedDictionary<int,StringBuilder> Parts=[];
        public int Length;
        public bool Complete;
    }

    private readonly Dictionary<string,Activity> _activity=new(StringComparer.Ordinal);
    private readonly List<string> _activityOrder=[];
    private readonly HashSet<string> _hidden=new(StringComparer.Ordinal);
    private bool _visibilityOverflow;
    private bool _requireStart;
    private bool _finalFailed;
    private bool _finalTextOverflow;
    private readonly SortedDictionary<int,StringBuilder> _finalParts=[];
    private string _finalId="";
    private string _cachedFinal="";
    private bool _finalDirty=true;
    private int _finalLength;

    public string FinalMessageId=>_finalId;
    public bool Completed {get;private set;}
    public string Error {get;private set;}="";
    public string FinalText
    {
        get
        {
            if(_finalDirty)
            {
                _cachedFinal=string.Concat(_finalParts.Values.Select(x=>x.ToString()));
                _finalDirty=false;
            }
            return _cachedFinal;
        }
    }

    public StreamActivity[] Activities=>_activityOrder.Select(id=>
    {
        var record=_activity[id];
        return new StreamActivity(id,record.Content,record.Title,
            string.Join("\n",record.Parts.Values.Select(p=>p.ToString())),
            record.Complete);
    }).ToArray();

    public bool Apply(NormalizedStreamUpdate update)
    {
        if(_visibilityOverflow || string.IsNullOrWhiteSpace(update.MessageId) ||
           update.MessageId.Length>256 || update.PartIndex<0 ||
           update.PartIndex>=MaxParts)return false;
        var id=update.MessageId;
        if(update.Content==StreamContent.Hidden)
        {
            if(!_hidden.Contains(id) && _hidden.Count>=MaxHiddenIds)
            {
                // If hidden-ID tracking fills, stop exposing all updates.
                _visibilityOverflow=true;
                ResetFinal();
                _activity.Clear();
                _activityOrder.Clear();
                Error="visibility_limit_reached";
                return true;
            }
            _hidden.Add(id);
            var removed=_activity.Remove(id);
            if(removed)_activityOrder.Remove(id);
            if(_finalId!=id)return removed;
            ResetFinal();
            _requireStart=true;
            return true;
        }
        if(_hidden.Contains(id))return false;

        if(update.Content==StreamContent.Final)
            return ApplyFinal(update);
        if(update.Content is not (StreamContent.PublicProgress or
                                 StreamContent.ToolActivity))return false;
        return ApplyActivity(update);
    }

    private bool ApplyFinal(NormalizedStreamUpdate update)
    {
        var id=update.MessageId;
        if(update.Operation==StreamOperation.Start)
        {
            ResetFinal();
            _requireStart=false;
            _finalId=id;
            return true;
        }
        if(_finalId.Length==0)
        {
            if(_requireStart || update.Operation is StreamOperation.Complete or
                StreamOperation.Fail)return false;
            _finalId=id;
        }
        if(id!=_finalId)return false;
        if(update.Operation==StreamOperation.Complete)
        {
            if(Completed || _finalLength==0 || _finalFailed ||
               _finalTextOverflow)return false;
            Completed=true;
            return true;
        }
        if(update.Operation==StreamOperation.Fail)
        {
            if(Completed || _finalFailed)return false;
            _finalFailed=true;
            Error="stream_failed";
            return true;
        }
        if(Completed || _finalFailed)return false;
        if(update.Operation is not (StreamOperation.Append or
            StreamOperation.Replace))return false;
        var append=update.Operation==StreamOperation.Append;
        if(append && update.Text.Length==0)return false;
        _finalParts.TryGetValue(update.PartIndex,out var part);
        var oldLength=part?.Length??0;
        var newLength=append?(long)oldLength+update.Text.Length:update.Text.Length;
        if(newLength>MaxFinalChars ||
           _finalLength-oldLength+newLength>MaxFinalChars)
        {
            _finalTextOverflow=true;
            Error="text_limit_reached";
            return false;
        }
        part??=new StringBuilder();
        if(!append)part.Clear();
        part.Append(update.Text);
        _finalParts[update.PartIndex]=part;
        _finalLength+=(int)newLength-oldLength;
        _finalDirty=true;
        _finalTextOverflow=false;
        if(Error=="text_limit_reached")Error="";
        return true;
    }

    private bool ApplyActivity(NormalizedStreamUpdate update)
    {
        var id=update.MessageId;
        if(!_activity.TryGetValue(id,out var activity))
        {
            if(update.Operation is StreamOperation.Complete or StreamOperation.Fail ||
               _activityOrder.Count>=MaxActivityItems ||
               update.Operation==StreamOperation.Append && update.Text.Length==0)
                return false;
            if(update.Operation is StreamOperation.Append or StreamOperation.Replace &&
               update.Text.Length>MaxActivityChars)
            {
                Error="activity_limit_reached";
                return false;
            }
            activity=new Activity(update.Content,SafeTitle(update.Title));
            _activity[id]=activity;
            _activityOrder.Add(id);
        }
        if(activity.Content!=update.Content)return false;
        if(update.Operation==StreamOperation.Start)
        {
            if(activity.Complete)return false;
            activity.Parts.Clear();
            activity.Length=0;
            if(update.Title.Length>0)activity.Title=SafeTitle(update.Title);
            return true;
        }
        if(update.Operation is StreamOperation.Complete or StreamOperation.Fail)
        {
            if(activity.Complete)return false;
            activity.Complete=true;
            if(update.Operation==StreamOperation.Fail)
                activity.Title="Activity unavailable";
            return true;
        }
        if(activity.Complete)return false;
        if(update.Operation is not (StreamOperation.Append or
            StreamOperation.Replace))return false;
        var append=update.Operation==StreamOperation.Append;
        if(append && update.Text.Length==0)return false;
        activity.Parts.TryGetValue(update.PartIndex,out var part);
        var oldLength=part?.Length??0;
        var nextLength=append?(long)oldLength+update.Text.Length:update.Text.Length;
        if(nextLength>MaxActivityChars ||
           activity.Length-oldLength+nextLength>MaxActivityChars)
        {
            Error="activity_limit_reached";
            return false;
        }
        part??=new StringBuilder();
        if(!append)part.Clear();
        part.Append(update.Text);
        activity.Parts[update.PartIndex]=part;
        activity.Length+=(int)nextLength-oldLength;
        return true;
    }

    private static string SafeTitle(string title)=>
        title.Length>120?title[..120]:title;

    private void ResetFinal()
    {
        _finalParts.Clear();
        _finalLength=0;
        _finalId="";
        _finalDirty=true;
        _cachedFinal="";
        Completed=false;
        _finalFailed=false;
        _finalTextOverflow=false;
        Error="";
    }
}
