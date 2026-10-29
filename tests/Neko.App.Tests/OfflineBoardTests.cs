using System.IO;
using System.Text.Json;
using Neko;

namespace Neko.App.Tests;

public sealed class OfflineBoardTests
{
    private static string TempFolder()=>
        Path.Combine(Path.GetTempPath(),"Neko-Offline-Test-"+
            Guid.NewGuid().ToString("N"));

    [Fact]
    public void BoardCreationAndRenamePersistAcrossRestart()
    {
        var folder=TempFolder();
        try
        {
            var boards=new OfflineBoardStore(folder);
            var original=boards.ActiveBoardId;
            Assert.Single(boards.Boards);
            Assert.True(boards.Create());
            var second=boards.ActiveBoardId;
            Assert.NotEqual(original,second);
            Assert.True(boards.Rename(second,"  Work board  "));
            Assert.True(boards.Switch(original));

            var reopened=new OfflineBoardStore(folder);
            Assert.Equal(original,reopened.ActiveBoardId);
            Assert.Equal(2,reopened.Boards.Count);
            Assert.Equal("Work board",reopened.Boards.Single(b=>b.Id==second).Name);
        }
        finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }

    [Fact]
    public void DeletionCannotRemoveTheLastBoard()
    {
        var folder=TempFolder();
        try
        {
            var boards=new OfflineBoardStore(folder);
            Assert.False(boards.Delete(boards.ActiveBoardId));
            Assert.True(boards.Create());
            var second=boards.ActiveBoardId;
            Assert.True(boards.Delete(second));
            Assert.Single(boards.Boards);
            Assert.False(boards.Delete(second));
            Assert.False(boards.Delete("invalid"));
        }
        finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }

    [Fact]
    public void InvalidIdsAndNamesDoNotMutateAStoredBoard()
    {
        var folder=TempFolder();
        try
        {
            var boards=new OfflineBoardStore(folder);
            var id=boards.ActiveBoardId;
            Assert.False(boards.Switch(Guid.NewGuid().ToString("D")));
            Assert.False(boards.Rename(id,"   "));
            Assert.False(boards.Rename(id,new string('X',61)));
            Assert.False(boards.Rename("unknown","Valid name"));
            Assert.Equal("Workspace 1",Assert.Single(boards.Boards).Name);
        }
        finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }

    [Fact]
    public void BoardCapacityIsBounded()
    {
        var folder=TempFolder();
        try
        {
            var boards=new OfflineBoardStore(folder);
            for(var i=1;i<OfflineBoardStore.MaxBoards;i++)
                Assert.True(boards.Create());
            Assert.False(boards.Create());
            Assert.Equal(OfflineBoardStore.MaxBoards,boards.Boards.Count);
        }
        finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }

    [Fact]
    public void CorruptOrDuplicateBoardDataIsRejected()
    {
        var folder=TempFolder();
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder,"boards.json"),"{invalid");
            var recovered=new OfflineBoardStore(folder);
            Assert.Single(recovered.Boards);
            var id=Guid.NewGuid().ToString("D");
            File.WriteAllText(Path.Combine(folder,"boards.json"),
                JsonSerializer.Serialize(new {
                    ActiveBoardId=id,
                    Boards=new[]{new {Id=id,Name="A"},new {Id=id,Name="B"}}
                }));
            var duplicate=new OfflineBoardStore(folder);
            Assert.Single(duplicate.Boards);
            Assert.Equal("Workspace 1",duplicate.Boards[0].Name);
        }
        finally {Directory.Delete(folder,true);}
    }

    [Theory]
    [InlineData("https://neko.internal/index.html",true)]
    [InlineData("https://neko.internal/scripts/NekoCanvas.js",true)]
    [InlineData("http://neko.internal/index.html",false)]
    [InlineData("https://neko.internal.evil.test/",false)]
    [InlineData("https://neko.internal:4443/",false)]
    [InlineData("https://example.test/",false)]
    [InlineData("file:///C:/example.html",false)]
    public void BrowserAcceptsOnlyItsBundledOrigin(string address,bool expected)
    {
        Assert.Equal(expected,MainWindow.IsLocalAddress(address));
    }
}
