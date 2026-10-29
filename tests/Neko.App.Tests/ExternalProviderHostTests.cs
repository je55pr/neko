using System.IO;
using System.Reflection;
using System.Windows;
using Neko;

namespace Neko.App.Tests;

public sealed class ExternalProviderHostTests
{
    [Fact]
    public void MainWindowRetainsOrdinaryLaunchAndSupportsExplicitProvider()
    {
        var type=typeof(MainWindow);
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        Assert.NotNull(type.GetConstructor([typeof(IConversationProvider)]));
    }

    [Fact]
    public void ExplicitProviderIsActuallyUsedAndDisposedOnWindowClose()
    {
        var profile=Path.Combine(Path.GetTempPath(),
            "NekoHostTest-"+Guid.NewGuid().ToString("N"));
        Exception? failure=null;
        var thread=new Thread(() =>
        {
            try
            {
                var provider=new ProbeProvider();
                var window=new MainWindow(provider,profile);
                try
                {
                    // Observe the actual workspace, not merely the constructor.
                    var workspace=(ConversationWorkspaceHost)typeof(MainWindow)
                        .GetField("_workspace",BindingFlags.Instance|
                            BindingFlags.NonPublic)!.GetValue(window)!;
                    workspace.RefreshLibraryAsync().GetAwaiter().GetResult();
                    Assert.Equal(1,provider.BrowseCount);
                    Assert.Same(provider,typeof(MainWindow).GetField("_provider",
                        BindingFlags.Instance|BindingFlags.NonPublic)!
                        .GetValue(window));
                    Assert.False(provider.Disposed);
                }
                finally
                {
                    window.Close();
                }
                Assert.True(provider.Disposed);
            }
            catch(Exception ex){failure=ex;}
        });
        thread.IsBackground=true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)),
                "The STA provider handoff test did not finish.");
            if(failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            if(Directory.Exists(profile))Directory.Delete(profile,true);
        }
    }

    [Fact]
    public void ExternalProviderConstructorRejectsNull()
    {
        Exception? failure=null;
        var thread=new Thread(() =>
        {
            try
            {
                Assert.Throws<ArgumentNullException>(()=>
                    new MainWindow((IConversationProvider)null!));
            }
            catch(Exception ex){failure=ex;}
        });
        thread.IsBackground=true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if(failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ProbeProvider:IConversationProvider,IDisposable
    {
        private readonly DisconnectedConversationProvider _fallback=new();
        public int BrowseCount {get;private set;}
        public bool Disposed {get;private set;}
        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(
            CancellationToken cancellation)
        {
            BrowseCount++;
            return _fallback.BrowseAsync(cancellation);
        }
        public Task<VisibleConversation?> ReadAsync(string id,CancellationToken cancellation)=>
            _fallback.ReadAsync(id,cancellation);
        public Task<SendReceipt> CreateAsync(string text,CancellationToken cancellation)=>
            _fallback.CreateAsync(text,cancellation);
        public Task<SendReceipt> SendAsync(string id,string text,
            CancellationToken cancellation)=>
            _fallback.SendAsync(id,text,cancellation);
        public Task<StopReceipt> StopAsync(string id,CancellationToken cancellation)=>
            _fallback.StopAsync(id,cancellation);
        public IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
            string id,CancellationToken cancellation)=>
            _fallback.WatchAsync(id,cancellation);
        public void Dispose()=>Disposed=true;
    }
}
