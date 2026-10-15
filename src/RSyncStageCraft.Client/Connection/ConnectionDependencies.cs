using RSyncStageCraft.Core.Dependencies;
using RSyncStageCraft.Core.Receiver;

namespace RSyncStageCraft.Core.Connection;

public static class ConnectionDependencies
{
    /// <summary>
    /// The plugin's readiness, in one place so the end-to-end tests exercise
    /// the production composition:
    /// <list type="bullet">
    /// <item>The local-object domain (furniture, lights and protocol 6
    /// base-game actors) is ready exactly while the normalized scene location
    /// is known.</item>
    /// <item>Penumbra and Glamourer gate neither; they are reported only.</item>
    /// </list>
    /// Native actor availability is checked per spawn by the actor host, which
    /// lists an actor it cannot create instead of closing a domain.
    /// </summary>
    public static ReceiverDependencySnapshot ForCurrentPlace(IEnumerable<DependencyHealth> health, bool sceneKnown) =>
        FromHealth(health, localObjectHostReady: sceneKnown);

    /// <summary>
    /// Maps observed dependency health and the game-side hosts to the
    /// readiness the receiver uses. The plugin may connect and hold server
    /// state without the host; local objects apply only while it is ready.
    /// </summary>
    public static ReceiverDependencySnapshot FromHealth(
        IEnumerable<DependencyHealth> health,
        bool localObjectHostReady = false)
    {
        ArgumentNullException.ThrowIfNull(health);
        var observed = health.ToArray();
        bool Ready(string name) => observed.Any(dependency =>
            string.Equals(dependency.Name, name, StringComparison.Ordinal) &&
            dependency.Available && dependency.Compatible);

        var penumbra = Ready("Penumbra");
        var glamourer = Ready("Glamourer");
        return new(
            PenumbraReady: penumbra,
            GlamourerReady: glamourer,
            IpcReady: penumbra && glamourer,
            ProtocolCompatible: true,
            LocalObjectHostReady: localObjectHostReady);
    }
}
