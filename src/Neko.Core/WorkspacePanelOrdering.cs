namespace Neko;

/// <summary>Stable, ID-based layout reordering with no conversation mutations.</summary>
internal static class WorkspacePanelOrdering
{
    internal static bool Move(List<string> ordered,string source,string target,bool after)
    {
        if(string.IsNullOrEmpty(source)||string.IsNullOrEmpty(target)||source==target)
            return false;
        var from=ordered.IndexOf(source);
        var targetIndex=ordered.IndexOf(target);
        if(from<0||targetIndex<0)return false;
        var current=ordered.ToArray();
        ordered.RemoveAt(from);
        targetIndex=ordered.IndexOf(target);
        ordered.Insert(targetIndex+(after?1:0),source);
        return !ordered.SequenceEqual(current);
    }

    internal static object Audit()
    {
        var items=new List<string>{"A","B","C","D"};
        var front=Move(items,"D","A",false) &&
            items.SequenceEqual(["D","A","B","C"]);
        var end=Move(items,"A","C",true) &&
            items.SequenceEqual(["D","B","C","A"]);
        var invalid=Move(items,"missing","D",false) is false &&
            items.SequenceEqual(["D","B","C","A"]);
        var noChange=Move(items,"B","C",false) is false;
        return new {passed=front&&end&&invalid&&noChange,
            front,end,invalid,noChange};
    }
}
