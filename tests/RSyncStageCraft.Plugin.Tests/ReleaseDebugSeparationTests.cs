#nullable enable
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RSyncStageCraft.Ui;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The plugin holds no user-reachable development tooling in any build: no
/// Debug Tools window type, no self-test controller instance or registration,
/// no spike UI and no button, even when Dalamud loads it as a dev plugin. A
/// Debug build (RSC_DEVELOPMENT) loaded as a dev plugin differs from a Release
/// build only in the development diagnostics the actor host logs, in capturing
/// skeleton groups for preview (never uploaded), and in T3-1's one invocation
/// path (R-1): a command used only from the Dalamud log window's console, which
/// composes the human puppet host through its runner and nothing else (R-1b).
/// A Release build has none of that runner, its command, its adapters or its
/// IPC labels (checked as positive absence below).
///
/// Every test runs in both CI jobs, so absence is checked in both builds.
/// </summary>
public sealed class ReleaseDebugSeparationTests
{
#if RSC_DEVELOPMENT
    private const bool Debug = true;
#else
    private const bool Debug = false;
#endif

    private static readonly PluginMetadata Plugin = new(typeof(DevelopmentBuild).Assembly.Location);
    private static readonly PluginMetadata Client = new(typeof(RSyncStageCraft.Core.RSyncStageCraftRuntime).Assembly.Location);

    [Fact]
    public void TheTestsAndThePluginWereBuiltInTheSameConfiguration()
    {
        Assert.Equal(Debug, DevelopmentBuild.Enabled);
        Assert.Equal(Debug, DevelopmentBuild.ToolsEnabled(isDevPlugin: true));
        Assert.False(DevelopmentBuild.ToolsEnabled(isDevPlugin: false));
    }

    // The five tabs and the Settings sections are the same in both builds;
    // there is no Development section and no way to open Debug Tools.
    [Fact]
    public void TheMainWindowNavigationIsTheSameInBothBuilds()
    {
        Assert.Equal<(string, MainTab)>(
            [
                ("Scene here", MainTab.SceneHere),
                ("Furniture & lights", MainTab.FurnitureAndLights),
                ("Actors", MainTab.Actors),
                ("Appearances", MainTab.Appearances),
                ("Settings", MainTab.Settings),
            ],
            PluginNavigation.Tabs);
        Assert.Equal(5, Enum.GetNames<MainTab>().Length);

        Assert.Equal<(string, SettingsSection)>(
            [
                ("Server", SettingsSection.Server),
                ("Plugins", SettingsSection.Plugins),
                ("Location details", SettingsSection.LocationDetails),
                ("About", SettingsSection.About),
            ],
            PluginNavigation.SettingsSections);
        Assert.DoesNotContain("Development", Enum.GetNames<SettingsSection>());
        Assert.False(Plugin.HasType("RSyncStageCraft.Ui.PluginWindowKind"));
    }

    [Fact]
    public void NoBuildHasADebugToolsWindow()
    {
        Assert.DoesNotContain(Plugin.TypeNames, name => name.EndsWith("PluginTestSettingsWindow", StringComparison.Ordinal));
        Assert.False(Plugin.HasMethod("RSyncStageCraft.Ui.PluginUi", "OpenSettings"));
        Assert.False(Plugin.HasField("RSyncStageCraft.Ui.PluginUi", "settingsWindow"));
        Assert.True(Plugin.HasType("RSyncStageCraft.Ui.PluginUi+PluginMainWindow"));
        Assert.False(Plugin.HasField("RSyncStageCraft.Ui.PluginUi+PluginMainWindow", "openDebugTools"));
    }

    [Fact]
    public void NoSelfTestControllerOrSelfTestExists()
    {
        Assert.DoesNotContain(Plugin.TypeNames, name => name.Contains("SelfTest", StringComparison.Ordinal));
        Assert.Empty(Plugin.Callers(target =>
            target.Type.Contains("SelfTest", StringComparison.Ordinal) ||
            target.Name.Contains("SelfTest", StringComparison.Ordinal)));
    }

    [Fact]
    public void NoSpikeTypeMemberOrCallExists()
    {
        Assert.DoesNotContain(Plugin.TypeNames, name => name.Contains("Spike", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Plugin.Callers(target =>
            target.Type.Contains("Spike", StringComparison.OrdinalIgnoreCase) ||
            target.Name.Contains("Spike", StringComparison.OrdinalIgnoreCase)));
        foreach (var field in new[] { "spikeArmed", "replaceArmed", "spikeTimelineId", "spikeTimelineSearch", "spikeTimelineResults" })
            Assert.False(Plugin.HasField("RSyncStageCraft.Ui.ActorWindow", field));
    }

    [Fact]
    public void OnlyTheProductionCompositionSwitchesDevelopmentDiagnostics()
    {
        // Nothing outside Core sets the host's development switch; the plugin
        // passes it once, through DevelopmentBuild, at composition.
        Assert.Empty(Plugin.Callers(target =>
                target.Type == "RSyncStageCraft.Core.Actors.ActorLocalObjectHost" && target.Name == "set_DevelopmentToolsEnabled")
            .Where(caller => !caller.Type.StartsWith("RSyncStageCraft.Core.", StringComparison.Ordinal)));
    }

    [Fact]
    public void ThePluginComposesActorsThroughTheDevelopmentSwitch()
    {
        var callers = Plugin.Callers(target => target.Type == "RSyncStageCraft.DevelopmentBuild" && target.Name == "ToolsEnabled");
        Assert.Contains(callers, caller => caller.Type == "RSyncStageCraft.PluginConnection" && caller.Name == ".ctor");
    }

    [Fact]
    public void OnlyTheProductionCompositionSetsTheCaptureOptions()
    {
        // The capture options turn skeleton groups on. The plugin builds them
        // once, from DevelopmentBuild.ToolsEnabled, and nothing else in the
        // plugin constructs, copies or sets them. Calls into the Client
        // assembly are member references to type references, which Callers
        // does not resolve, so they are read through ReferenceCallers.
        const string options = "RSyncStageCraft.Core.Appearance.AppearanceCaptureOptions";
        var callers = Plugin.ReferenceCallers(target => target.Type == options &&
            target.Name is ".ctor" or "set_AllowSkeletonGroups" or "set_AllowDeformer" or "<Clone>$").ToArray();
        Assert.Equal(new[] { new MethodName("RSyncStageCraft.PluginConnection", ".ctor") }, callers);
        Assert.Contains(new MethodName("RSyncStageCraft.PluginConnection", ".ctor"),
            Plugin.ReferenceCallers(target => target.Type == options && target.Name == ".ctor"));
        // The reader's race switch is passed from the same options, in the same place.
        Assert.Equal(new[] { new MethodName("RSyncStageCraft.PluginConnection", ".ctor") },
            Plugin.ReferenceCallers(target => target.Type == "RSyncStageCraft.Core.Appearance.GameAppearanceCaptureReader" && target.Name == ".ctor"));
    }

    [Fact]
    public void NothingInThePluginReceivesASkeletonGroup()
    {
        // Receiving a skeleton group is not wired: no plugin method constructs,
        // copies, reads or sets the receive options, calls HoldBundleAsync,
        // touches a bundle hold or uses the Debug skeleton-group receiver.
        const string options = "RSyncStageCraft.Core.Appearance.AppearanceReceiveOptions";
        const string hold = "RSyncStageCraft.Core.Appearance.AppearanceBundleHold";
        const string client = "RSyncStageCraft.Core.Appearance.AppearanceTransferClient";
        Assert.Empty(Plugin.ReferenceCallers(target => target.Type is options or hold ||
            (target.Type == client && target.Name == "HoldBundleAsync") ||
            SkeletonGroupReceiverTypes.Contains(target.Type)));
        // Nor does the plugin name the receiver's types at all: no field,
        // parameter, local, call or implementation of the host port.
        Assert.Empty(Plugin.ReferencedTypeNames.Intersect(SkeletonGroupReceiverTypes, StringComparer.Ordinal));
    }

    /// <summary>The Debug skeleton-group receiver (W1) and everything only it uses; not wired to any plugin code.</summary>
    private static readonly string[] SkeletonGroupReceiverTypes =
    [
        "RSyncStageCraft.Core.Appearance.SkeletonGroupReceiver",
        "RSyncStageCraft.Core.Appearance.ISkeletonGroupAssignmentPort",
        "RSyncStageCraft.Core.Appearance.ActorSlotKey",
        "RSyncStageCraft.Core.Appearance.SkeletonGroupReceiveResult",
        "RSyncStageCraft.Core.Appearance.SkeletonGroupReceiveStatus",
        "RSyncStageCraft.Core.Appearance.SkeletonGroupReceiveStep",
    ];

    /// <summary>The stage-3 Debug human puppet host (H-0): Client code under RSC_DEVELOPMENT only.</summary>
    private const string HumanPuppetNamespace = "RSyncStageCraft.Core.Actors.Humans.";

    [Fact]
    public void TheHumanPuppetHostIsCompiledOutOfRelease()
    {
        var client = typeof(RSyncStageCraft.Core.RSyncStageCraftRuntime).Assembly;
        Assert.Equal(Debug, client.GetType(HumanPuppetNamespace + "HumanPuppetHost") is not null);
        Assert.Equal(Debug, client.GetTypes().Any(type => type.FullName!.StartsWith(HumanPuppetNamespace, StringComparison.Ordinal)));
    }

    private const string HumanPuppetHostType = HumanPuppetNamespace + "HumanPuppetHost";
    private const string T3SequenceType = HumanPuppetNamespace + "HumanPuppetT3Sequence";
    private const string T3RunType = "RSyncStageCraft.HumanPuppetT3Run";

    /// <summary>T3-1's plugin types (A1–A5): Debug only.</summary>
    private static readonly string[] T3PluginTypes =
    [
        T3RunType,
        "RSyncStageCraft.Adapters.HumanPuppetNativeBackend",
        "RSyncStageCraft.Adapters.HumanParentMapIpc",
        "RSyncStageCraft.Adapters.HumanPenumbraReadIpc",
        "RSyncStageCraft.Adapters.HumanGlamourerReadIpc",
        "RSyncStageCraft.Adapters.HumanPuppetT3Probe",
    ];

    [Fact]
    public void OnlyTheT3RunnerComposesTheHumanPuppetHost()
    {
        if (!DebugBuild())
        {
            // Release: no plugin method names, constructs, calls or implements any human puppet type.
            Assert.Empty(Plugin.ReferenceCallers(target => target.Type.StartsWith(HumanPuppetNamespace, StringComparison.Ordinal)));
            Assert.DoesNotContain(Plugin.ReferencedTypeNames, name => name.StartsWith(HumanPuppetNamespace, StringComparison.Ordinal));
            return;
        }
        // R-1b, Debug: the plugin never constructs the host itself. In the
        // Client only the T3-1 sequence constructs it; in the plugin only the
        // runner constructs the sequence and the adapters; and only the
        // connection's constructor constructs the runner. That the site is
        // inside DevelopmentBuild.ToolsEnabled is not proved here (IL cannot
        // prove a runtime branch, and the constructor calls ToolsEnabled for
        // other tools too): the #if around the site and the branch are the guards.
        Assert.Empty(Plugin.ReferenceCallers(target => target.Type == HumanPuppetHostType && target.Name == ".ctor"));
        Assert.Equal(new[] { new MethodName(T3SequenceType, ".ctor") },
            Client.Callers(target => target.Type == HumanPuppetHostType && target.Name == ".ctor").ToArray());
        Assert.Equal(new[] { new MethodName(T3RunType, ".ctor") },
            Plugin.ReferenceCallers(target => target.Type == T3SequenceType && target.Name == ".ctor").ToArray());
        foreach (var adapter in T3PluginTypes.Skip(1))
            Assert.Equal(new[] { new MethodName(T3RunType, ".ctor") },
                Plugin.Callers(target => target.Type == adapter && target.Name == ".ctor").ToArray());
        Assert.Equal(new[] { new MethodName("RSyncStageCraft.PluginConnection", ".ctor") },
            Plugin.Callers(target => target.Type == T3RunType && target.Name == ".ctor").ToArray());
    }

    [Fact]
    public void NothingCallsTheHumanPuppetAssignment()
    {
        // F1's assignment path stays unreachable in both builds: no Client or
        // plugin method calls it, so A3's throwing write members are never reached.
        Assert.Empty(Client.Callers(target => target.Type == HumanPuppetHostType && target.Name == "AssignTemporaryCollection"));
        Assert.Empty(Plugin.ReferenceCallers(target => target.Type == HumanPuppetHostType && target.Name == "AssignTemporaryCollection"));
    }

    [Fact]
    public void TheKindRestoreExistsOnlyInDebugAndOnlyTheSpawnCallsIt()
    {
        // R-8a: the only kind restore is the human puppet backend's. Release
        // has no restore member, implementation or caller; in Debug only the
        // host's spawn calls it, only the T3-1 native backend implements it,
        // and no plugin method calls it.
        const string backend = HumanPuppetNamespace + "IHumanPuppetBackend";
        const string native = "RSyncStageCraft.Adapters.HumanPuppetNativeBackend";
        var client = typeof(RSyncStageCraft.Core.RSyncStageCraftRuntime).Assembly;
        Assert.Equal(Debug, client.GetType(backend)?.GetMethod("RestoreKind") is not null);
        Assert.Equal(Debug, client.GetType(HumanPuppetNamespace + "HumanPuppetKindPair") is not null);
        Assert.Equal(Debug, Plugin.HasMethod(native, "RestoreKind"));
        Assert.Empty(Plugin.ReferenceCallers(target => target.Name == "RestoreKind"));
        if (!DebugBuild())
        {
            Assert.Empty(Client.Callers(target => target.Name == "RestoreKind"));
            return;
        }
        Assert.Equal(new[] { new MethodName(HumanPuppetHostType, "SpawnCore") },
            Client.Callers(target => target.Name == "RestoreKind").ToArray());
    }

    [Theory]
    [InlineData("CreateTemporaryCollection")]
    [InlineData("AssignTemporaryCollection")]
    [InlineData("DeleteTemporaryCollection")]
    [InlineData("AddTemporaryMod")]
    [InlineData("RemoveTemporaryMod")]
    [InlineData("SetCollectionForObject")]
    [InlineData("SetCollection")]
    [InlineData("RedrawObject")]
    [InlineData("RedrawAll")]
    public void NoBuildReferencesAPenumbraWrite(string subscriber)
    {
        if (DebugBuild() && Hv2PenumbraWrites.Contains(subscriber))
        {
            // Debug, HV-2 only: exactly this subscriber (not one whose name
            // merely starts with it, such as a mod for every collection),
            // called only by HV-2's adapter.
            Assert.Equal(new[] { "Penumbra.Api.IpcSubscribers." + subscriber }, Plugin.ReferencedTypeNames
                .Where(name => name.StartsWith("Penumbra.Api.IpcSubscribers." + subscriber, StringComparison.Ordinal)).ToArray());
            var callers = Plugin.ReferenceCallers(target => target.Type.StartsWith("Penumbra.Api.IpcSubscribers." + subscriber, StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(callers);
            Assert.All(callers, caller => Assert.True(InHv2Adapter(caller), caller.ToString()));
            // The same name-based check as Release: any Penumbra member named
            // like the write, on any type, is called from the adapter only.
            var named = Plugin.ReferenceCallers(target => target.Type.StartsWith("Penumbra.", StringComparison.Ordinal) &&
                target.Name.StartsWith(subscriber, StringComparison.Ordinal)).ToArray();
            Assert.DoesNotContain(named, caller => !InHv2Adapter(caller));
            Assert.DoesNotContain(Plugin.UserStrings, value => value.StartsWith("Penumbra." + subscriber, StringComparison.Ordinal));
            return;
        }
        // Both builds (Release for HV-2's own calls too; the redraws in Debug
        // as well, since HV-2 redraws through the game, not Penumbra): no
        // temporary collection or mod, no collection write and no redraw: no such
        // Penumbra.Api subscriber (or one whose name starts with it), no call
        // to one, and no such IPC label.
        Assert.DoesNotContain(Plugin.ReferencedTypeNames, name =>
            name.StartsWith("Penumbra.Api.IpcSubscribers." + subscriber, StringComparison.Ordinal));
        Assert.Empty(Plugin.ReferenceCallers(target => target.Type.StartsWith("Penumbra.", StringComparison.Ordinal) &&
            (target.Type.StartsWith("Penumbra.Api.IpcSubscribers." + subscriber, StringComparison.Ordinal) ||
             target.Name.StartsWith(subscriber, StringComparison.Ordinal))));
        Assert.DoesNotContain(Plugin.UserStrings, value => value.StartsWith("Penumbra." + subscriber, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryPenumbraSubscriberReferencedIsOnTheReadAllowlist()
    {
        // An allowlist, not only a denylist: any Penumbra.Api subscriber not
        // named here (a write, a redraw, a mod or settings change) fails.
        const string prefix = "Penumbra.Api.IpcSubscribers.";
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "ApiVersion", "GetGameObjectResourcePaths", "GetModDirectory" };
        if (DebugBuild())
        {
            allowed.UnionWith(new[] { "GetCollection", "GetCollectionForObject", "GetCollections", "GetCutsceneParentIndex", "SetCutsceneParentIndex",
                "Initialized", "Disposed" });
            // HV-2's read, and its writes (kept to its adapter by NoBuildReferencesAPenumbraWrite).
            allowed.Add("GetMetaManipulations");
            allowed.UnionWith(Hv2PenumbraWrites);
        }
        var referenced = Plugin.ReferencedTypeNames.Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => name[prefix.Length..]).ToArray();
        Assert.NotEmpty(referenced);
        Assert.DoesNotContain(referenced, name => !allowed.Contains(name));
    }

    // A branch on the constant itself is unreachable code (CS0162) in one build.
    private static bool DebugBuild() => Debug;

    [Fact]
    public void TheT3RunnerItsCommandAndItsIpcExistOnlyInDebug()
    {
        // Positive absence in Release (and presence in Debug, so the check is not vacuous).
        foreach (var type in T3PluginTypes)
            Assert.Equal(Debug, Plugin.HasType(type));
        Assert.Equal(Debug, Plugin.TypeNames.Any(name => name.Contains("HumanPuppet", StringComparison.Ordinal)));
        Assert.Equal(Debug, Plugin.ReferencedTypeNames.Any(name => name.StartsWith(HumanPuppetNamespace, StringComparison.Ordinal)));
        foreach (var subscriber in new[] { "SetCutsceneParentIndex", "GetCutsceneParentIndex", "GetCollection", "GetCollectionForObject", "GetCollections" })
            Assert.Equal(Debug, Plugin.ReferencedTypeNames.Contains("Penumbra.Api.IpcSubscribers." + subscriber));
        Assert.Equal(Debug, Plugin.ReferencedTypeNames.Contains("Glamourer.Api.IpcSubscribers.CanUnlock"));
        Assert.Equal(Debug, Plugin.UserStrings.Any(value => value.StartsWith("CustomizePlus.", StringComparison.Ordinal)));
        Assert.Equal(Debug, Plugin.UserStrings.Contains("/rsct31"));
        Assert.Equal(Debug, Plugin.ReferencedTypeNames.Contains("Dalamud.Plugin.Services.ICommandManager"));
        Assert.Equal(Debug, Plugin.ReferencedTypeNames.Contains("Dalamud.Game.Command.CommandInfo"));
    }

    private const string Hv1RunType = "RSyncStageCraft.HumanActorBaselineRun";
    private const string Hv1NativeType = "RSyncStageCraft.Adapters.HumanActorNativeBackend";
    private const string Hv1BaselineType = HumanPuppetNamespace + "HumanActorBaseline";

    [Fact]
    public void TheHv1RunnerItsCommandAndItsAdapterExistOnlyInDebug()
    {
        // HV-1: positive absence in Release, and in Debug one composition
        // site: the connection constructs the runner, and only the runner
        // constructs the native adapter and the baseline.
        var client = typeof(RSyncStageCraft.Core.RSyncStageCraftRuntime).Assembly;
        Assert.Equal(Debug, client.GetType(Hv1BaselineType) is not null);
        Assert.Equal(Debug, client.GetType(HumanPuppetNamespace + "IHumanActorBackend") is not null);
        Assert.Equal(Debug, Plugin.HasType(Hv1RunType));
        Assert.Equal(Debug, Plugin.HasType(Hv1NativeType));
        Assert.Equal(Debug, Plugin.UserStrings.Contains("/rschv1"));
        if (!DebugBuild())
            return;
        Assert.Equal(new[] { new MethodName("RSyncStageCraft.PluginConnection", ".ctor") },
            Plugin.Callers(target => target.Type == Hv1RunType && target.Name == ".ctor").ToArray());
        // HV-2 and HV-3 compose the same native adapter and baseline in their own runners.
        // Ordinal order: HumanActorBaselineRun, HumanActorGlamourerRun, HumanActorPenumbraRun.
        Assert.Equal(new[] { Hv1RunType, Hv3RunType, Hv2RunType },
            Plugin.Callers(target => target.Type == Hv1NativeType && target.Name == ".ctor").Select(caller => caller.Type).Order(StringComparer.Ordinal));
        Assert.All(Plugin.Callers(target => target.Type == Hv1NativeType && target.Name == ".ctor"), caller => Assert.Equal(".ctor", caller.Name));
        // Ordinal order: HumanActorBaselineRun, HumanActorGlamourerRun, HumanActorPenumbraRun.
        Assert.Equal(new[] { Hv1RunType, Hv3RunType, Hv2RunType },
            Plugin.ReferenceCallers(target => target.Type == Hv1BaselineType && target.Name == ".ctor").Select(caller => caller.Type)
                .Order(StringComparer.Ordinal));
        Assert.All(Plugin.ReferenceCallers(target => target.Type == Hv1BaselineType && target.Name == ".ctor"),
            caller => Assert.Equal(".ctor", caller.Name));
    }

    private const string Hv2RunType = "RSyncStageCraft.HumanActorPenumbraRun";
    private const string Hv2PenumbraType = "RSyncStageCraft.Adapters.HumanActorPenumbraIpc";
    private const string Hv2StageType = HumanPuppetNamespace + "HumanActorPenumbraStage";

    /// <summary>HV-2's Penumbra writes: Debug only, and only in its adapter. No redraw: HV-2 undraws and draws through the game.</summary>
    private static readonly string[] Hv2PenumbraWrites =
    [
        "CreateTemporaryCollection", "AssignTemporaryCollection", "DeleteTemporaryCollection", "AddTemporaryMod", "RemoveTemporaryMod",
    ];

    /// <summary>The caller is HV-2's adapter, or a type the compiler nests in it.</summary>
    private static bool InHv2Adapter(MethodName caller) =>
        caller.Type == Hv2PenumbraType || caller.Type.StartsWith(Hv2PenumbraType + "+", StringComparison.Ordinal);

    [Fact]
    public void TheHv2RunnerItsCommandAndItsPenumbraAdapterExistOnlyInDebug()
    {
        // HV-2: positive absence in Release, and in Debug one composition
        // site: the connection constructs the runner, and only the runner
        // constructs the Penumbra adapter and the stage. Every HV-2 Penumbra
        // call, read or write, is in that adapter.
        var client = typeof(RSyncStageCraft.Core.RSyncStageCraftRuntime).Assembly;
        Assert.Equal(Debug, client.GetType(Hv2StageType) is not null);
        Assert.Equal(Debug, client.GetType(HumanPuppetNamespace + "IHumanActorPenumbra") is not null);
        Assert.Equal(Debug, Plugin.HasType(Hv2RunType));
        Assert.Equal(Debug, Plugin.HasType(Hv2PenumbraType));
        Assert.Equal(Debug, Plugin.UserStrings.Contains("/rschv2"));
        foreach (var subscriber in Hv2PenumbraWrites.Append("GetMetaManipulations"))
            Assert.Equal(Debug, Plugin.ReferencedTypeNames.Contains("Penumbra.Api.IpcSubscribers." + subscriber));
        // Neither build subscribes to Penumbra's redraw event: nothing waits on a redraw queued in Penumbra.
        Assert.DoesNotContain(Plugin.ReferencedTypeNames, name => name.StartsWith("Penumbra.Api.IpcSubscribers.GameObjectRedrawn", StringComparison.Ordinal));
        if (!DebugBuild())
            return;
        Assert.Equal(new[] { new MethodName("RSyncStageCraft.PluginConnection", ".ctor") },
            Plugin.Callers(target => target.Type == Hv2RunType && target.Name == ".ctor").ToArray());
        // HV-3 composes the same Penumbra adapter and stage in its own runner.
        Assert.Equal(new[] { new MethodName(Hv3RunType, ".ctor"), new MethodName(Hv2RunType, ".ctor") },
            Plugin.Callers(target => target.Type == Hv2PenumbraType && target.Name == ".ctor").OrderBy(caller => caller.Type, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { new MethodName(Hv3RunType, ".ctor"), new MethodName(Hv2RunType, ".ctor") },
            Plugin.ReferenceCallers(target => target.Type == Hv2StageType && target.Name == ".ctor").OrderBy(caller => caller.Type, StringComparer.Ordinal)
                .ToArray());
        var metaCallers = Plugin.ReferenceCallers(target => target.Type == "Penumbra.Api.IpcSubscribers.GetMetaManipulations").ToArray();
        Assert.NotEmpty(metaCallers);
        Assert.All(metaCallers, caller => Assert.True(InHv2Adapter(caller), caller.ToString()));
    }

    private const string Hv3RunType = "RSyncStageCraft.HumanActorGlamourerRun";
    private const string Hv3GlamourerType = "RSyncStageCraft.Adapters.HumanActorGlamourerIpc";
    private const string Hv3StageType = HumanPuppetNamespace + "HumanActorGlamourerStage";

    /// <summary>HV-3's Glamourer state calls, by object index: Debug only, and only in its adapter.</summary>
    private static readonly string[] Hv3GlamourerCalls = ["UnlockState", "ApplyState", "GetState", "RevertState"];

    /// <summary>The caller is HV-3's adapter, or a type the compiler nests in it.</summary>
    private static bool InHv3Adapter(MethodName caller) =>
        caller.Type == Hv3GlamourerType || caller.Type.StartsWith(Hv3GlamourerType + "+", StringComparison.Ordinal);

    [Theory]
    [InlineData("ApplyState")]
    [InlineData("UnlockState")]
    [InlineData("GetState")]
    [InlineData("RevertState")]
    [InlineData("ApplyStateName")]
    [InlineData("UnlockStateName")]
    [InlineData("GetStateName")]
    [InlineData("GetStateBase64Name")]
    [InlineData("UnlockAll")]
    [InlineData("RevertStateName")]
    [InlineData("RevertToAutomation")]
    [InlineData("RevertToAutomationName")]
    [InlineData("ReapplyState")]
    [InlineData("ReapplyStateName")]
    [InlineData("ApplyDesign")]
    [InlineData("ApplyDesignName")]
    [InlineData("SetItem")]
    [InlineData("SetItemName")]
    [InlineData("SetMetaState")]
    [InlineData("SetMetaStateName")]
    [InlineData("DeletePlayerState")]
    public void NoBuildReferencesAGlamourerStateWrite(string subscriber)
    {
        if (DebugBuild() && Hv3GlamourerCalls.Contains(subscriber))
        {
            // Debug, HV-3 only: exactly this by-index subscriber (never its
            // by-name variant, which the other cases forbid in both builds),
            // called only by HV-3's adapter.
            Assert.Equal(new[] { "Glamourer.Api.IpcSubscribers." + subscriber }, Plugin.ReferencedTypeNames
                .Where(name => name.EndsWith("." + subscriber, StringComparison.Ordinal)).ToArray());
            var callers = Plugin.ReferenceCallers(target => target.Type == "Glamourer.Api.IpcSubscribers." + subscriber).ToArray();
            Assert.NotEmpty(callers);
            Assert.All(callers, caller => Assert.True(InHv3Adapter(caller), caller.ToString()));
            Assert.DoesNotContain(Plugin.ReferenceCallers(target => target.Type.StartsWith("Glamourer.", StringComparison.Ordinal) &&
                target.Name == subscriber), caller => !InHv3Adapter(caller));
            Assert.DoesNotContain(Plugin.UserStrings, value => value.StartsWith("Glamourer." + subscriber, StringComparison.Ordinal));
            return;
        }
        // H-12, both builds: no other state-writing Glamourer path, no
        // by-name call, no design application and no DeletePlayerState (P14):
        // the plugin names no such Glamourer.Api subscriber, calls no member
        // of one, and holds no such IPC label. Release has none of HV-3's.
        Assert.DoesNotContain(Plugin.ReferencedTypeNames, name => name.EndsWith("." + subscriber, StringComparison.Ordinal));
        Assert.Empty(Plugin.ReferenceCallers(target => target.Type.StartsWith("Glamourer.", StringComparison.Ordinal) &&
            (target.Type.EndsWith("." + subscriber, StringComparison.Ordinal) || target.Name == subscriber)));
        Assert.DoesNotContain(Plugin.UserStrings, value => value.StartsWith("Glamourer." + subscriber, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryGlamourerSubscriberReferencedIsOnTheAllowlistAndNoneIsAnEvent()
    {
        // An allowlist, not only a denylist: any other Glamourer.Api
        // subscriber (a write, a by-name call, an event) fails.
        const string prefix = "Glamourer.Api.IpcSubscribers.";
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "ApiVersion", "AddDesign", "GetDesignList", "GetDesignListExtended", "GetDesignBase64", "GetStateBase64",
        };
        if (DebugBuild())
        {
            allowed.Add("CanUnlock");
            allowed.UnionWith(Hv3GlamourerCalls);
        }
        var referenced = Plugin.ReferencedTypeNames.Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => name[prefix.Length..]).ToArray();
        Assert.NotEmpty(referenced);
        Assert.DoesNotContain(referenced, name => !allowed.Contains(name));
        // No build subscribes to a Glamourer event: no RSC code runs inside a Glamourer call.
        Assert.Empty(Plugin.ReferenceCallers(target => target.Type.StartsWith("Glamourer.", StringComparison.Ordinal) && target.Name == "Subscriber"));
        Assert.DoesNotContain(Plugin.ReferencedTypeNames, name => name.StartsWith("Glamourer.", StringComparison.Ordinal) &&
            (name.Contains("StateChanged", StringComparison.Ordinal) || name.Contains("StateFinalized", StringComparison.Ordinal) ||
             name.Contains("GPoseChanged", StringComparison.Ordinal) || name.Contains("EventSubscriber", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheHv3RunnerItsCommandAndItsGlamourerAdapterExistOnlyInDebug()
    {
        // HV-3: positive absence in Release, and in Debug one composition
        // site: the connection constructs the runner, and only the runner
        // constructs the Glamourer adapter and stage. The adapter reads no
        // state as base64 and makes no design call.
        var client = typeof(RSyncStageCraft.Core.RSyncStageCraftRuntime).Assembly;
        Assert.Equal(Debug, client.GetType(Hv3StageType) is not null);
        Assert.Equal(Debug, client.GetType(HumanPuppetNamespace + "IHumanActorGlamourer") is not null);
        Assert.Equal(Debug, Plugin.HasType(Hv3RunType));
        Assert.Equal(Debug, Plugin.HasType(Hv3GlamourerType));
        Assert.Equal(Debug, Plugin.UserStrings.Contains("/rschv3"));
        foreach (var subscriber in Hv3GlamourerCalls)
            Assert.Equal(Debug, Plugin.ReferencedTypeNames.Contains("Glamourer.Api.IpcSubscribers." + subscriber));
        if (!DebugBuild())
            return;
        Assert.Equal(new[] { new MethodName("RSyncStageCraft.PluginConnection", ".ctor") },
            Plugin.Callers(target => target.Type == Hv3RunType && target.Name == ".ctor").ToArray());
        Assert.Equal(new[] { new MethodName(Hv3RunType, ".ctor") },
            Plugin.Callers(target => target.Type == Hv3GlamourerType && target.Name == ".ctor").ToArray());
        Assert.Equal(new[] { new MethodName(Hv3RunType, ".ctor") },
            Plugin.ReferenceCallers(target => target.Type == Hv3StageType && target.Name == ".ctor").ToArray());
        Assert.DoesNotContain(Plugin.ReferenceCallers(target => target.Type.StartsWith("Glamourer.", StringComparison.Ordinal) &&
            (target.Type.Contains("Base64", StringComparison.Ordinal) || target.Type.Contains("Design", StringComparison.Ordinal))), InHv3Adapter);
    }

    [Theory]
    [InlineData("Debug tools")]
    [InlineData("self-test")]
    [InlineData("spike")]
    [InlineData("Play for verification")]
    [InlineData("Observation run: ")]
    public void NoDevelopmentUiTextExists(string text) =>
        Assert.DoesNotContain(Plugin.UserStrings, value => value.Contains(text, StringComparison.OrdinalIgnoreCase));

    private readonly record struct MethodName(string Type, string Name);

    /// <summary>The plugin assembly read as metadata and IL; nothing is loaded or run.</summary>
    private sealed class PluginMetadata
    {
        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => code.Value);

        private readonly List<(MethodName Method, MethodName[] Calls)> bodies = [];
        private readonly List<(MethodName Method, MethodName[] Calls)> referenceBodies = [];
        private readonly HashSet<string> types = new(StringComparer.Ordinal);
        private readonly HashSet<MethodName> methods = [];
        private readonly Dictionary<MethodName, string> fields = [];

        public PluginMetadata(string path)
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            var strings = new List<string>();
            var handle = MetadataTokens.UserStringHandle(1);
            while (!handle.IsNil)
            {
                strings.Add(reader.GetUserString(handle));
                handle = reader.GetNextHandle(handle);
            }
            UserStrings = strings;
            ReferencedTypeNames = reader.TypeReferences
                .Select(reference => reader.GetTypeReference(reference))
                .Select(reference => reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name))
                .ToHashSet(StringComparer.Ordinal);
            var provider = new TypeNameProvider();
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                var typeName = TypeName(reader, typeHandle);
                types.Add(typeName);
                foreach (var fieldHandle in type.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle);
                    fields[new(typeName, reader.GetString(field.Name))] = field.DecodeSignature(provider, null);
                }
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    var name = new MethodName(typeName, reader.GetString(method.Name));
                    methods.Add(name);
                    if (method.RelativeVirtualAddress != 0)
                    {
                        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                        bodies.Add((name, Calls(reader, il, Resolve)));
                        referenceBodies.Add((name, Calls(reader, il, ResolveWithReferences)));
                    }
                }
            }
        }

        public IReadOnlyList<string> UserStrings { get; }

        /// <summary>Every type another assembly defines that the plugin names, as namespace and name.</summary>
        public IReadOnlyCollection<string> ReferencedTypeNames { get; }
        public IReadOnlyCollection<string> TypeNames => types;

        public bool HasType(string name) => types.Contains(name);
        public bool HasMethod(string type, string name) => methods.Contains(new(type, name));
        public bool HasField(string type, string name) => fields.ContainsKey(new(type, name));

        public IReadOnlyList<string> FieldsOfType(string type, string fieldType) =>
            fields.Where(entry => entry.Key.Type == type && entry.Value == fieldType).Select(entry => entry.Key.Name).ToArray();

        /// <summary>Methods whose IL calls, constructs or takes the address of a matching method.</summary>
        public IEnumerable<MethodName> Callers(Func<MethodName, bool> target) =>
            bodies.Where(body => body.Calls.Any(target)).Select(body => body.Method).Distinct();

        /// <summary>
        /// As <see cref="Callers"/>, but a call into another assembly (a member
        /// reference whose parent is a type reference) is also resolved, by
        /// namespace and name.
        /// </summary>
        public IEnumerable<MethodName> ReferenceCallers(Func<MethodName, bool> target) =>
            referenceBodies.Where(body => body.Calls.Any(target)).Select(body => body.Method).Distinct();

        private static MethodName[] Calls(MetadataReader reader, byte[] il, Func<MetadataReader, EntityHandle, MethodName?> resolve)
        {
            var calls = new List<MethodName>();
            var offset = 0;
            while (offset < il.Length)
            {
                short value = il[offset++];
                if (value == 0xFE)
                    value = unchecked((short)(0xFE00 | il[offset++]));
                var code = OpCodesByValue[value];
                switch (code.OperandType)
                {
                    case OperandType.InlineMethod:
                        var token = BitConverter.ToInt32(il, offset);
                        if (resolve(reader, MetadataTokens.EntityHandle(token)) is { } called)
                            calls.Add(called);
                        offset += 4;
                        break;
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        offset += 1;
                        break;
                    case OperandType.InlineVar:
                        offset += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        offset += 8;
                        break;
                    case OperandType.InlineSwitch:
                        var count = BitConverter.ToInt32(il, offset);
                        offset += 4 + 4 * count;
                        break;
                    default:
                        offset += 4;
                        break;
                }
            }
            return calls.ToArray();
        }

        private static MethodName? Resolve(MetadataReader reader, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return new(TypeName(reader, method.GetDeclaringType()), reader.GetString(method.Name));
                case HandleKind.MethodSpecification:
                    return Resolve(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                case HandleKind.MemberReference:
                    var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    return member.Parent.Kind == HandleKind.TypeDefinition
                        ? new(TypeName(reader, (TypeDefinitionHandle)member.Parent), reader.GetString(member.Name))
                        : null;
                default:
                    return null;
            }
        }

        private static MethodName? ResolveWithReferences(MetadataReader reader, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodSpecification:
                    return ResolveWithReferences(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                case HandleKind.MemberReference:
                    var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    return member.Parent.Kind == HandleKind.TypeReference
                        ? new(TypeName(reader, (TypeReferenceHandle)member.Parent), reader.GetString(member.Name))
                        : Resolve(reader, handle);
                default:
                    return Resolve(reader, handle);
            }
        }

        private static string TypeName(MetadataReader reader, TypeReferenceHandle handle)
        {
            var type = reader.GetTypeReference(handle);
            var name = reader.GetString(type.Name);
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? TypeName(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + name
                : (type.Namespace.IsNil ? "" : reader.GetString(type.Namespace) + ".") + name;
        }

        private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            return declaring.IsNil
                ? (type.Namespace.IsNil ? "" : reader.GetString(type.Namespace) + ".") + name
                : TypeName(reader, declaring) + "+" + name;
        }

        /// <summary>Type names from field signatures, without resolving any assembly.</summary>
        private sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
        {
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[*]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
            public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) =>
                genericType + "<" + string.Join(",", typeArguments) + ">";
            public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
            public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetPinnedType(string elementType) => elementType;
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
                TypeName(reader, handle);
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var type = reader.GetTypeReference(handle);
                return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
            }
            public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle,
                byte rawTypeKind) => "spec";
        }
    }
}
