using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>
/// SD-17(a): this Protocol against its checked-in protocol-7 golden fixtures
/// (<see cref="Protocol7GoldenFixtures"/>). Any missing, extra or changed
/// file, any byte difference, any decode that does not re-encode exactly, and
/// any malformed fixture that is accepted fails.
/// </summary>
public sealed class Protocol7GoldenFixtureTests
{
    [Fact]
    public void TheCorpusCoversEveryMessageTypeTheHumanKindsAndSceneFormatTwo()
    {
        var frames = Protocol7GoldenFixtures.Frames();
        Assert.Equal(Enum.GetValues<ObjectProtocolMessageType>().Order(), frames.Select(frame => frame.Type).Distinct().Order());
        Assert.Contains(frames, frame => frame.Payload is CreateObjectRequest { Payload: HumanActorCreatePayload });
        Assert.Contains(frames, frame => frame.Payload is UpdateObjectRequest { Patch: HumanTransformPatch });
        Assert.Contains(frames, frame => frame.Payload is ObjectCreatedMessage { State: HumanActorState });
        Assert.Contains(frames, frame => frame.Payload is ObjectDeletedMessage { Tombstone.Kind: RscObjectKind.Human });
        Assert.Contains(frames, frame => frame.Payload is ObjectSnapshotMessage snapshot && snapshot.Objects.Any(state => state is HumanActorState));
        Assert.Contains(frames, frame => frame.Payload is SceneReadCompletion { Document: { FormatVersion: 2 } document } &&
            document.Objects.Any(record => record.Kind == RscObjectKind.Human));
        Assert.Equal(7, ProtocolConstants.CurrentVersion);
        Assert.Equal(2, Core.Scenes.SceneFormat.CurrentVersion);
        Assert.Equal(3, AppearanceLimits.HumanPackageFormatVersion);
    }

    [Fact]
    public void TheCorpusIsDeterministicAndEveryValidFrameRoundTripsAndEveryMalformedOneIsRefused()
    {
        Assert.Equal(Protocol7GoldenFixtures.Hashes(Protocol7GoldenFixtures.Valid().Concat(Protocol7GoldenFixtures.Malformed())),
            Protocol7GoldenFixtures.Hashes(Protocol7GoldenFixtures.Valid().Concat(Protocol7GoldenFixtures.Malformed())));
        Assert.Equal(Protocol7GoldenFixtures.ValidCount, Protocol7GoldenFixtures.Valid().Count);
        Assert.Equal(Protocol7GoldenFixtures.MalformedCount, Protocol7GoldenFixtures.Malformed().Count);
        foreach (var (path, bytes) in Protocol7GoldenFixtures.Valid())
            Assert.True(Reads(path, bytes, out var problem), $"{path}: {problem}");
        foreach (var (path, bytes) in Protocol7GoldenFixtures.Malformed())
            Assert.False(Reads(path, bytes, out _), $"{path} was accepted");
    }

    [Fact]
    public void TheCheckedInFixturesAreExactlyTheCorpus()
    {
        var folder = Protocol7GoldenFixtures.Folder();
        var corpus = new SortedDictionary<string, byte[]>(Protocol7GoldenFixtures.Valid(), StringComparer.Ordinal);
        foreach (var (path, bytes) in Protocol7GoldenFixtures.Malformed())
            corpus.Add(path, bytes);
        var hashes = Protocol7GoldenFixtures.Hashes(corpus);

        if (Environment.GetEnvironmentVariable(Protocol7GoldenFixtures.EmitVariable) is { Length: > 0 } target)
        {
            // The emission step only (never in CI): writes the corpus and its hashes, then the checks below run on what was written.
            foreach (var (path, bytes) in corpus)
            {
                var file = Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
            }
            File.WriteAllText(Path.Combine(target, Protocol7GoldenFixtures.HashFile), hashes, new UTF8Encoding(false));
            folder = target;
        }

        Assert.True(Directory.Exists(folder), "The protocol-7 golden fixtures are not checked in; run the emission step.");
        Assert.Equal(hashes, File.ReadAllText(Path.Combine(folder, Protocol7GoldenFixtures.HashFile), Encoding.UTF8));
        var onDisk = Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(corpus.Keys.ToArray(), onDisk);
        foreach (var (path, bytes) in corpus)
            Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(folder, path))), $"{path} differs from the corpus");
    }

    /// <summary>A fixture is read when it decodes, validates and re-encodes to exactly its own bytes.</summary>
    internal static bool Reads(string path, byte[] bytes, out string? problem)
    {
        problem = null;
        if (path.Contains("manifest-", StringComparison.Ordinal))
        {
            var manifest = AppearanceBundleManifest.TryParse(bytes, AppearanceManifestReadOptions.AllowHumanPackages, out var error);
            if (manifest is null)
            {
                problem = error;
                return false;
            }
            if (manifest.FormatVersion != AppearanceLimits.HumanPackageFormatVersion || !manifest.CanonicalBytes.AsSpan().SequenceEqual(bytes))
            {
                problem = "not the canonical format-3 bytes";
                return false;
            }
            return true;
        }
        var codec = new ObjectControlMessageCodec();
        if (!codec.TryDecode(bytes, out var message, out var decodeError) || message is null)
        {
            problem = decodeError.ToString();
            return false;
        }
        if (!codec.TryEncode(message.Envelope, message.Payload, out var again, out var encodeError))
        {
            problem = encodeError.ToString();
            return false;
        }
        if (!again.AsSpan().SequenceEqual(bytes))
        {
            problem = "re-encoded to other bytes";
            return false;
        }
        return true;
    }
}
