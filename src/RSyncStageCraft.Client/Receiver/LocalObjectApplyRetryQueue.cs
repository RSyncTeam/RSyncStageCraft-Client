using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Receiver;

public readonly record struct LocalObjectApplyRetryPolicy(
    int MaximumAttempts,
    TimeSpan InitialDelay,
    TimeSpan MaximumDelay,
    double JitterRatio)
{
    public static LocalObjectApplyRetryPolicy Default => new(
        MaximumAttempts: 5,
        InitialDelay: TimeSpan.FromMilliseconds(250),
        MaximumDelay: TimeSpan.FromSeconds(8),
        JitterRatio: 0.2);

    public bool IsValid =>
        MaximumAttempts is > 0 and < int.MaxValue &&
        InitialDelay > TimeSpan.Zero &&
        MaximumDelay >= InitialDelay &&
        JitterRatio is >= 0 and <= 0.5;

    public TimeSpan GetDelay(LocalObjectApplyToken token, int attempt)
    {
        if (!IsValid)
            throw new InvalidOperationException("Retry policy is invalid.");
        if (!token.IsValid)
            throw new ArgumentException("Apply token is invalid.", nameof(token));
        if (attempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempt));

        var exponent = Math.Min(attempt - 1, 30);
        var unjitteredTicks = Math.Min(
            MaximumDelay.Ticks,
            InitialDelay.Ticks * Math.Pow(2, exponent));
        if (JitterRatio == 0)
            return TimeSpan.FromTicks((long)unjitteredTicks);

        var bytes = token.ObjectId.Value.ToByteArray();
        uint hash = 2166136261;
        foreach (var value in bytes)
            hash = (hash ^ value) * 16777619;
        hash = (hash ^ (uint)attempt) * 16777619;
        var unit = (hash % 1001) / 1000d;
        var factor = 1d - JitterRatio + (2d * JitterRatio * unit);
        var jitteredTicks = Math.Clamp(
            unjitteredTicks * factor,
            1d,
            MaximumDelay.Ticks);
        return TimeSpan.FromTicks((long)jitteredTicks);
    }
}

public readonly record struct LocalObjectApplyRetryEntry(
    LocalObjectApplyToken Token,
    int Attempt,
    DateTimeOffset DueAt)
{
    public bool Promoted { get; init; }
}

public enum LocalObjectApplyRetryScheduleStatus
{
    None,
    Scheduled,
    CapacityExceeded,
    Exhausted,
    Invalid,
    Stale,
}

public readonly record struct LocalObjectApplyRetryScheduleResult(
    LocalObjectApplyRetryScheduleStatus Status,
    LocalObjectApplyRetryEntry Entry)
{
    public bool Scheduled => Status == LocalObjectApplyRetryScheduleStatus.Scheduled;
}

/// <summary>
/// Bounded delayed retry state with one entry per session object and no
/// per-object task or timer. The shared receiver pump promotes due entries.
/// </summary>
public sealed class LocalObjectApplyRetryQueue
{
    private readonly record struct RetryKey(
        ReceiverSessionKey Session,
        RscObjectId ObjectId);

    private readonly object gate = new();
    private readonly int maximumObjects;
    private readonly LocalObjectApplyRetryPolicy policy;
    private readonly Dictionary<RetryKey, LocalObjectApplyRetryEntry> entries = new();
    private readonly Dictionary<ReceiverSessionKey, HashSet<RetryKey>> sessionEntries = new();

    public LocalObjectApplyRetryQueue(
        int maximumObjects,
        LocalObjectApplyRetryPolicy? policy = null)
    {
        if (maximumObjects <= 0 || maximumObjects > ProtocolLimits.MaximumObjectsPerSnapshot)
            throw new ArgumentOutOfRangeException(nameof(maximumObjects));

        this.policy = policy ?? LocalObjectApplyRetryPolicy.Default;
        if (!this.policy.IsValid)
            throw new ArgumentException("Retry policy is invalid.", nameof(policy));
        this.maximumObjects = maximumObjects;
    }

    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    public LocalObjectApplyRetryScheduleResult TrySchedule(
        LocalObjectApplyToken token,
        DateTimeOffset now)
    {
        if (!token.IsValid)
            return new(LocalObjectApplyRetryScheduleStatus.Invalid, default);

        var key = new RetryKey(token.Session, token.ObjectId);
        lock (gate)
        {
            var attempt = 1;
            if (entries.TryGetValue(key, out var existing))
            {
                if (existing.Token == token)
                    attempt = existing.Attempt + 1;
                else if (existing.Token.Generation.Value > token.Generation.Value)
                {
                    return new(LocalObjectApplyRetryScheduleStatus.Stale, existing);
                }
            }
            else if (entries.Count >= maximumObjects)
            {
                return new(LocalObjectApplyRetryScheduleStatus.CapacityExceeded, default);
            }

            if (attempt > policy.MaximumAttempts)
            {
                RemoveUnsafe(key);
                return new(
                    LocalObjectApplyRetryScheduleStatus.Exhausted,
                    new LocalObjectApplyRetryEntry(token, attempt, now));
            }

            var entry = new LocalObjectApplyRetryEntry(
                token,
                attempt,
                SaturatingAdd(now, policy.GetDelay(token, attempt)));
            entries[key] = entry;
            AddSessionEntryUnsafe(key);
            return new(LocalObjectApplyRetryScheduleStatus.Scheduled, entry);
        }
    }

    public IReadOnlyList<LocalObjectApplyRetryEntry> GetDueBatch(
        DateTimeOffset now,
        int maximumEntries)
    {
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        lock (gate)
        {
            return entries.Values
                .Where(entry => !entry.Promoted && entry.DueAt <= now)
                .OrderBy(entry => entry.DueAt)
                .Take(maximumEntries)
                .ToArray();
        }
    }

    public bool TryRemove(LocalObjectApplyToken token)
    {
        if (!token.IsValid)
            return false;

        var key = new RetryKey(token.Session, token.ObjectId);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry) || entry.Token != token)
                return false;

            RemoveUnsafe(key);
            return true;
        }
    }

    public bool TryRemoveSuperseded(LocalObjectApplyToken current)
    {
        if (!current.IsValid)
            return false;

        var key = new RetryKey(current.Session, current.ObjectId);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry) ||
                entry.Token.Generation.Value >= current.Generation.Value)
            {
                return false;
            }

            RemoveUnsafe(key);
            return true;
        }
    }

    public bool TryMarkPromoted(LocalObjectApplyToken token)
    {
        if (!token.IsValid)
            return false;

        var key = new RetryKey(token.Session, token.ObjectId);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry) || entry.Token != token)
                return false;

            entries[key] = entry with { Promoted = true };
            return true;
        }
    }

    public int CancelSession(ReceiverSessionKey session, int maximumEntries)
    {
        if (!session.IsValid)
            return 0;
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        lock (gate)
        {
            if (!sessionEntries.TryGetValue(session, out var keys))
                return 0;

            var selected = keys.Take(maximumEntries).ToArray();
            foreach (var key in selected)
                RemoveUnsafe(key);

            return selected.Length;
        }
    }

    public bool HasSessionEntries(ReceiverSessionKey session)
    {
        lock (gate)
            return sessionEntries.TryGetValue(session, out var keys) && keys.Count > 0;
    }

    private void AddSessionEntryUnsafe(RetryKey key)
    {
        if (!sessionEntries.TryGetValue(key.Session, out var keys))
        {
            keys = new HashSet<RetryKey>();
            sessionEntries.Add(key.Session, keys);
        }

        keys.Add(key);
    }

    private void RemoveUnsafe(RetryKey key)
    {
        if (!entries.Remove(key))
            return;
        if (!sessionEntries.TryGetValue(key.Session, out var keys))
            return;

        keys.Remove(key);
        if (keys.Count == 0)
            sessionEntries.Remove(key.Session);
    }

    private static DateTimeOffset SaturatingAdd(
        DateTimeOffset value,
        TimeSpan delay)
    {
        var maximumTicks = DateTimeOffset.MaxValue.UtcTicks;
        var currentTicks = value.UtcTicks;
        var dueTicks = delay.Ticks >= maximumTicks - currentTicks
            ? maximumTicks
            : currentTicks + delay.Ticks;
        return new DateTimeOffset(dueTicks, TimeSpan.Zero);
    }
}
