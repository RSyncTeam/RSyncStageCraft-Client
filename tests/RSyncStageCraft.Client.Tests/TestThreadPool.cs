using System.Runtime.CompilerServices;

namespace RSyncStageCraft.Tests;

internal static class TestThreadPool
{
    // Many tests block a pool thread on purpose (ManualResetEventSlim.Wait) to
    // hold an operation open. On 2-core CI runners the default minimum leaves
    // the pool starved and unrelated waits time out, so start with headroom.
#pragma warning disable CA2255 // Test assembly, not a library.
    [ModuleInitializer]
    internal static void Initialize() => ThreadPool.SetMinThreads(64, 64);
#pragma warning restore CA2255
}
