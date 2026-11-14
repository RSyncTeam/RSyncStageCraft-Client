using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Core.Actors;

/// <summary>
/// Protocol 7: the human apply domain's host. It is given the desired shared
/// human actors on every framework update (game thread) and converges on
/// them by itself: admission, download, holds, spawn, placement, moves and
/// cleanup are its own. A Release build binds
/// <see cref="UnavailableHumanActorHost"/>, which draws nothing; only a Debug
/// build's plugin has a drawing implementation.
/// </summary>
public interface IHumanActorHostPort
{
    /// <summary>Game thread, every framework update.</summary>
    void Tick(HumanActorDesiredView desired);

    /// <summary>Human actors this client draws now (any phase).</summary>
    int LiveCount { get; }

    /// <summary>Unload, after the last tick: everything this host created is cleaned up within the caller's bound.</summary>
    void Shutdown(Func<TimeSpan> remaining);
}

/// <summary>
/// The Release human host: it never draws a human actor. Each desired human
/// is reported once, by its object's short id, as not drawn because human
/// actors need a development build; nothing else is done, and nothing is
/// sent to the server.
/// </summary>
public sealed class UnavailableHumanActorHost : IHumanActorHostPort
{
    public const string NotDrawn = "human actors need a development build";

    private readonly Action<string> log;
    private readonly HashSet<RscObjectId> reported = [];
    private long seenVersion = -1;

    public UnavailableHumanActorHost(Action<string> log)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public int LiveCount => 0;

    /// <summary>The humans reported as not drawn and still desired.</summary>
    public int NotDrawnCount => reported.Count;

    public void Tick(HumanActorDesiredView desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (desired.Version == seenVersion)
            return;
        seenVersion = desired.Version;
        var wanted = desired.Humans.Select(human => human.ObjectId).ToHashSet();
        reported.RemoveWhere(id => !wanted.Contains(id));
        foreach (var human in desired.Humans)
        {
            if (reported.Add(human.ObjectId))
                log($"RSC: shared human actor {human.ObjectId.Value.ToString("N")[..8]} is not drawn: {NotDrawn}.");
        }
    }

    public void Shutdown(Func<TimeSpan> remaining) => reported.Clear();
}
