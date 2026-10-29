using System.Reflection;
using System.Runtime.CompilerServices;
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
        Exception? failure=null;
        var thread=new Thread(() =>
        {
            try
            {
                var provider=new ProbeProvider();
                var window=new MainWindow(provider);
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)),
            "The STA provider handoff test did not finish.");
        if(failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if(failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ProbeProvider:IConversationProvider,IDisposable
    {
        public int BrowseCount {get;private set;}
        public bool Disposed {get;private set;}
        public Task<IReadOnlyList<ConversationSummary>> BrowseAsync(
            CancellationToken cancellation)
        {
            BrowseCount++;
            return Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
        }
        public Task<VisibleConversation?> ReadAsync(string id,CancellationToken cancellation)=>
            Task.FromResult<VisibleConversation?>(null);
        public Task<SendReceipt> CreateAsync(string text,CancellationToken cancellation)=>
            Task.FromResult(new SendReceipt(SendDisposition.Rejected));
        public Task<SendReceipt> SendAsync(string id,string text,
            CancellationToken cancellation)=>
            Task.FromResult(new SendReceipt(SendDisposition.Rejected));
        public Task<StopReceipt> StopAsync(string id,CancellationToken cancellation)=>
            Task.FromResult(new StopReceipt(StopDisposition.NotActive));
        public async IAsyncEnumerable<PublicConversationUpdate> WatchAsync(
            string id,[EnumeratorCancellation]CancellationToken cancellation)
        {
            await Task.CompletedTask;
            yield break;
        }
        public void Dispose()=>Disposed=true;
    }
}
