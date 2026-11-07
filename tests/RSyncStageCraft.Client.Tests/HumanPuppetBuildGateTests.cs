namespace RSyncStageCraft.Tests;

/// <summary>
/// H-0: the stage-3 human puppet host exists only in a Debug build. These
/// tests run in both CI jobs, so absence from Release is checked there.
/// </summary>
public sealed class HumanPuppetBuildGateTests
{
    private const string HumanNamespace = "RSyncStageCraft.Core.Actors.Humans";

    private static readonly Type[] ClientTypes = typeof(RSyncStageCraft.Core.RSyncStageCraftRuntime).Assembly.GetTypes();

    private static string[] HumanTypeNames() =>
        ClientTypes.Where(type => type.Namespace == HumanNamespace).Select(type => type.FullName!).ToArray();

#if RSC_DEVELOPMENT
    [Fact]
    public void ADebugClientHasTheHumanPuppetHost() =>
        Assert.Contains(HumanNamespace + ".HumanPuppetHost", HumanTypeNames());
#else
    [Fact]
    public void AReleaseClientHasNoHumanPuppetType()
    {
        Assert.Empty(HumanTypeNames());
        Assert.DoesNotContain(ClientTypes, type => type.Name.Contains("HumanPuppet", StringComparison.Ordinal) ||
            type.Name.Contains("HumanParentMap", StringComparison.Ordinal) || type.Name.Contains("HumanCollection", StringComparison.Ordinal));
    }
#endif
}
