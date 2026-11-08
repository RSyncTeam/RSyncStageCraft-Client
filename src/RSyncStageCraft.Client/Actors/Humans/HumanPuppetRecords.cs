#if RSC_DEVELOPMENT
namespace RSyncStageCraft.Core.Actors.Humans;

/// <summary>Human puppets must stay at global indices 200–439: both parent maps cover only those slots.</summary>
public static class HumanPuppetSlotRange
{
    public const int MinimumGlobalIndex = ActorSlotRange.MinimumGlobalIndex;
    public const int MaximumGlobalIndex = 439;
    public const int Count = MaximumGlobalIndex - MinimumGlobalIndex + 1;

    public static bool Contains(int globalIndex) => globalIndex is >= MinimumGlobalIndex and <= MaximumGlobalIndex;
}

/// <summary>An opaque key for one Debug human puppet; never an index, address or name.</summary>
public readonly record struct HumanPuppetHandle(Guid Value)
{
    public override string ToString() => $"puppet {Value:N}";
}

/// <summary>Why the host refused before any side effect. A refusal is not a stop.</summary>
public enum HumanPuppetRefusal
{
    None,
    Stopped,
    Unloading,
    Unavailable,
    CustomizePlusNotChecked,
    WorldUnverified,
    AtCapacity,
    UnknownPuppet,
    NotHidden,
    AlreadyAssigned,
    CustomizeNotFilled,

    /// <summary>H1-c: GPose, a cutscene, a loading screen, no player, another RSC actor or a server session.</summary>
    SceneNotQuiet,
}

/// <summary>The stop rule that latched. Nothing is retried after a stop.</summary>
public enum HumanPuppetStopRule
{
    /// <summary>Creation, the slot or ownership checks 1–5 failed.</summary>
    Ownership,

    /// <summary>S4: a map set, read-back or reading failed, or an isolation check failed.</summary>
    S4ParentMapOrIsolation,

    /// <summary>S5: the name, world, kind, name ID or owner is not this spawn's (P5-6, P5-7).</summary>
    S5Identity,

    /// <summary>S6: a receipt was lost or something was left behind.</summary>
    S6Residue,

    /// <summary>H-10: another object carries the puppet's identity, or an exact Penumbra entry exists.</summary>
    Collision,

    /// <summary>CP-3 failed: Customize+ stays off for humans.</summary>
    CustomizePlus,

    /// <summary>A provider stopped answering, was reloaded (H1-b), or a port call threw (H1-d).</summary>
    Provider,

    /// <summary>H1-c: the scene stopped being quiet while a puppet was live.</summary>
    LiveExit,

    /// <summary>H1-a and H1-e: the puppet was targetable.</summary>
    Hiding,
}

public sealed record HumanPuppetStopRecord(HumanPuppetStopRule Rule, string Step, string Detail, long Update);

/// <summary>One reading of both maps for a puppet's slot. Null: the provider did not answer.</summary>
public sealed record HumanParentMapReading(long Update, string Phase, int GlobalIndex, int? Penumbra, int? CustomizePlus)
{
    /// <summary>Both are −1 or the slot itself (r5 §8.1 item 3).</summary>
    public bool Resolved => IsSelfOrDetached(Penumbra, GlobalIndex) && IsSelfOrDetached(CustomizePlus, GlobalIndex);

    internal static bool IsSelfOrDetached(int? value, int globalIndex) => value is { } parent && (parent == -1 || parent == globalIndex);
}

/// <summary>What the puppet's fields were after a body copy (F0's record), without its name.</summary>
public sealed record HumanPuppetCopyRecord(string Copy, int RawKind, ushort HomeWorld, uint NameId, ulong OwnerId, bool HasHumanModel, bool CustomizeFilled, bool Targetable);

/// <summary>Something RSC made that is still there, recorded rather than resolved.</summary>
public sealed record HumanPuppetResidue(HumanPuppetHandle Handle, string What, string Reason);

/// <summary>M-2's baseline: both maps for every slot 200–439, plus the read-only provider facts.</summary>
public sealed record HumanProviderBaseline(
    long Update,
    int?[] Penumbra,
    int?[] CustomizePlus,
    (int Major, int Minor)? CustomizePlusApiVersion,
    (int Major, int Minor)? GlamourerApiVersion,
    (bool IsLocked, bool CanUnlock)? GlamourerCanUnlockBaseline)
{
    /// <summary>The global indices whose entry differs between the two baselines, apart from <paramref name="ignored"/>.</summary>
    public IReadOnlyList<int> ChangedSlots(HumanProviderBaseline other, IReadOnlySet<int>? ignored = null)
    {
        ArgumentNullException.ThrowIfNull(other);
        var changed = new List<int>();
        for (var i = 0; i < HumanPuppetSlotRange.Count; i++)
        {
            var index = HumanPuppetSlotRange.MinimumGlobalIndex + i;
            if (ignored?.Contains(index) == true)
                continue;
            if (Penumbra[i] != other.Penumbra[i] || CustomizePlus[i] != other.CustomizePlus[i])
                changed.Add(index);
        }
        return changed;
    }
}

public enum HumanPuppetPhase
{
    /// <summary>Hidden, never asked to draw.</summary>
    Hidden,

    /// <summary>Draw requested, still at alpha 0.</summary>
    DrawRequested,

    /// <summary>Clean-up has begun; deletion waits for D2.</summary>
    AwaitingDeletion,

    /// <summary>Deleted.</summary>
    Deleted,

    /// <summary>Left as residue: ownership was lost, or deletion was not allowed (D2 inside an unload).</summary>
    Residue,
}

/// <summary>A puppet's state, without its name, world or address.</summary>
public sealed record HumanPuppetSnapshot(
    HumanPuppetHandle Handle,
    HumanPuppetPhase Phase,
    int GlobalIndex,
    HumanPuppetKind Kind,
    bool Assigned,
    long? LatestChangeUpdate,
    long? DrawRequestedUpdate,
    long? DrawObservedUpdate,
    long? DeletedUpdate);

public sealed record HumanPuppetSpawnResult(
    HumanPuppetHandle? Handle,
    HumanPuppetRefusal Refusal,
    HumanPuppetStopRecord? Stop,
    IReadOnlyList<HumanParentMapReading> Readings,
    IReadOnlyList<HumanPuppetCopyRecord> Copies)
{
    public bool Spawned => Handle is not null;

    /// <summary>
    /// R-8a: the kind and sub-kind as created, as the copies left them, and
    /// as read back after the one restore; null when no restore was written.
    /// </summary>
    public HumanPuppetKindRestore? KindRestore { get; init; }
}

/// <summary>R-8a's one restore: the pair as created, after the copies, and as read back after the write (null if unreadable).</summary>
public sealed record HumanPuppetKindRestore(HumanPuppetKindPair Created, HumanPuppetKindPair AfterCopies, HumanPuppetKindPair? ReadBack)
{
    /// <summary>Both bytes read back equal to the created pair.</summary>
    public bool Restored => ReadBack == Created;
}

/// <summary>An operation's outcome: done, refused before any side effect, or stopped.</summary>
public sealed record HumanPuppetOutcome(bool Done, HumanPuppetRefusal Refusal, HumanPuppetStopRecord? Stop)
{
    internal static readonly HumanPuppetOutcome Ok = new(true, HumanPuppetRefusal.None, null);

    internal static HumanPuppetOutcome Refused(HumanPuppetRefusal refusal) => new(false, refusal, null);

    internal static HumanPuppetOutcome Stopped(HumanPuppetStopRecord stop) => new(false, HumanPuppetRefusal.None, stop);
}

/// <summary>What an unload did. A puppet left inside D2 is residue, never a pass.</summary>
public sealed record HumanPuppetUnloadReport(
    int Deleted,
    IReadOnlyList<HumanPuppetResidue> Residue,
    int DrainedWork,
    IReadOnlyList<string> DependencyFailures,
    IReadOnlyList<string> Order);
#endif
