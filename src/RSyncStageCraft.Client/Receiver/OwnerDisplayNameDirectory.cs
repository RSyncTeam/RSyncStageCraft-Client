using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Receiver;

/// <summary>
/// The display names the server attached to actor messages (protocol 6),
/// keyed by stable owner id, for the UI only. Nothing here decides ownership
/// or editing rights: those compare stable ids (<see cref="ActorOwnership"/>),
/// and the server enforces them regardless. A message that names no owner
/// clears that owner's entry (the account has no display name now). Bounded.
/// </summary>
public sealed class OwnerDisplayNameDirectory
{
    public const int MaximumEntries = 512;
    private readonly object gate = new();
    private readonly Dictionary<RscUserId, string> names = new();
    private readonly Queue<RscUserId> order = new();

    public string? NameOf(RscUserId owner)
    {
        lock (gate)
            return names.TryGetValue(owner, out var name) ? name : null;
    }

    public int Count
    {
        get
        {
            lock (gate)
                return names.Count;
        }
    }

    /// <summary>Records what a received, already validated message says about its actors' owners.</summary>
    internal void Observe(object payload)
    {
        switch (payload)
        {
            case ObjectCreatedMessage { State: ActorState actor } created:
                Set(actor.OwnerId, created.OwnerName?.DisplayName);
                break;
            case ObjectUpdatedMessage { State: ActorState actor } updated:
                Set(actor.OwnerId, updated.OwnerName?.DisplayName);
                break;
            case ObjectSnapshotMessage snapshot:
                ObserveSnapshot(snapshot);
                break;
            case ObjectSnapshotCompletion completion:
                ObserveSnapshot(completion.Snapshot);
                break;
        }
    }

    private void ObserveSnapshot(ObjectSnapshotMessage snapshot)
    {
        var listed = (snapshot.OwnerNames ?? []).ToDictionary(name => name.UserId, name => name.DisplayName);
        foreach (var owner in snapshot.Objects.OfType<ActorState>().Select(actor => actor.OwnerId).Distinct())
            Set(owner, listed.TryGetValue(owner, out var name) ? name : null);
    }

    private void Set(RscUserId owner, string? name)
    {
        lock (gate)
        {
            if (name is null || !RscDisplayName.IsValid(name))
            {
                names.Remove(owner);
                return;
            }
            if (!names.ContainsKey(owner))
            {
                while (names.Count >= MaximumEntries && order.TryDequeue(out var oldest))
                    names.Remove(oldest);
                order.Enqueue(owner);
            }
            names[owner] = name;
        }
    }
}

/// <summary>
/// How the UI shows an actor's owner. Editable strictly when the actor's
/// stable owner id equals the signed-in user's stable id; a display name
/// (which may repeat, change or be missing) is only the label.
/// </summary>
public static class ActorOwnership
{
    public static bool IsEditableBy(ActorState actor, RscUserId? self) =>
        self is { } me && me.IsValid && actor.OwnerId == me;

    /// <summary>The owner's label: its display name if known, otherwise its stable id.</summary>
    public static string OwnerLabel(ActorState actor, Func<RscUserId, string?> nameOf) =>
        nameOf(actor.OwnerId) ?? actor.OwnerId.Value;
}
