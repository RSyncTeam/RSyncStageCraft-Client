using RSyncStageCraft.Core.Diagnostics;

namespace RSyncStageCraft.Ui;

/// <summary>
/// Converts Dalamud plugin-list notifications into one deferred UI-thread
/// refresh. The callback itself never invokes a dependency provider.
/// </summary>
internal sealed class DependencyChangeRefreshGate
{
    internal const int MaximumAffectedNames = 64;
    private readonly RuntimeObservationTrace observations;
    private int pending;

    internal DependencyChangeRefreshGate(RuntimeObservationTrace observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        this.observations = observations;
    }

    internal void Observe(string kind, IEnumerable<string> affectedNames)
    {
        ArgumentNullException.ThrowIfNull(affectedNames);

        try
        {
            using var names = affectedNames.GetEnumerator();
            for (var inspected = 0; inspected < MaximumAffectedNames; inspected++)
            {
                if (!names.MoveNext())
                    return;

                if (IsObservedPlugin(names.Current))
                {
                    Request(kind, names.Current);
                    return;
                }
            }

            if (names.MoveNext())
                Request(kind, "affected-list-truncated");
        }
        catch
        {
            Request(kind, "affected-list-unreadable");
        }
    }

    internal void ObserveUnreadable() =>
        Request("unreadable", "event-arguments-unreadable");

    internal bool TryConsume() =>
        Interlocked.Exchange(ref pending, 0) != 0;

    private void Request(string kind, string detail)
    {
        Interlocked.Exchange(ref pending, 1);
        observations.RecordCallback(
            "dependency.change-observed",
            $"{Bound(kind)}/{Bound(detail)}");
    }

    private static bool IsObservedPlugin(string? name) =>
        string.Equals(name, "Penumbra", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Glamourer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "CustomizePlus", StringComparison.OrdinalIgnoreCase);

    private static string Bound(string? value)
    {
        const int maximumLength = 96;
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        return value.Length <= maximumLength
            ? value
            : value[..maximumLength];
    }
}
