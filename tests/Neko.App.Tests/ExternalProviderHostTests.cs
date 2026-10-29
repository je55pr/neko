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
    public void ExternalProviderConstructorDoesNotNeedConfigurationOrAssemblyPath()
    {
        var overload=typeof(MainWindow).GetConstructor(
            [typeof(IConversationProvider)]);
        Assert.NotNull(overload);
        Assert.Single(overload.GetParameters());
        Assert.Equal(typeof(IConversationProvider),
            overload.GetParameters()[0].ParameterType);
    }
}
