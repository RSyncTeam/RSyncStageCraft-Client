using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class LocalObjectApplyRetryQueueTests
{
    private static readonly LocalObjectApplyRetryPolicy Policy = new(
        MaximumAttempts: 2,
        InitialDelay: TimeSpan.FromSeconds(1),
        MaximumDelay: TimeSpan.FromSeconds(4),
        JitterRatio: 0);

    [Fact]
    public void DefaultRetryResultDoesNotReportScheduledWork()
    {
        Assert.False(default(LocalObjectApplyRetryScheduleResult).Scheduled);
        Assert.Equal(
            LocalObjectApplyRetryScheduleStatus.None,
            default(LocalObjectApplyRetryScheduleResult).Status);
    }

    [Fact]
    public void UnrepresentableExhaustionAttemptIsRejectedByPolicy()
    {
        var policy = Policy with { MaximumAttempts = int.MaxValue };

        Assert.False(policy.IsValid);
        Assert.Throws<ArgumentException>(() => new LocalObjectApplyRetryQueue(1, policy));
    }

    [Fact]
    public void MaximumDelaySaturatesWithoutConvertingToNegativeTicks()
    {
        var policy = new LocalObjectApplyRetryPolicy(
            MaximumAttempts: 1,
            InitialDelay: TimeSpan.MaxValue,
            MaximumDelay: TimeSpan.MaxValue,
            JitterRatio: 0);
        var token = Token(new ReceiverSessionKey(1, 1, 1));

        Assert.True(policy.IsValid);
        Assert.Equal(TimeSpan.MaxValue, policy.GetDelay(token, attempt: 1));
        var jitteredToken = new LocalObjectApplyToken(
            token.Session,
            new RscObjectId(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")),
            token.Generation);
        Assert.Equal(
            TimeSpan.MaxValue,
            (policy with { JitterRatio = 0.5 }).GetDelay(jitteredToken, attempt: 1));

        var scheduled = new LocalObjectApplyRetryQueue(1, policy)
            .TrySchedule(token, DateTimeOffset.UnixEpoch);

        Assert.Equal(LocalObjectApplyRetryScheduleStatus.Scheduled, scheduled.Status);
        Assert.Equal(DateTimeOffset.MaxValue, scheduled.Entry.DueAt);
    }

    [Fact]
    public void OneRecordPerObjectAdvancesAttemptWithoutGrowingCapacity()
    {
        var retries = new LocalObjectApplyRetryQueue(1, Policy);
        var now = DateTimeOffset.UtcNow;
        var token = Token(new ReceiverSessionKey(1, 1, 1));

        var first = retries.TrySchedule(token, now);
        var second = retries.TrySchedule(token, now);
        var overflow = retries.TrySchedule(
            Token(new ReceiverSessionKey(2, 1, 2)),
            now);

        Assert.Equal(LocalObjectApplyRetryScheduleStatus.Scheduled, first.Status);
        Assert.Equal(1, first.Entry.Attempt);
        Assert.Equal(now + TimeSpan.FromSeconds(1), first.Entry.DueAt);
        Assert.Equal(LocalObjectApplyRetryScheduleStatus.Scheduled, second.Status);
        Assert.Equal(2, second.Entry.Attempt);
        Assert.Equal(now + TimeSpan.FromSeconds(2), second.Entry.DueAt);
        Assert.Equal(LocalObjectApplyRetryScheduleStatus.CapacityExceeded, overflow.Status);
        Assert.Equal(1, retries.Count);
    }

    [Fact]
    public void ExhaustionRemovesDelayedRecordInsteadOfRetainingDeadState()
    {
        var retries = new LocalObjectApplyRetryQueue(1, Policy);
        var token = Token(new ReceiverSessionKey(1, 1, 1));
        var now = DateTimeOffset.UtcNow;
        Assert.True(retries.TrySchedule(token, now).Scheduled);
        Assert.True(retries.TrySchedule(token, now).Scheduled);

        var exhausted = retries.TrySchedule(token, now);

        Assert.Equal(LocalObjectApplyRetryScheduleStatus.Exhausted, exhausted.Status);
        Assert.Equal(3, exhausted.Entry.Attempt);
        Assert.Equal(0, retries.Count);
    }

    [Fact]
    public void SupersedingGenerationResetsAttemptsAndRejectsStaleScheduleAndRemoval()
    {
        var retries = new LocalObjectApplyRetryQueue(1, Policy);
        var session = new ReceiverSessionKey(1, 1, 1);
        var objectId = new RscObjectId(Guid.NewGuid());
        var oldToken = new LocalObjectApplyToken(session, objectId, new ApplyGeneration(1));
        var newToken = new LocalObjectApplyToken(session, objectId, new ApplyGeneration(2));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, retries.TrySchedule(oldToken, now).Entry.Attempt);
        Assert.Equal(2, retries.TrySchedule(oldToken, now).Entry.Attempt);

        var replacement = retries.TrySchedule(newToken, now);
        Assert.True(retries.TryMarkPromoted(newToken));

        var stale = retries.TrySchedule(oldToken, now);

        Assert.Equal(1, replacement.Entry.Attempt);
        Assert.Equal(LocalObjectApplyRetryScheduleStatus.Stale, stale.Status);
        Assert.Equal(replacement.Entry with { Promoted = true }, stale.Entry);
        Assert.Empty(retries.GetDueBatch(DateTimeOffset.MaxValue, 1));
        Assert.False(retries.TryRemoveSuperseded(oldToken));
        Assert.False(retries.TryRemove(oldToken));
        Assert.True(retries.TryRemove(newToken));
        Assert.Equal(0, retries.Count);
    }

    [Fact]
    public void DueBatchPromotionAndSessionCancellationAreBoundedAndExact()
    {
        var retries = new LocalObjectApplyRetryQueue(4, Policy);
        var now = DateTimeOffset.UtcNow;
        var oldSession = new ReceiverSessionKey(1, 1, 1);
        var newSession = new ReceiverSessionKey(2, 1, 2);
        var first = retries.TrySchedule(Token(oldSession), now).Entry;
        retries.TrySchedule(Token(oldSession), now);
        var current = retries.TrySchedule(Token(newSession), now).Entry;

        Assert.Empty(retries.GetDueBatch(now + TimeSpan.FromMilliseconds(999), 4));
        Assert.Equal(3, retries.GetDueBatch(now + TimeSpan.FromSeconds(1), 4).Count);
        Assert.True(retries.TryMarkPromoted(first.Token));
        Assert.Equal(2, retries.GetDueBatch(now + TimeSpan.FromSeconds(1), 4).Count);
        Assert.Equal(1, retries.CancelSession(oldSession, 1));
        Assert.True(retries.HasSessionEntries(oldSession));
        Assert.Equal(1, retries.CancelSession(oldSession, 1));
        Assert.False(retries.HasSessionEntries(oldSession));
        Assert.True(retries.TryRemove(current.Token));
        Assert.Equal(0, retries.Count);
    }

    [Fact]
    public void SaturatedDueTimeDoesNotOverflow()
    {
        var retries = new LocalObjectApplyRetryQueue(1, Policy);
        var scheduled = retries.TrySchedule(
            Token(new ReceiverSessionKey(1, 1, 1)),
            DateTimeOffset.MaxValue);

        Assert.True(scheduled.Scheduled);
        Assert.Equal(DateTimeOffset.MaxValue, scheduled.Entry.DueAt);
    }

    private static LocalObjectApplyToken Token(ReceiverSessionKey session) => new(
        session,
        new RscObjectId(Guid.NewGuid()),
        new ApplyGeneration(1));
}
