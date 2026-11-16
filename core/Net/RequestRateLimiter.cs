namespace ExploreTogether.Core.Net;

/// <summary>How fast one kind of client request may arrive: a sustained rate plus a burst allowance.</summary>
public readonly record struct RateLimit(double PerSecond, double Burst);

/// <summary>What the limiter decided about one request.</summary>
public enum RateDecision
{
    /// <summary>Within the limit: handle it.</summary>
    Allow,
    /// <summary>Over the limit: ignore it.</summary>
    Drop,
    /// <summary>Over the limit so persistently that the peer should be disconnected.</summary>
    Disconnect,
}

/// <summary>
/// Per-peer, per-request-kind token buckets, so one client spamming requests can't stall the server
/// (item security review, finding 2). Each bucket refills at PerSecond up to Burst; a request costs one token.
/// A peer that has more than <see cref="MaxDropsPerWindow"/> requests dropped within <see cref="DropWindowSeconds"/>
/// is reported for disconnection. Time is passed in (seconds, any monotonic clock), which keeps it testable.
/// </summary>
public sealed class RequestRateLimiter(IReadOnlyDictionary<string, RateLimit> limits)
{
    public const double DropWindowSeconds = 10;
    public const int MaxDropsPerWindow = 200;

    private sealed class Bucket { public double Tokens; public double Updated; }
    private sealed class PeerState
    {
        public readonly Dictionary<string, Bucket> Buckets = new();
        public double DropWindowStart = double.NegativeInfinity;
        public int DropsInWindow;
    }

    private readonly Dictionary<long, PeerState> _peers = new();

    /// <summary>Decides whether a request of this kind from this peer, arriving at time <paramref name="now"/>, is handled.
    /// Kinds without a configured limit are always allowed.</summary>
    public RateDecision Check(long peer, string kind, double now)
    {
        if (!limits.TryGetValue(kind, out var limit)) return RateDecision.Allow;
        if (!_peers.TryGetValue(peer, out var state)) _peers[peer] = state = new PeerState();
        if (!state.Buckets.TryGetValue(kind, out var bucket))
            state.Buckets[kind] = bucket = new Bucket { Tokens = limit.Burst, Updated = now };

        bucket.Tokens = Math.Min(limit.Burst, bucket.Tokens + Math.Max(0, now - bucket.Updated) * limit.PerSecond);
        bucket.Updated = now;
        if (bucket.Tokens >= 1)
        {
            bucket.Tokens -= 1;
            return RateDecision.Allow;
        }

        if (now - state.DropWindowStart > DropWindowSeconds)
        {
            state.DropWindowStart = now;
            state.DropsInWindow = 0;
        }
        return ++state.DropsInWindow > MaxDropsPerWindow ? RateDecision.Disconnect : RateDecision.Drop;
    }

    /// <summary>Forgets a peer (call on disconnect).</summary>
    public void Remove(long peer) => _peers.Remove(peer);
}
