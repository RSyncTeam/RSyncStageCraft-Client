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
/// no spike UI, no button and no invocation path, even when Dalamud loads it as
/// a dev plugin. A Debug build (RSC_DEVELOPMENT) loaded as a dev plugin
/// differs from a Release build only in the development diagnostics the actor
/// host logs and in capturing skeleton groups for preview (never uploaded).
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

    [Fact]
    public void NothingInThePluginComposesTheHumanPuppetHost()
    {
        // Composition is not part of this candidate: no plugin method names,
        // constructs, calls or implements any human puppet type, in either
        // build, so no build can reach it.
        Assert.Empty(Plugin.ReferenceCallers(target => target.Type.StartsWith(HumanPuppetNamespace, StringComparison.Ordinal)));
        Assert.DoesNotContain(Plugin.ReferencedTypeNames, name => name.StartsWith(HumanPuppetNamespace, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ApplyState")]
    [InlineData("ApplyStateName")]
    [InlineData("UnlockState")]
    [InlineData("UnlockStateName")]
    [InlineData("UnlockAll")]
    [InlineData("RevertState")]
    [InlineData("RevertStateName")]
    [InlineData("RevertToAutomation")]
    [InlineData("RevertToAutomationName")]
    [InlineData("SetItem")]
    [InlineData("SetItemName")]
    [InlineData("DeletePlayerState")]
    public void NoBuildReferencesAGlamourerStateWrite(string subscriber)
    {
        // H-12: no state-writing Glamourer path, and no DeletePlayerState (P14),
        // until a later candidate: the plugin names no such Glamourer.Api
        // subscriber, calls no member of one, and holds no such IPC label.
        Assert.DoesNotContain(Plugin.ReferencedTypeNames, name => name.EndsWith("." + subscriber, StringComparison.Ordinal));
        Assert.Empty(Plugin.ReferenceCallers(target => target.Type.StartsWith("Glamourer.", StringComparison.Ordinal) &&
            (target.Type.EndsWith("." + subscriber, StringComparison.Ordinal) || target.Name == subscriber)));
        Assert.DoesNotContain(Plugin.UserStrings, value => value.StartsWith("Glamourer." + subscriber, StringComparison.Ordinal));
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
