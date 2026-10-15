using RSyncStageCraft.Core.Diagnostics;

namespace RSyncStageCraft.Tests;

public sealed class RuntimeObservationTraceTests
{
    [Fact]
    public void NestedExternalCallsExposeDepthAndReentrancy()
    {
        var trace = new RuntimeObservationTrace();

        using (trace.BeginExternalCall("outer", "Penumbra"))
        {
            trace.Record("provider.callback");
            using (trace.BeginExternalCall("inner", "Glamourer"))
                trace.Record("provider.nested-callback");
        }

        var observations = trace.Snapshot();
        Assert.Collection(
            observations,
            current => AssertObservation(current, 1, "outer.begin", 1, false),
            current => AssertObservation(current, 2, "provider.callback", 1, false),
            current => AssertObservation(current, 3, "inner.begin", 2, true),
            current => AssertObservation(current, 4, "provider.nested-callback", 2, true),
            current => AssertObservation(current, 5, "inner.end", 2, true),
            current => AssertObservation(current, 6, "outer.end", 1, false));
    }

    [Fact]
    public void TraceRetainsOnlyNewestBoundedMonotonicEntries()
    {
        var trace = new RuntimeObservationTrace();

        for (var index = 0; index < RuntimeObservationTrace.MaximumEntries + 10; index++)
            trace.Record("sample", new string('x', 512));

        var observations = trace.Snapshot();
        Assert.Equal(RuntimeObservationTrace.MaximumEntries, observations.Count);
        Assert.Equal(11, observations[0].Sequence);
        Assert.Equal(
            RuntimeObservationTrace.MaximumEntries + 10,
            observations[^1].Sequence);
        Assert.All(observations, current =>
        {
            Assert.Equal(trace.RunId, current.RunId);
            Assert.NotNull(current.Detail);
            Assert.True(current.Detail.Length <= 256);
        });
    }

    [Fact]
    public void DiagnosticPublisherFailureCannotEscapeRecording()
    {
        var trace = new RuntimeObservationTrace(
            _ => throw new InvalidOperationException("logger failed"));

        var observation = trace.Record("bootstrap.start");

        Assert.Equal(observation, Assert.Single(trace.Snapshot()));
    }

    private static void AssertObservation(
        RuntimeObservation observation,
        long sequence,
        string phase,
        int depth,
        bool reentrant)
    {
        Assert.Equal(sequence, observation.Sequence);
        Assert.Equal(phase, observation.Phase);
        Assert.Equal(depth, observation.ExternalCallDepth);
        Assert.Equal(reentrant, observation.Reentrant);
        Assert.True(observation.ManagedThreadId > 0);
    }
}
