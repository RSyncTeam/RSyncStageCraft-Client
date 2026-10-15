#nullable enable
using System.Reflection.Metadata;
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
            IsBaseLibrary(name) || name == "Dalamud" || name.StartsWith("Dalamud.", StringComparison.Ordinal) || PluginReferences.Contains(name),
            $"unexpected assembly reference {name}"));
        Assert.Contains("RSyncStageCraft.Client", Plugin.References);
        Assert.Contains("RSyncStageCraft.Protocol", Plugin.References);
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
