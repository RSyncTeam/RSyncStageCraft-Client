using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Actors;

/// <summary>
/// Location visibility, with actors hidden while <paramref name="suppressed"/>
/// is true (GPose or a cutscene: treated like leaving the place, for actors
/// only). Furniture and lights keep the inner visibility unchanged.
/// </summary>
public sealed class ActorSuppressingVisibility(ILocalObjectVisibility inner, Func<bool> suppressed) : ILocalObjectVisibility
{
    public bool IsVisibleHere(AuthoritativeObjectState state)
    {
        if (!inner.IsVisibleHere(state))
            return false;
        if (state is not ActorState)
            return true;
        try
        {
            return !suppressed();
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// One observation of the conditions that hide RSC actors on this client:
/// GPose and the game's cutscene conditions. A failed read hides them.
/// </summary>
public readonly record struct ActorSuppression(
    bool GPosing,
    bool OccupiedInCutSceneEvent,
    bool WatchingCutscene,
    bool WatchingCutscene78,
    string? ReadFailure = null)
{
    public static ActorSuppression None => default;

    public static ActorSuppression Failed(string reason) => new(false, false, false, false, reason);

    public bool Suppressed => GPosing || OccupiedInCutSceneEvent || WatchingCutscene || WatchingCutscene78 || ReadFailure is not null;

    public string Describe() => !Suppressed
        ? "no"
        : ReadFailure is { } failure
            ? $"yes (the conditions could not be read: {failure})"
            : "yes (" + string.Join(", ", new[]
            {
                GPosing ? "GPose" : null,
                OccupiedInCutSceneEvent ? "OccupiedInCutSceneEvent" : null,
                WatchingCutscene ? "WatchingCutscene" : null,
                WatchingCutscene78 ? "WatchingCutscene78" : null,
            }.Where(flag => flag is not null)) + ")";
}
