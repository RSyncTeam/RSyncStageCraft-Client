using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Core.Furniture;

/// <summary>How much of the furniture and lights at the player's place is drawn.</summary>
public readonly record struct SceneObjectLoadProgress(int Drawn, int Total)
{
    /// <summary>Something here is still being drawn.</summary>
    public bool IsLoading => Total > 0 && Drawn < Total;

    public string Describe() => $"Loading scene objects: {Drawn}/{Total} drawn";

    /// <summary>
    /// Counts the furniture and lights wanted at <paramref name="here"/> and
    /// how many are drawn. <paramref name="excluded"/> are the objects this
    /// client will not draw: permanently unsupported ones and lights whose
    /// creation retries are exhausted. A light still being retried is not
    /// excluded: it stays pending. Nothing is counted while the place is
    /// unknown.
    /// </summary>
    public static SceneObjectLoadProgress Compute(
        IEnumerable<AuthoritativeObjectState> desired,
        SceneLocation? here,
        Func<RscObjectId, bool> isDrawn,
        IReadOnlyCollection<RscObjectId> excluded)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(isDrawn);
        ArgumentNullException.ThrowIfNull(excluded);
        if (here is not { } place)
            return default;
        var skip = excluded as ISet<RscObjectId> ?? excluded.ToHashSet();
        int total = 0, drawn = 0;
        foreach (var state in desired)
        {
            if (state is not (ActorState or FurnitureState or LightState) || state.Location != place ||
                skip.Contains(state.ObjectId))
                continue;
            total++;
            if (isDrawn(state.ObjectId))
                drawn++;
        }
        return new(drawn, total);
    }
}

public static class SceneObjectReports
{
    /// <summary>The objects excluded from loading progress: permanently unsupported, or failed after all retries.</summary>
    public static IReadOnlyCollection<RscObjectId> Excluded(IEnumerable<FurnitureUnsupportedReport> reports) =>
        reports.Where(report => report.Kind != LocalObjectReportKind.Retrying)
            .Select(report => report.ObjectId)
            .ToHashSet();
}
