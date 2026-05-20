using System.Collections.Concurrent;

namespace SCIMServer.Web.Services;

/// <summary>
/// In-process brute-force protection for the portal login. Tracks failed attempts
/// per (username, ip) tuple with sliding-window decay and applies progressive
/// backoff once the threshold is breached. Singleton so all circuits/requests
/// share the same view.
///
/// Production-grade hardening would persist this to the DB (so process restarts
/// don't reset the counter) and emit metrics — see the issues log. For a single-
/// process demo server, the in-memory variant is enough to defeat scripted
/// password spray.
/// </summary>
public class LoginThrottle
{
    // Thresholds — tunable but conservative.
    private static readonly TimeSpan Window         = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailureDecay   = TimeSpan.FromMinutes(15);
    private const int SoftThreshold = 5;       // start delaying after 5 failures in 15 min
    private const int HardThreshold = 10;      // lock the bucket after 10 failures in 15 min
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, BucketState> _buckets = new();

    public sealed record CheckResult(bool Allowed, TimeSpan RetryAfter, int FailuresInWindow);

    /// <summary>
    /// Call BEFORE attempting to verify the password. If allowed=false, refuse the
    /// login outright with a 429 and the supplied Retry-After.
    /// </summary>
    public CheckResult CheckAllowed(string usernameLower, string ipAddress)
    {
        var key = MakeKey(usernameLower, ipAddress);
        var now = DateTime.UtcNow;
        var b = _buckets.GetOrAdd(key, _ => new BucketState());

        lock (b)
        {
            b.PruneOlderThan(now - Window);

            // Hard lockout in effect?
            if (b.LockoutUntil > now)
            {
                return new CheckResult(false, b.LockoutUntil - now, b.Failures.Count);
            }

            // Soft delay — return Retry-After but don't reject. Caller can choose to
            // sleep, or just surface this to the user as "slow down."
            if (b.Failures.Count >= SoftThreshold)
            {
                var soft = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, b.Failures.Count - SoftThreshold)));
                return new CheckResult(true, soft, b.Failures.Count);
            }

            return new CheckResult(true, TimeSpan.Zero, b.Failures.Count);
        }
    }

    /// <summary>
    /// Call after a successful login — clears the bucket.
    /// </summary>
    public void RegisterSuccess(string usernameLower, string ipAddress)
    {
        _buckets.TryRemove(MakeKey(usernameLower, ipAddress), out _);
    }

    /// <summary>
    /// Call after a failed login. Returns the new failure count.
    /// </summary>
    public int RegisterFailure(string usernameLower, string ipAddress)
    {
        var key = MakeKey(usernameLower, ipAddress);
        var now = DateTime.UtcNow;
        var b = _buckets.GetOrAdd(key, _ => new BucketState());
        lock (b)
        {
            b.PruneOlderThan(now - FailureDecay);
            b.Failures.Add(now);
            if (b.Failures.Count >= HardThreshold)
            {
                b.LockoutUntil = now + LockoutDuration;
            }
            return b.Failures.Count;
        }
    }

    private static string MakeKey(string usernameLower, string ip) =>
        $"{usernameLower}|{ip}";

    private sealed class BucketState
    {
        public List<DateTime> Failures { get; } = new();
        public DateTime LockoutUntil { get; set; } = DateTime.MinValue;

        public void PruneOlderThan(DateTime cutoff)
        {
            // List<DateTime> is fine here — these lists never grow past ~10 entries.
            Failures.RemoveAll(t => t < cutoff);
        }
    }
}
