#nullable enable
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

/// <summary>Kind-scoped local-object resets (GPose/cutscene redraw actors only).</summary>
public sealed class ApplyDomainGateScopeTests
{
    [Fact]
    public void AScopedResetReleasesOnlyItsKindAndAWiderResetSupersedesIt()
    {
        var gate = new ApplyDomainGate();
        gate.RequestReset(RscObjectKind.Actor);
        var actorsOnly = gate.CleaningEpoch(out var releases);
        Assert.NotNull(releases);
        Assert.True(releases!(RscObjectKind.Actor));
        Assert.False(releases(RscObjectKind.Furniture));

        // A location change while cleaning widens the reset to every kind,
        // under a newer epoch: the actors-only pass cannot complete it.
        gate.RequestReset(null);
        var everything = gate.CleaningEpoch(out var wider);
        Assert.Null(wider);
        Assert.NotEqual(actorsOnly, everything);
        Assert.False(gate.TryCompleteCleaning(actorsOnly, []));
        Assert.True(gate.TryCompleteCleaning(everything, []));
        Assert.Equal(ApplyDomainPhase.Open, gate.Phase);
    }

    [Fact]
    public void ScopedResetsRequestedWhileCleaningAccumulateTheirKinds()
    {
        var gate = new ApplyDomainGate();
        gate.RequestReset(RscObjectKind.Actor);
        gate.RequestReset(RscObjectKind.Light);
        gate.CleaningEpoch(out var releases);
        Assert.True(releases!(RscObjectKind.Actor));
        Assert.True(releases(RscObjectKind.Light));
        Assert.False(releases(RscObjectKind.Furniture));

        // After it completes, a new scoped reset starts from its own kind only.
        Assert.True(gate.TryCompleteCleaning(gate.CleaningEpoch(), []));
        gate.RequestReset(RscObjectKind.Furniture);
        gate.CleaningEpoch(out releases);
        Assert.False(releases!(RscObjectKind.Actor));
        Assert.True(releases(RscObjectKind.Furniture));
    }
}
