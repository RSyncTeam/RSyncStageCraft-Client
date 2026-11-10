#nullable enable
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The plugin and the client library reference only what this repository
/// builds, the .NET base library, Dalamud and its bundled game libraries, and
/// the pinned Glamourer and Penumbra API packages. Their types live only in
/// the reviewed namespaces, and their compiled sources lie inside their own
/// project folders.
/// </summary>
public sealed class AssemblyBoundaryTests
{
    // Assemblies Dalamud loads for every plugin (Dalamud.dll and its bindings,
    // FFXIVClientStructs with its interop runtime, Lumina) and the pinned API
    // packages. A new dependency is added here in the same change.
    private static readonly string[] PluginReferences =
    [
        "FFXIVClientStructs",
        "Glamourer.Api",
        "InteropGenerator.Runtime",
        "Lumina",
        "Lumina.Excel",
        "Penumbra.Api",
        "RSyncStageCraft.Client",
        "RSyncStageCraft.Protocol",
    ];

#if RSC_DEVELOPMENT
    private const bool Debug = true;
#else
    private const bool Debug = false;
#endif

    // A branch on the constant itself is unreachable code (CS0162) in one build.
    private static bool DebugBuild() => Debug;

    /// <summary>
    /// HV-3 (owner-approved, Debug only): the pinned Glamourer.Api 2.8.2
    /// declares <c>GetState</c>'s IPC result as Newtonsoft's <c>JObject</c>,
    /// so its one caller must reference Newtonsoft.Json. Dalamud supplies that
    /// assembly to every plugin; RSC adds no package and bundles no copy.
    /// Only HV-3's Glamourer adapter, and types the compiler nests in it, may
    /// use it; Release references it not at all.
    /// </summary>
    private const string Hv3OnlyReference = "Newtonsoft.Json";

    private const string Hv3GlamourerAdapter = "RSyncStageCraft.Adapters.HumanActorGlamourerIpc";

    private static readonly string[] PluginNamespaces = ["", "RSyncStageCraft", "RSyncStageCraft.Adapters", "RSyncStageCraft.Ui"];

    // Attribute types the compiler embeds in an assembly that uses newer
    // language features; they are marked [Embedded] and [CompilerGenerated].
    private static readonly string[] CompilerNamespaces = ["Microsoft.CodeAnalysis", "System.Runtime.CompilerServices"];

    private static readonly AssemblyMetadata Plugin = new(typeof(DevelopmentBuild).Assembly.Location);
    private static readonly AssemblyMetadata Client = new(typeof(RSyncStageCraft.Core.RSyncStageCraftRuntime).Assembly.Location);

    [Fact]
    public void ThePluginReferencesOnlyItsReviewedDependencies()
    {
        Assert.All(Plugin.References, name => Assert.True(
            IsBaseLibrary(name) || name == "Dalamud" || name.StartsWith("Dalamud.", StringComparison.Ordinal) || PluginReferences.Contains(name) ||
            (DebugBuild() && name == Hv3OnlyReference),
            $"unexpected assembly reference {name}"));
        Assert.Contains("RSyncStageCraft.Client", Plugin.References);
        Assert.Contains("RSyncStageCraft.Protocol", Plugin.References);
    }

    [Fact]
    public void NewtonsoftJsonIsReferencedOnlyInDebugAndOnlyByTheHv3GlamourerAdapter()
    {
        var users = TypesUsingAssembly(Plugin.Path, Hv3OnlyReference);
        if (!DebugBuild())
        {
            // Release: no assembly reference, so no type can use it.
            Assert.DoesNotContain(Hv3OnlyReference, Plugin.References);
            Assert.Empty(users);
            return;
        }
        // Debug: the reference exists (the check is not vacuous), and every
        // plugin type whose signatures, fields, locals, base types or IL use a
        // Newtonsoft type is the HV-3 adapter or a type the compiler nests in it.
        Assert.Contains(Hv3OnlyReference, Plugin.References);
        Assert.Contains(users, user => user.Name == Hv3GlamourerAdapter);
        // The adapter itself, or a compiler-generated type nested in it; never a hand-written nested type.
        Assert.All(users, user => Assert.True(
            user.Name == Hv3GlamourerAdapter || (user.Name.StartsWith(Hv3GlamourerAdapter + "+", StringComparison.Ordinal) && user.CompilerGenerated),
            $"{user.Name} uses {Hv3OnlyReference}"));
    }

    [Fact]
    public void TheClientNeverReferencesNewtonsoftJson()
    {
        Assert.DoesNotContain(Hv3OnlyReference, Client.References);
        Assert.Empty(TypesUsingAssembly(Client.Path, Hv3OnlyReference));
    }

    [Fact]
    public void TheClientReferencesOnlyTheProtocolAndTheBaseLibrary()
    {
        Assert.All(Client.References, name => Assert.True(
            IsBaseLibrary(name) || name == "RSyncStageCraft.Protocol",
            $"unexpected assembly reference {name}"));
        Assert.Contains("RSyncStageCraft.Protocol", Client.References);
        Assert.Equal("RSyncStageCraft.Protocol", typeof(ProtocolConstants).Assembly.GetName().Name);
    }

    [Fact]
    public void PluginTypesLiveOnlyInThePluginNamespaces()
    {
        Assert.All(Plugin.Types, type => Assert.True(
            PluginNamespaces.Contains(type.Namespace) || (type.CompilerGenerated && CompilerNamespaces.Contains(type.Namespace)),
            $"unexpected type {type.Namespace}.{type.Name}"));
    }

    [Fact]
    public void ClientTypesLiveOnlyInTheCoreNamespaces()
    {
        Assert.All(Client.Types, type => Assert.True(
            type.Namespace is "" or "RSyncStageCraft.Core"
                || type.Namespace.StartsWith("RSyncStageCraft.Core.", StringComparison.Ordinal)
                || (type.CompilerGenerated && CompilerNamespaces.Contains(type.Namespace)),
            $"unexpected type {type.Namespace}.{type.Name}"));
    }

    [Fact]
    public void OnlyThePluginEmbedsAResourceAndItIsTheBNpcLinkData()
    {
        Assert.Equal(new[] { "RSyncStageCraft.Data.BNpcLink.csv" }, Plugin.Resources);
        Assert.Empty(Client.Resources);
    }

    [Theory]
    [InlineData("RSyncStageCraft", "src/RSyncStageCraft/")]
    [InlineData("RSyncStageCraft.Client", "src/RSyncStageCraft.Client/")]
    public void EveryCompiledSourceLiesInsideItsProjectFolder(string assembly, string folder)
    {
        var path = assembly == "RSyncStageCraft" ? Plugin.Path : Client.Path;
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        // A side-by-side or an embedded portable PDB, whichever the build wrote.
        Assert.True(pe.TryOpenAssociatedPortablePdb(path, File.OpenRead, out var provider, out _), $"no portable PDB for {assembly}");
        using var pdb = provider!;
        var metadata = pdb.GetMetadataReader();
        var documents = metadata.Documents.Select(handle => metadata.GetString(metadata.GetDocument(handle).Name)).ToArray();
        Assert.NotEmpty(documents);
        // The build maps the repository root to /_/ (Directory.Build.props);
        // generated files under obj/ are inside the project folder too.
        Assert.All(documents, document => Assert.StartsWith("/_/" + folder, document.Replace('\\', '/'), StringComparison.Ordinal));
    }

    /// <summary>
    /// Every type definition (nested types as Outer+Inner, with whether it is
    /// compiler-generated) whose base type, interfaces, generic constraints,
    /// field, method, property or local signatures, custom attributes (on the
    /// type, its fields, methods, parameters, properties and events), catch
    /// clauses, or IL operands (method, field, type and signature tokens,
    /// including generic instantiations) name a type from
    /// <paramref name="assembly"/>. An assembly- or module-level attribute
    /// from it is reported as "&lt;assembly&gt;". Read as metadata; nothing
    /// is loaded or run.
    /// </summary>
    private static IReadOnlyList<(string Name, bool CompilerGenerated)> TypesUsingAssembly(string path, string assembly)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var target = reader.AssemblyReferences.FirstOrDefault(handle => reader.GetString(reader.GetAssemblyReference(handle).Name) == assembly);
        if (target.IsNil)
            return [];
        var scope = new ScopeProvider(target);
        var users = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        bool Attributes(CustomAttributeHandleCollection attributes) =>
            attributes.Any(handle => scope.Uses(reader, reader.GetCustomAttribute(handle).Constructor));
        bool Constraints(GenericParameterHandleCollection parameters) =>
            parameters.Any(handle => reader.GetGenericParameter(handle).GetConstraints()
                .Any(constraint => scope.Uses(reader, reader.GetGenericParameterConstraint(constraint).Type)));
        if (Attributes(reader.GetAssemblyDefinition().GetCustomAttributes()) || Attributes(reader.GetModuleDefinition().GetCustomAttributes()))
            users["<assembly>"] = false;
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var uses = scope.Uses(reader, type.BaseType) ||
                type.GetInterfaceImplementations().Any(handle => scope.Uses(reader, reader.GetInterfaceImplementation(handle).Interface)) ||
                Attributes(type.GetCustomAttributes()) || Constraints(type.GetGenericParameters()) ||
                type.GetFields().Any(handle => reader.GetFieldDefinition(handle) is var field &&
                    (field.DecodeSignature(scope, null) || Attributes(field.GetCustomAttributes()))) ||
                type.GetProperties().Any(handle => reader.GetPropertyDefinition(handle) is var property &&
                    (Any(property.DecodeSignature(scope, null)) || Attributes(property.GetCustomAttributes()))) ||
                type.GetEvents().Any(handle => reader.GetEventDefinition(handle) is var definition &&
                    (scope.Uses(reader, definition.Type) || Attributes(definition.GetCustomAttributes())));
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                uses |= Any(method.DecodeSignature(scope, null)) || Attributes(method.GetCustomAttributes()) ||
                    Constraints(method.GetGenericParameters()) ||
                    method.GetParameters().Any(handle => Attributes(reader.GetParameter(handle).GetCustomAttributes()));
                if (method.RelativeVirtualAddress == 0)
                    continue;
                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                if (!body.LocalSignature.IsNil)
                    uses |= reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(scope, null).Any(local => local);
                // A catch clause names its exception type outside the IL stream.
                uses |= body.ExceptionRegions.Any(region => region.Kind == ExceptionRegionKind.Catch && scope.Uses(reader, region.CatchType));
                uses |= UsesInIl(reader, scope, body.GetILBytes() ?? []);
            }
            if (uses)
                users[TypeName(reader, typeHandle)] = CompilerGenerated(reader, type);
        }
        return users.Select(user => (Name: user.Key, CompilerGenerated: user.Value)).ToArray();
    }

    /// <summary>The type carries [CompilerGenerated] (System.Runtime.CompilerServices).</summary>
    private static bool CompilerGenerated(MetadataReader reader, TypeDefinition type) =>
        type.GetCustomAttributes().Any(handle =>
        {
            var constructor = reader.GetCustomAttribute(handle).Constructor;
            if (constructor.Kind != HandleKind.MemberReference)
                return false;
            var parent = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference)
                return false;
            var reference = reader.GetTypeReference((TypeReferenceHandle)parent);
            return reader.GetString(reference.Namespace) == "System.Runtime.CompilerServices" &&
                reader.GetString(reference.Name) == "CompilerGeneratedAttribute";
        });

    private static bool Any(MethodSignature<bool> signature) => signature.ReturnType || signature.ParameterTypes.Any(parameter => parameter);

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    private static bool UsesInIl(MetadataReader reader, ScopeProvider scope, byte[] il)
    {
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
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                    if (scope.Uses(reader, MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset))))
                        return true;
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
                    offset += 4 + 4 * BitConverter.ToInt32(il, offset);
                    break;
                default:
                    offset += 4;
                    break;
            }
        }
        return false;
    }

    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var declaring = type.GetDeclaringType();
        return declaring.IsNil
            ? (reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." : "") + reader.GetString(type.Name)
            : TypeName(reader, declaring) + "+" + reader.GetString(type.Name);
    }

    /// <summary>Decodes signatures to "names a type whose resolution scope is the target assembly reference".</summary>
    private sealed class ScopeProvider(AssemblyReferenceHandle target) : ISignatureTypeProvider<bool, object?>
    {
        public bool Uses(MetadataReader reader, EntityHandle handle)
        {
            if (handle.IsNil)
                return false;
            switch (handle.Kind)
            {
                case HandleKind.TypeReference:
                    return FromScope(reader, (TypeReferenceHandle)handle);
                case HandleKind.TypeSpecification:
                    return reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null);
                case HandleKind.MemberReference:
                    var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    var parent = member.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeSpecification && Uses(reader, member.Parent);
                    return parent || (member.GetKind() == MemberReferenceKind.Method
                        ? Any(member.DecodeMethodSignature(this, null))
                        : member.DecodeFieldSignature(this, null));
                case HandleKind.MethodSpecification:
                    var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    return Uses(reader, specification.Method) || specification.DecodeSignature(this, null).Any(argument => argument);
                case HandleKind.StandaloneSignature:
                    var signature = reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                    return signature.GetKind() == StandaloneSignatureKind.Method && Any(signature.DecodeMethodSignature(this, null));
                default:
                    // Definitions in this assembly: their own signatures are scanned with their declaring type.
                    return false;
            }
        }

        private bool FromScope(MetadataReader reader, TypeReferenceHandle handle)
        {
            var scope = reader.GetTypeReference(handle).ResolutionScope;
            return scope.Kind switch
            {
                HandleKind.AssemblyReference => (AssemblyReferenceHandle)scope == target,
                HandleKind.TypeReference => FromScope(reader, (TypeReferenceHandle)scope),
                _ => false,
            };
        }

        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => FromScope(reader, handle);
        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => false;
        public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;
        public bool GetSZArrayType(bool elementType) => elementType;
        public bool GetArrayType(bool elementType, ArrayShape shape) => elementType;
        public bool GetByReferenceType(bool elementType) => elementType;
        public bool GetPointerType(bool elementType) => elementType;
        public bool GetPinnedType(bool elementType) => elementType;
        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) => genericType || typeArguments.Any(argument => argument);
        public bool GetGenericMethodParameter(object? genericContext, int index) => false;
        public bool GetGenericTypeParameter(object? genericContext, int index) => false;
        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => modifier || unmodifiedType;
        public bool GetFunctionPointerType(MethodSignature<bool> signature) => Any(signature);
    }

    private static bool IsBaseLibrary(string name) =>
        name is "System" or "netstandard" or "mscorlib" || name.StartsWith("System.", StringComparison.Ordinal);

    private sealed record TypeEntry(string Namespace, string Name, bool CompilerGenerated);

    private sealed class AssemblyMetadata
    {
        public AssemblyMetadata(string path)
        {
            Path = path;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            References = reader.AssemblyReferences
                .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Resources = reader.ManifestResources
                .Select(handle => reader.GetString(reader.GetManifestResource(handle).Name))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Types = reader.TypeDefinitions
                .Select(handle => reader.GetTypeDefinition(handle))
                .Where(type => type.GetDeclaringType().IsNil)
                .Select(type => new TypeEntry(
                    reader.GetString(type.Namespace),
                    reader.GetString(type.Name),
                    type.GetCustomAttributes().Any(attribute => AttributeName(reader, reader.GetCustomAttribute(attribute)) is
                        "System.Runtime.CompilerServices.CompilerGeneratedAttribute" or "Microsoft.CodeAnalysis.EmbeddedAttribute")))
                .ToArray();
        }

        public string Path { get; }
        public IReadOnlyList<string> References { get; }
        public IReadOnlyList<string> Resources { get; }
        public IReadOnlyList<TypeEntry> Types { get; }

        private static string AttributeName(MetadataReader reader, CustomAttribute attribute)
        {
            var parent = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default,
            };
            return parent.Kind switch
            {
                HandleKind.TypeReference => Name(reader.GetTypeReference((TypeReferenceHandle)parent).Namespace, reader.GetTypeReference((TypeReferenceHandle)parent).Name),
                HandleKind.TypeDefinition => Name(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Namespace, reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
                _ => "",
            };

            string Name(StringHandle ns, StringHandle name) => reader.GetString(ns) + "." + reader.GetString(name);
        }
    }
}
