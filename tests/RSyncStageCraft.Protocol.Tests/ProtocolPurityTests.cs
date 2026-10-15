using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The protocol project is MIT-licensed and shared with other programs, so it
/// stays pure: no project or package reference, nothing but the .NET base
/// library, every compiled source inside its own folder, and only the reviewed
/// files (with its LICENSE) in that folder.
/// </summary>
public sealed class ProtocolPurityTests
{
    private const string ProjectFolder = "src/RSyncStageCraft.Protocol";

    // The reviewed file list. A new file is added here in the same change.
    private static readonly string[] Files =
    [
        "Actors/ActorAnimationAllowlist.cs",
        "Appearance/AppearanceBlobEnvelope.cs",
        "Appearance/AppearanceBundle.cs",
        "Authentication/AccessKeyCredentialFormat.cs",
        "Authentication/AuthenticationIoOperation.cs",
        "Authentication/AuthenticationMessageCodec.cs",
        "LICENSE",
        "Protocol/ActorContracts.cs",
        "Protocol/ControlResponseMessages.cs",
        "Protocol/FurnitureIdentity.cs",
        "Protocol/LightContracts.cs",
        "Protocol/ObjectControlMessageCodec.cs",
        "Protocol/ObjectPatchContracts.cs",
        "Protocol/ObjectProtocolMessages.cs",
        "Protocol/ObjectStateContracts.cs",
        "Protocol/ProtocolConstants.cs",
        "Protocol/ProtocolLimits.cs",
        "Protocol/ProtocolMessages.cs",
        "Protocol/SceneLocation.cs",
        "Protocol/WebSocketControlMessageReader.cs",
        "RSyncStageCraft.Protocol.csproj",
        "Scenes/SceneDocument.cs",
        "packages.lock.json",
    ];

    [Fact]
    public void TheProjectReferencesNoProjectAndNoPackage()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot(), ProjectFolder, "RSyncStageCraft.Protocol.csproj"));
        var references = project.Descendants()
            .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference" or "Reference" or "FrameworkReference")
            .Select(element => element.Name.LocalName + " " + (string?)element.Attribute("Include"));
        Assert.Empty(references);
        Assert.Equal("Microsoft.NET.Sdk", (string?)project.Root!.Attribute("Sdk"));
    }

    [Fact]
    public void TheAssemblyReferencesOnlyTheBaseLibraryAndHasNoResources()
    {
        using var stream = File.OpenRead(typeof(ProtocolConstants).Assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToArray();
        Assert.NotEmpty(references);
        Assert.All(references, name => Assert.True(
            name is "System" or "netstandard" or "mscorlib" || name.StartsWith("System.", StringComparison.Ordinal),
            $"unexpected assembly reference {name}"));
        Assert.Empty(metadata.ManifestResources);
    }

    [Fact]
    public void EveryCompiledSourceLiesInsideTheProjectFolder()
    {
        var pdb = Path.ChangeExtension(typeof(ProtocolConstants).Assembly.Location, ".pdb");
        using var stream = File.OpenRead(pdb);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var metadata = provider.GetMetadataReader();
        var documents = metadata.Documents.Select(handle => metadata.GetString(metadata.GetDocument(handle).Name)).ToArray();
        Assert.NotEmpty(documents);
        // The build maps the repository root to /_/ (Directory.Build.props).
        Assert.All(documents, document => Assert.StartsWith("/_/" + ProjectFolder + "/", document, StringComparison.Ordinal));
    }

    [Fact]
    public void TheFolderHoldsOnlyTheReviewedFilesAndItsLicence()
    {
        var folder = Path.Combine(RepositoryRoot(), ProjectFolder);
        var present = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(folder, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("bin/", StringComparison.Ordinal) && !path.StartsWith("obj/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Files.Order(StringComparer.Ordinal), present);
        Assert.StartsWith("MIT License", File.ReadAllText(Path.Combine(folder, "LICENSE")), StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RSyncStageCraft-Client.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
