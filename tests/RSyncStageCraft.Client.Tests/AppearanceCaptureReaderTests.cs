using System.Security.Cryptography;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The read-only capture reader over the approved IPC calls (fake IPC
/// and player lookup), and the planner's local-file constraints.
/// </summary>
public sealed class AppearanceCaptureReaderTests : IDisposable
{
    private static readonly AppearancePlayer Player = new(17, 0x1000, 0xABCDEF);
    private static readonly string State = Convert.ToBase64String(RandomNumberGenerator.GetBytes(300));
    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-appearance-reader", Guid.NewGuid().ToString("N"));

    public AppearanceCaptureReaderTests() => Directory.CreateDirectory(Path.Combine(root, "mods"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TheLocalPlayerIsLookedUpAndEveryCallUsesItsCurrentIndex()
    {
        var ipc = new FakeIpc();
        var dispatcher = new FrameworkDispatcher();
        var reader = new GameAppearanceCaptureReader(dispatcher, new FakeLocator(Player), ipc);

        var read = await reader.ReadCurrentCharacterAsync(CancellationToken.None);

        Assert.Equal(AppearanceReadStatus.Ok, read.Status);
        Assert.Equal(new ushort[] { 17 }, ipc.ResourceIndices);
        Assert.Equal(17, ipc.StateIndex);
        Assert.Equal(Player, read.Value!.Player);
        Assert.Equal(State, read.Value.GlamourerState);
        Assert.Equal(ipc.ModDirectory, read.Value.PenumbraModDirectory);
        // Only the approved calls, all on the framework thread.
        Assert.Equal(new[] { "PenumbraState", "GlamourerState", "GetModDirectory", "GetGameObjectResourcePaths", "GetStateBase64" }, ipc.Calls);
        Assert.True(ipc.AllOnFramework);
        Assert.Equal(1, dispatcher.Invocations);
        // The snapshot is RSC's own copy, not the IPC's collections.
        ipc.Paths![0]!.First().Value.Add("chara/x/added.tex");
        Assert.DoesNotContain("chara/x/added.tex", read.Value.Resources.SelectMany(entry => entry.GamePaths));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-object")]
    [InlineData("replaced-before-paths")]
    [InlineData("replaced-before-state")]
    [InlineData("replaced-after-state")]
    [InlineData("index-changed")]
    public async Task AMissingWrongOrChangingPlayerFailsWithoutAFallback(string scenario)
    {
        var ipc = new FakeIpc();
        var locator = new FakeLocator(scenario == "missing" ? null : Player);
        switch (scenario)
        {
            case "wrong-object":
                // The index resolves to another object.
                locator.StillAfter = 0;
                break;
            case "replaced-before-paths":
                locator.StillAfter = 1;
                break;
            case "replaced-before-state":
                locator.StillAfter = 2;
                break;
            case "replaced-after-state":
                locator.StillAfter = 3;
                break;
            case "index-changed":
                ipc.AfterResourcePaths = () => locator.Current = Player with { ObjectIndex = 18 };
                break;
        }
        var reader = new GameAppearanceCaptureReader(new FrameworkDispatcher(), locator, ipc);

        var read = await reader.ReadCurrentCharacterAsync(CancellationToken.None);

        Assert.Equal(AppearanceReadStatus.PlayerNotFound, read.Status);
        Assert.Null(read.Value);
        // Never any other index.
        Assert.All(ipc.ResourceIndices ?? [], index => Assert.Equal(17, index));
        Assert.True(ipc.StateIndex is null or 17);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("two")]
    [InlineData("null-entry")]
    [InlineData("null-game-paths")]
    public async Task AMismatchedOrNullResourceResultFailsClosed(string shape)
    {
        var ipc = new FakeIpc();
        ipc.Paths = shape switch
        {
            "null" => null,
            "empty" => [],
            "two" => [ipc.Paths![0], ipc.Paths[0]],
            "null-entry" => [null],
            "null-game-paths" => [new Dictionary<string, HashSet<string>> { [Path.Combine(root, "mods", "a.tex")] = null! }],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var reader = new GameAppearanceCaptureReader(new FrameworkDispatcher(), new FakeLocator(Player), ipc);
        Assert.Equal(AppearanceReadStatus.Failed, (await reader.ReadCurrentCharacterAsync(CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData(AppearanceApiState.Unavailable, AppearanceApiState.Ready)]
    [InlineData(AppearanceApiState.Incompatible, AppearanceApiState.Ready)]
    [InlineData(AppearanceApiState.Ready, AppearanceApiState.Unavailable)]
    [InlineData(AppearanceApiState.Ready, AppearanceApiState.Incompatible)]
    public async Task AnUnavailableOrIncompatibleApiMakesNoReadCall(AppearanceApiState penumbra, AppearanceApiState glamourer)
    {
        var ipc = new FakeIpc { Penumbra = penumbra, Glamourer = glamourer };
        var reader = new GameAppearanceCaptureReader(new FrameworkDispatcher(), new FakeLocator(Player), ipc);

        Assert.Equal(AppearanceReadStatus.Unavailable, (await reader.ReadCurrentCharacterAsync(CancellationToken.None)).Status);
        if (glamourer != AppearanceApiState.Ready)
        {
            Assert.Equal(AppearanceReadStatus.Unavailable, (await reader.ListDesignsAsync(CancellationToken.None)).Status);
            Assert.Equal(AppearanceReadStatus.Unavailable,
                (await reader.ReadDesignAsync(new AppearanceDesignEntry(Guid.NewGuid(), "x"), CancellationToken.None)).Status);
        }
        Assert.DoesNotContain(ipc.Calls, call => call.StartsWith("Get", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, AppearanceReadStatus.Failed)]                 // NothingDone
    [InlineData(2, AppearanceReadStatus.PlayerNotFound)]         // ActorNotFound
    [InlineData(3, AppearanceReadStatus.Failed)]                 // ActorNotHuman
    [InlineData(4, AppearanceReadStatus.Failed)]                 // DesignNotFound
    [InlineData(5, AppearanceReadStatus.Failed)]                 // ItemInvalid
    [InlineData(6, AppearanceReadStatus.StateLocked)]            // InvalidKey
    [InlineData(7, AppearanceReadStatus.Failed)]                 // InvalidState
    [InlineData(8, AppearanceReadStatus.Failed)]                 // CouldNotParse
    [InlineData(int.MaxValue, AppearanceReadStatus.Failed)]      // UnknownError
    [InlineData(-1, AppearanceReadStatus.Failed)]                // not a known code
    [InlineData(99, AppearanceReadStatus.Failed)]
    public async Task EveryGlamourerResultButSuccessIsAFailure(int code, AppearanceReadStatus expected)
    {
        var ipc = new FakeIpc { StateCode = code };
        var reader = new GameAppearanceCaptureReader(new FrameworkDispatcher(), new FakeLocator(Player), ipc);
        var read = await reader.ReadCurrentCharacterAsync(CancellationToken.None);
        Assert.Equal(expected, read.Status);
        Assert.Null(read.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("QUJD\n")]          // whitespace
    [InlineData("QUJ")]             // not a multiple of four
    [InlineData("QUI=QUJD")]        // padding in the middle
    [InlineData("QUJ=")]            // non-zero padding bits: not canonical
    [InlineData("-_-_")]            // URL alphabet
    public async Task MalformedOrNullGlamourerDataIsRefused(string? data)
    {
        var ipc = new FakeIpc { StateText = data, DesignText = data };
        var reader = new GameAppearanceCaptureReader(new FrameworkDispatcher(), new FakeLocator(Player), ipc);
        Assert.Equal(AppearanceReadStatus.InvalidData, (await reader.ReadCurrentCharacterAsync(CancellationToken.None)).Status);
        var design = await reader.ReadDesignAsync(new AppearanceDesignEntry(Guid.NewGuid(), "x"), CancellationToken.None);
        Assert.Equal(data is null ? AppearanceReadStatus.DesignNotFound : AppearanceReadStatus.InvalidData, design.Status);
    }

    [Fact]
    public async Task OversizedGlamourerDataIsRefusedAndTheLimitItselfIsAccepted()
    {
        var limit = GameAppearanceCaptureReader.MaximumGlamourerTextLength;
        var atLimit = Convert.ToBase64String(new byte[limit / 4 * 3]);
        Assert.Equal(limit, atLimit.Length);
        Assert.True(GameAppearanceCaptureReader.IsCanonicalBase64(atLimit));
        var over = Convert.ToBase64String(new byte[limit / 4 * 3 + 3]);
        var ipc = new FakeIpc { StateText = over, DesignText = over };
        var reader = new GameAppearanceCaptureReader(new FrameworkDispatcher(), new FakeLocator(Player), ipc);
        Assert.Equal(AppearanceReadStatus.InvalidData, (await reader.ReadCurrentCharacterAsync(CancellationToken.None)).Status);
        Assert.Equal(AppearanceReadStatus.InvalidData,
            (await reader.ReadDesignAsync(new AppearanceDesignEntry(Guid.NewGuid(), "x"), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task APluginLostAfterTheButtonWasShownIsStillRefusedAtTheClick()
    {
        // The Appearances buttons follow the last plugin status;
        // if a plugin goes away between that frame and the click, the reader
        // refuses on its own and makes no read call (the existing backstop).
        var id = Guid.NewGuid();
        var ipc = new FakeIpc { Designs = new() { [id] = "Evening" }, DesignText = State };
        var reader = new GameAppearanceCaptureReader(new FrameworkDispatcher(), new FakeLocator(Player), ipc);

        ipc.Penumbra = AppearanceApiState.Unavailable;
        Assert.Equal(AppearanceReadStatus.Unavailable, (await reader.ReadCurrentCharacterAsync(CancellationToken.None)).Status);
        Assert.DoesNotContain(ipc.Calls, call => call.StartsWith("Get", StringComparison.Ordinal));

        // Penumbra alone does not block saved designs while Glamourer is there.
        var list = await reader.ListDesignsAsync(CancellationToken.None);
        Assert.Equal(AppearanceReadStatus.Ok, list.Status);
        Assert.Equal(AppearanceReadStatus.Ok, (await reader.ReadDesignAsync(list.Value![0], CancellationToken.None)).Status);
        Assert.DoesNotContain(ipc.Calls, call => call is "GetModDirectory" or "GetGameObjectResourcePaths" or "GetStateBase64");

        ipc.Calls.Clear();
        ipc.Glamourer = AppearanceApiState.Unavailable;
        Assert.Equal(AppearanceReadStatus.Unavailable, (await reader.ListDesignsAsync(CancellationToken.None)).Status);
        Assert.Equal(AppearanceReadStatus.Unavailable, (await reader.ReadDesignAsync(list.Value[0], CancellationToken.None)).Status);
        Assert.DoesNotContain(ipc.Calls, call => call.StartsWith("Get", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SavedDesignsAreListedAndReadWithoutAnyPlayerCall()
    {
        var id = Guid.NewGuid();
        var ipc = new FakeIpc { Designs = new() { [id] = "Evening" }, DesignText = State };
        var locator = new FakeLocator(Player);
        var reader = new GameAppearanceCaptureReader(new FrameworkDispatcher(), locator, ipc);

        var list = await reader.ListDesignsAsync(CancellationToken.None);
        Assert.Equal(new AppearanceDesignEntry(id, "Evening"), Assert.Single(list.Value!));
        var design = await reader.ReadDesignAsync(list.Value![0], CancellationToken.None);
        Assert.Equal(AppearanceReadStatus.Ok, design.Status);
        Assert.Equal(AppearanceCaptureSource.SavedDesign, design.Value!.Source);
        Assert.Empty(design.Value.Resources);
        Assert.Null(design.Value.Player);
        Assert.Equal(new[] { "GlamourerState", "GetDesignList", "GlamourerState", "GetDesignBase64" }, ipc.Calls);
        Assert.Equal(0, locator.Lookups);

        // The design id and name never enter the bundle.
        using var plan = await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"), design.Value, Path.Combine(root, "work"));
        var manifest = System.Text.Encoding.UTF8.GetString(plan.Manifest!.CanonicalBytes);
        Assert.DoesNotContain(id.ToString(), manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(id.ToString("N"), manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Evening", manifest, StringComparison.Ordinal);

        ipc.Designs = null;
        Assert.Equal(AppearanceReadStatus.Failed, (await reader.ListDesignsAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task IpcExceptionsNamingLocalPathsBecomeStatusesOnly()
    {
        var secretPath = Path.Combine(root, "mods", "private folder", "secret.tex");
        foreach (var failing in new[] { "GetModDirectory", "GetGameObjectResourcePaths", "GetStateBase64", "GetDesignList", "GetDesignBase64", "PenumbraState" })
        {
            var ipc = new FakeIpc { Throw = failing, ThrowText = $"could not read {secretPath}" };
            var reader = new GameAppearanceCaptureReader(new FrameworkDispatcher(), new FakeLocator(Player), ipc);
            var results = new object[]
            {
                await reader.ReadCurrentCharacterAsync(CancellationToken.None),
                await reader.ListDesignsAsync(CancellationToken.None),
                await reader.ReadDesignAsync(new AppearanceDesignEntry(Guid.NewGuid(), "x"), CancellationToken.None),
            };
            foreach (var result in results)
                Assert.DoesNotContain(secretPath, result.ToString(), StringComparison.OrdinalIgnoreCase);
            var current = ((AppearanceReadResult<AppearanceCaptureRead>)results[0]).Status;
            var designs = ((AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>)results[1]).Status;
            var design = ((AppearanceReadResult<AppearanceCaptureRead>)results[2]).Status;
            Assert.Equal(failing is "GetModDirectory" or "GetGameObjectResourcePaths" or "GetStateBase64" or "PenumbraState"
                ? AppearanceReadStatus.Failed : AppearanceReadStatus.Ok, current);
            Assert.Equal(failing == "GetDesignList" ? AppearanceReadStatus.Failed : AppearanceReadStatus.Ok, designs);
            Assert.Equal(failing == "GetDesignBase64" ? AppearanceReadStatus.Failed : AppearanceReadStatus.Ok, design);
        }

        // While the plugin unloads (the dispatcher may run work inline on any
        // thread) no IPC call is made at all.
        var unloadingIpc = new FakeIpc();
        var whileUnloading = new GameAppearanceCaptureReader(new FrameworkDispatcher(), new FakeLocator(Player), unloadingIpc, () => true);
        Assert.Equal(AppearanceReadStatus.Unavailable, (await whileUnloading.ReadCurrentCharacterAsync(CancellationToken.None)).Status);
        Assert.Equal(AppearanceReadStatus.Unavailable, (await whileUnloading.ListDesignsAsync(CancellationToken.None)).Status);
        Assert.Empty(unloadingIpc.Calls);

        // A dispatcher that cannot run the call (unloading) is "unavailable", not an exception.
        var unloading = new GameAppearanceCaptureReader(new FrameworkDispatcher { Refuse = true }, new FakeLocator(Player), new FakeIpc());
        Assert.Equal(AppearanceReadStatus.Unavailable, (await unloading.ReadCurrentCharacterAsync(CancellationToken.None)).Status);
        Assert.Equal(AppearanceReadStatus.Unavailable, (await unloading.ListDesignsAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task OnlyFilesProvablyInsideTheModDirectoryWithAgreeingExtensionsAreCaptured()
    {
        var mods = Path.Combine(root, "mods");
        var inside = Write(Path.Combine(mods, "Pack", "body.tex"), RandomNumberGenerator.GetBytes(400));
        var outside = Write(Path.Combine(root, "elsewhere", "body.tex"), RandomNumberGenerator.GetBytes(400));
        var sibling = Write(Path.Combine(root, "mods-other", "body.tex"), RandomNumberGenerator.GetBytes(400));
        var mismatched = Write(Path.Combine(mods, "Pack", "really.mdl"), RandomNumberGenerator.GetBytes(400));
        var traversal = Path.Combine(mods, "Pack", "..", "..", "elsewhere", "body.tex");
        var read = new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, State,
        [
            new(inside, ["chara/x/inside.tex"]),
            new(outside, ["chara/x/outside.tex"]),
            new(sibling, ["chara/x/sibling.tex"]),
            new(traversal, ["chara/x/traversal.tex"]),
            new(mismatched, ["chara/x/mismatch.tex", "chara/x/match.mdl"]),
            new(mods + Path.DirectorySeparatorChar, ["chara/x/folder.tex"]),
        ], PenumbraModDirectory: mods + Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            read = read with { Resources = [.. read.Resources, new(@"\\?\" + inside, ["chara/x/device.tex"])] };

        using var plan = await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"), read, Path.Combine(root, "work"));

        Assert.Equal(new[] { "chara/x/inside.tex", "chara/x/match.mdl" }, plan.Manifest!.Files.Select(file => file.GamePath));
        var reasons = plan.Excluded.ToDictionary(item => item.GamePath, item => item.Reason);
        Assert.Equal(AppearanceCaptureExclusion.OutsideModDirectory, reasons["chara/x/outside.tex"]);
        Assert.Equal(AppearanceCaptureExclusion.OutsideModDirectory, reasons["chara/x/sibling.tex"]);
        Assert.Equal(AppearanceCaptureExclusion.OutsideModDirectory, reasons["chara/x/traversal.tex"]);
        Assert.Equal(AppearanceCaptureExclusion.OutsideModDirectory, reasons["chara/x/folder.tex"]);
        if (OperatingSystem.IsWindows())
            Assert.Equal(AppearanceCaptureExclusion.OutsideModDirectory, reasons["chara/x/device.tex"]);
        Assert.Equal(AppearanceCaptureExclusion.ExtensionMismatch, reasons["chara/x/mismatch.tex"]);
        Assert.DoesNotContain(plan.Sources.Values, path => path.Contains("elsewhere", StringComparison.Ordinal));

        // No mod directory (or a relative one): nothing can be proven inside it.
        foreach (var unknown in new[] { null, "", "mods", "relative" + Path.DirectorySeparatorChar + "mods" })
        {
            using var none = await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"), read with { PenumbraModDirectory = unknown }, Path.Combine(root, "work"));
            Assert.Empty(none.Manifest!.Files);
            Assert.Contains(none.Excluded, item => item.Reason == AppearanceCaptureExclusion.OutsideModDirectory);
        }

        // A link inside the mod folder that points elsewhere cannot prove containment.
        var link = Path.Combine(mods, "Linked");
        if (TryLink(link, Path.Combine(root, "elsewhere")))
        {
            using var linked = await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"),
                read with { Resources = [new(Path.Combine(link, "body.tex"), ["chara/x/linked.tex"])] }, Path.Combine(root, "work"));
            Assert.Empty(linked.Manifest!.Files);
            Assert.Equal(AppearanceCaptureExclusion.OutsideModDirectory, Assert.Single(linked.Excluded).Reason);
        }
    }

    [Fact]
    public async Task AGamePathWithConflictingSourcesRefusesTheCapture()
    {
        var mods = Path.Combine(root, "mods");
        var content = RandomNumberGenerator.GetBytes(300);
        var first = Write(Path.Combine(mods, "a", "skin.tex"), content);
        var same = Write(Path.Combine(mods, "b", "skin.tex"), content);
        var outside = Write(Path.Combine(root, "outside", "skin.tex"), content);
        async Task<AppearanceCapturePlan> Plan(params AppearanceResourceEntry[] resources) =>
            await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"),
                new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, State, resources, PenumbraModDirectory: mods), Path.Combine(root, "work"));

        // Identical local files: one entry.
        using (var plan = await Plan(new AppearanceResourceEntry(first, ["chara/x/skin.tex"]), new AppearanceResourceEntry(same, ["chara/x/skin.tex"])))
            Assert.Equal(1, plan.FileCount);

        // A local redirect and a vanilla or swapped source for the same game path.
        using (var plan = await Plan(new AppearanceResourceEntry(first, ["chara/x/skin.tex"]), new AppearanceResourceEntry("chara/x/skin.tex", ["chara/x/skin.tex"])))
            Assert.Equal(("chara/x/skin.tex", AppearanceCaptureRefusal.ConflictingGamePath), (plan.ConflictingGamePath, plan.Refusal));
        using (var plan = await Plan(new AppearanceResourceEntry(first, ["chara/x/skin.tex"]), new AppearanceResourceEntry("chara/y/other.tex", ["chara/x/skin.tex"])))
            Assert.Equal(AppearanceCaptureRefusal.ConflictingGamePath, plan.Refusal);

        // An included file and an excluded one (outside the mod folder) for the same game path.
        using (var plan = await Plan(new AppearanceResourceEntry(first, ["chara/x/skin.tex"]), new AppearanceResourceEntry(outside, ["chara/x/skin.tex"])))
        {
            Assert.Equal(AppearanceCaptureRefusal.ConflictingGamePath, plan.Refusal);
            Assert.Null(plan.Manifest);
        }
    }

    [Fact]
    public async Task TheSnapshotFingerprintChangesWithThePlayerMappingOrState()
    {
        var file = Write(Path.Combine(root, "mods", "body.tex"), RandomNumberGenerator.GetBytes(100));
        var read = new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, State,
            [new(file, ["chara/x/body.tex"])], PenumbraModDirectory: Path.Combine(root, "mods"), Player: Player);
        using var plan = await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"), read, Path.Combine(root, "work"));
        var same = read with { Resources = [new(file, new HashSet<string> { "chara/x/body.tex" })] };
        Assert.Equal(plan.Fingerprint, AppearanceCapturePlanner.Fingerprint(same));
        foreach (var changed in new[]
        {
            read with { Player = Player with { ObjectIndex = 18 } },
            read with { Player = Player with { Address = 0x2000 } },
            read with { GlamourerState = Convert.ToBase64String(new byte[3]) },
            read with { Resources = [new(file, ["chara/x/body.tex", "chara/x/other.tex"])] },
            read with { PenumbraModDirectory = root },
        })
            Assert.NotEqual(plan.Fingerprint, AppearanceCapturePlanner.Fingerprint(changed));
    }

    private static string Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating links may need a privilege on Windows; the containment
            // rule for links is then covered on Linux.
            return false;
        }
    }

    /// <summary>Runs the call inline, marking it as on the framework thread.</summary>
    private sealed class FrameworkDispatcher : IGameThreadDispatcher
    {
        [ThreadStatic]
        public static bool OnFramework;

        public int Invocations { get; private set; }
        public bool Refuse { get; init; }

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            if (Refuse)
                return Task.FromException<T>(new InvalidOperationException("unloading"));
            Invocations++;
            OnFramework = true;
            try
            {
                return Task.FromResult(operation());
            }
            finally
            {
                OnFramework = false;
            }
        }
    }

    private sealed class FakeLocator(AppearancePlayer? player) : IAppearancePlayerLocator
    {
        private int checks;

        public AppearancePlayer? Current { get; set; } = player;

        /// <summary>IsStill answers true this many times, then false (the object was replaced).</summary>
        public int StillAfter { get; set; } = int.MaxValue;

        public int Lookups { get; private set; }

        public AppearancePlayer? LocalPlayer()
        {
            Lookups++;
            return Current;
        }

        public bool IsStill(AppearancePlayer candidate) => checks++ < StillAfter && Current == candidate;
    }

    private sealed class FakeIpc : IAppearanceIpc
    {
        public FakeIpc()
        {
            ModDirectory = Path.Combine(Path.GetTempPath(), "mods");
            Paths = [new Dictionary<string, HashSet<string>> { [Path.Combine(ModDirectory, "a.tex")] = ["chara/x/a.tex"] }];
        }

        public AppearanceApiState Penumbra { get; set; } = AppearanceApiState.Ready;
        public AppearanceApiState Glamourer { get; set; } = AppearanceApiState.Ready;
        public string? ModDirectory { get; }
        public Dictionary<string, HashSet<string>>?[]? Paths { get; set; }
        public int StateCode { get; init; }
        public string? StateText { get; init; } = State;
        public string? DesignText { get; init; } = State;
        public Dictionary<Guid, string>? Designs { get; set; } = new();
        public string? Throw { get; init; }
        public string ThrowText { get; init; } = "failed";
        public Action? AfterResourcePaths { get; set; }
        public List<string> Calls { get; } = [];
        public bool AllOnFramework { get; private set; } = true;
        public ushort[]? ResourceIndices { get; private set; }
        public int? StateIndex { get; private set; }

        public AppearanceApiState PenumbraState() => Call(nameof(PenumbraState), () => Penumbra);
        public AppearanceApiState GlamourerState() => Call(nameof(GlamourerState), () => Glamourer);
        public string? GetModDirectory() => Call(nameof(GetModDirectory), () => ModDirectory);

        public Dictionary<string, HashSet<string>>?[]? GetGameObjectResourcePaths(ushort[] gameObjectIndices) =>
            Call(nameof(GetGameObjectResourcePaths), () =>
            {
                ResourceIndices = gameObjectIndices;
                AfterResourcePaths?.Invoke();
                return Paths;
            });

        public (int Code, string? State) GetStateBase64(int objectIndex) =>
            Call(nameof(GetStateBase64), () =>
            {
                StateIndex = objectIndex;
                return (StateCode, StateText);
            });

        public Dictionary<Guid, string>? GetDesignList() => Call(nameof(GetDesignList), () => Designs);
        public string? GetDesignBase64(Guid designId) => Call(nameof(GetDesignBase64), () => DesignText);

        private T Call<T>(string name, Func<T> result)
        {
            Calls.Add(name);
            AllOnFramework &= FrameworkDispatcher.OnFramework;
            if (Throw == name)
                throw new IOException(ThrowText, new UnauthorizedAccessException(ThrowText));
            return result();
        }
    }
}
