using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Tests;

public sealed class ReceiverStateCapacityTests
{

    [Fact]
    public void LocalObjectStatePrimitivesRejectCapacityBeyondProtocolCeiling()
    {
        var authority = new ReceiverApplyAuthority();
        var store = new DesiredLocalObjectStore(
            authority,
            ProtocolLimits.MaximumObjectsPerSnapshot);
        var oversized = ProtocolLimits.MaximumObjectsPerSnapshot + 1;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesiredLocalObjectStore(authority, oversized));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LocalObjectApplyCoordinator(store, oversized));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LocalObjectApplyRetryQueue(oversized));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LocalObjectOwnershipLedger(oversized));
    }
}
