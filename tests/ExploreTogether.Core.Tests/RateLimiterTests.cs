using ExploreTogether.Core.Net;

namespace ExploreTogether.Core.Tests;

public class RateLimiterTests
{
    private static RequestRateLimiter Limiter(double perSecond = 10, double burst = 5) =>
        new(new Dictionary<string, RateLimit> { ["MoveItem"] = new(perSecond, burst) });

    [Fact]
    public void AllowsTheBurst_ThenDrops()
    {
        var limiter = Limiter(burst: 5);
        for (var i = 0; i < 5; i++) Assert.Equal(RateDecision.Allow, limiter.Check(1, "MoveItem", 0));
        Assert.Equal(RateDecision.Drop, limiter.Check(1, "MoveItem", 0));
    }

    [Fact]
    public void RefillsOverTime_UpToTheBurst()
    {
        var limiter = Limiter(perSecond: 10, burst: 5);
        for (var i = 0; i < 5; i++) limiter.Check(1, "MoveItem", 0);
        Assert.Equal(RateDecision.Drop, limiter.Check(1, "MoveItem", 0.05)); // half a token back
        Assert.Equal(RateDecision.Allow, limiter.Check(1, "MoveItem", 0.15)); // one token back
        // A long pause refills only up to the burst.
        for (var i = 0; i < 5; i++) Assert.Equal(RateDecision.Allow, limiter.Check(1, "MoveItem", 100));
        Assert.Equal(RateDecision.Drop, limiter.Check(1, "MoveItem", 100));
    }

    [Fact]
    public void SteadyRateWithinTheLimit_IsNeverDropped()
    {
        var limiter = Limiter(perSecond: 10, burst: 2);
        for (var i = 0; i < 1000; i++)
            Assert.Equal(RateDecision.Allow, limiter.Check(1, "MoveItem", i * 0.1));
    }

    [Fact]
    public void PeersAndKindsAreSeparate_AndUnlistedKindsAreAllowed()
    {
        var limiter = Limiter(burst: 1);
        Assert.Equal(RateDecision.Allow, limiter.Check(1, "MoveItem", 0));
        Assert.Equal(RateDecision.Drop, limiter.Check(1, "MoveItem", 0));
        Assert.Equal(RateDecision.Allow, limiter.Check(2, "MoveItem", 0));
        for (var i = 0; i < 1000; i++) Assert.Equal(RateDecision.Allow, limiter.Check(1, "Unlisted", 0));
    }

    [Fact]
    public void PersistentFlooding_AsksForADisconnect()
    {
        var limiter = Limiter(burst: 1);
        limiter.Check(1, "MoveItem", 0);
        for (var i = 0; i < RequestRateLimiter.MaxDropsPerWindow; i++)
            Assert.Equal(RateDecision.Drop, limiter.Check(1, "MoveItem", 0));
        Assert.Equal(RateDecision.Disconnect, limiter.Check(1, "MoveItem", 0));
    }

    [Fact]
    public void DropsSpreadOverTime_DontDisconnect()
    {
        var limiter = Limiter(perSecond: 1, burst: 1);
        // 150 dropped requests per window, for several windows.
        var t = 0.0;
        for (var i = 0; i < 1000; i++, t += RequestRateLimiter.DropWindowSeconds / 150.0)
            Assert.NotEqual(RateDecision.Disconnect, limiter.Check(1, "MoveItem", t));
    }

    [Fact]
    public void Remove_ForgetsThePeer()
    {
        var limiter = Limiter(burst: 1);
        limiter.Check(1, "MoveItem", 0);
        limiter.Remove(1);
        Assert.Equal(RateDecision.Allow, limiter.Check(1, "MoveItem", 0));
    }
}
