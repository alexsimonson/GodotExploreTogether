using ExploreTogether.Core.Net;

namespace ExploreTogether.Core.Tests;

public class PublicServerTests
{
    [Fact]
    public void DevSwitches_AreForbidden_OthersAreFine()
    {
        var forbidden = PublicServer.ForbiddenArguments(["server", "public", "port", "dev-supply", "db", "dev-skip-proximity"]);
        Assert.Equal(["dev-skip-proximity", "dev-supply"], forbidden);
        Assert.Empty(PublicServer.ForbiddenArguments(["server", "public", "city-size", "devious"]));
    }

    [Fact]
    public void InviteCodes_MustMatchExactly()
    {
        Assert.True(PublicServer.InviteCodeMatches("Blue-Otter-42", " Blue-Otter-42 "));
        Assert.False(PublicServer.InviteCodeMatches("Blue-Otter-42", "blue-otter-42"));
        Assert.False(PublicServer.InviteCodeMatches("Blue-Otter-42", ""));
        Assert.False(PublicServer.InviteCodeMatches("Blue-Otter-42", null));
        Assert.False(PublicServer.InviteCodeMatches("", ""));
        Assert.True(PublicServer.InviteCodeStrongEnough("Blue-Otter-42"));
        Assert.False(PublicServer.InviteCodeStrongEnough("abc"));
    }

    [Fact]
    public void RepeatedFailures_BanAnAddress_ForAWhile()
    {
        var guard = new AddressGuard(maxFailures: 3, window: 60, banSeconds: 100);
        Assert.False(guard.Failed("1.2.3.4", 0));
        Assert.False(guard.Failed("1.2.3.4", 1));
        Assert.True(guard.Failed("1.2.3.4", 2));
        Assert.True(guard.IsBanned("1.2.3.4", 50));
        Assert.False(guard.TryConnect("1.2.3.4", 50));
        Assert.False(guard.IsBanned("1.2.3.4", 103));
        Assert.False(guard.IsBanned("5.6.7.8", 50));
    }

    [Fact]
    public void FailuresSpreadOut_OrFollowedBySuccess_DontBan()
    {
        var guard = new AddressGuard(maxFailures: 3, window: 60, banSeconds: 100);
        guard.Failed("a", 0);
        guard.Failed("a", 70);
        Assert.False(guard.Failed("a", 140)); // never 3 within a minute
        guard.Failed("b", 0);
        guard.Failed("b", 1);
        guard.Succeeded("b");
        Assert.False(guard.Failed("b", 2));
    }

    [Fact]
    public void ConnectionsPerAddress_AreCapped()
    {
        var guard = new AddressGuard(maxConnections: 2);
        Assert.True(guard.TryConnect("a", 0));
        Assert.True(guard.TryConnect("a", 0));
        Assert.False(guard.TryConnect("a", 0));
        Assert.True(guard.TryConnect("b", 0));
        guard.Disconnected("a");
        Assert.True(guard.TryConnect("a", 0));
    }
}
