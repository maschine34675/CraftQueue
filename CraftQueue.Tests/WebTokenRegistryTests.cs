using CraftQueue.Server;
using SPTarkov.Server.Core.Models.Common;
using Xunit;

namespace CraftQueue.Tests;
public sealed class WebTokenRegistryTests
{
    private static readonly MongoId playerA = "0123456789abcdef01234567";
    private static readonly MongoId playerB = "89abcdef0123456789abcdef";

    [Fact]
    public void HostTokenMayAccessEveryProfile()
    {
        var registry = new WebTokenRegistry();

        WebTokenScope? scope = registry.Resolve(registry.HostToken);

        Assert.NotNull(scope);
        Assert.True(scope.MayAccess(playerA));
        Assert.True(scope.MayAccess(playerB));
    }

    [Fact]
    public void SessionTokenIsLockedToItsOwnProfile()
    {
        var registry = new WebTokenRegistry();
        string token = registry.GetOrCreateSessionToken(playerA);

        WebTokenScope? scope = registry.Resolve(token);

        Assert.NotNull(scope);
        Assert.True(scope.MayAccess(playerA));
        Assert.False(scope.MayAccess(playerB));
    }

    [Fact]
    public void EachSessionGetsItsOwnStableToken()
    {
        var registry = new WebTokenRegistry();

        string first = registry.GetOrCreateSessionToken(playerA);
        string second = registry.GetOrCreateSessionToken(playerB);

        Assert.NotEqual(first, second);
        Assert.NotEqual(registry.HostToken, first);
        Assert.Equal(first, registry.GetOrCreateSessionToken(playerA));
    }

    [Fact]
    public void UnknownAndEmptyTokensResolveToNothing()
    {
        var registry = new WebTokenRegistry();
        _ = registry.GetOrCreateSessionToken(playerA);

        Assert.Null(registry.Resolve(null));
        Assert.Null(registry.Resolve(""));
        Assert.Null(registry.Resolve("not-a-token"));
    }
}
