using System.Net;
using System.Security.Cryptography;
using System.Text;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

/// <summary>
/// "Make a Copy": the name rules, the text checks, the outcome rules after
/// AddDesign, and the copier against an in-memory appearance service with a
/// fake Glamourer (no Glamourer runs in CI).
/// </summary>
public sealed class AppearanceCopyTests : IDisposable
{
    private const int Limit = AppearanceCopyRules.MaximumTextBytes;
    private static readonly ContentHash SomeBundle = ContentHash.Of("bundle"u8);
    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-appearance-copy", Guid.NewGuid().ToString("N"));

    public AppearanceCopyTests() => Directory.CreateDirectory(root);

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

    // ---- 1. Name builder ---------------------------------------------------

    [Fact]
    public void EntryNamesAreMadeSafeForGlamourersDesignTree()
    {
        var copy = "Copy " + SomeBundle.Value[..8];
        var cases = new (string? Input, string Expected)[]
        {
            ("Coat/Red\\Blue", "Coat-Red-Blue"),
            ("  Dress \t ", "Dress"),
            // Control, format (zero width, bidi override), line and paragraph separators.
            ("A\u0000B\u200BC\u202ED\u2028E\u2029F\u0007", "ABCDEF"),
            // Lone surrogates are removed; a real pair is kept.
            ("A\uD800B\uDC00C", "ABC"),
            ("A\U0001F600B", "A\U0001F600B"),
            // Every trailing "( … )" Glamourer could take for its duplicate marker.
            ("Dress (3)", "Dress [3]"),
            ("Dress ( 3)", "Dress [ 3]"),
            ("Dress (+3)", "Dress [+3]"),
            ("Dress (3 )", "Dress [3 ]"),
            ("Dress (x)", "Dress [x]"),
            ("Dress(3)", "Dress(3)"),
            ("Dress (3) blue", "Dress (3) blue"),
            // Luna's empty-name sentinel.
            ("<None>", "[None]"),
            ("<none>", "[None]"),
            // Empty after cleaning.
            ("", copy),
            (null, copy),
            ("   ", copy),
            ("\u200B\u0001", copy),
            ("//", "--"),
        };
        foreach (var (input, expected) in cases)
            Assert.Equal(expected, AppearanceCopyNames.Clean(input, SomeBundle));

        // 64 UTF-8 bytes, cut at whole characters (one, two and four bytes).
        Assert.Equal(new string('a', 64), AppearanceCopyNames.Clean(new string('a', 70), SomeBundle));
        Assert.Equal(string.Concat(Enumerable.Repeat("é", 32)), AppearanceCopyNames.Clean(string.Concat(Enumerable.Repeat("é", 40)), SomeBundle));
        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F600", 16)), AppearanceCopyNames.Clean(string.Concat(Enumerable.Repeat("\U0001F600", 20)), SomeBundle));
        var mixed = AppearanceCopyNames.Clean("a" + string.Concat(Enumerable.Repeat("\U0001F600", 16)), SomeBundle);
        Assert.Equal("a" + string.Concat(Enumerable.Repeat("\U0001F600", 15)), mixed);
        Assert.Equal(61, Encoding.UTF8.GetByteCount(mixed));

        // The cut itself would leave a trailing " (12)": it is escaped after the cut.
        var cut = AppearanceCopyNames.Clean(new string('x', 59) + " (12)xyz", SomeBundle);
        Assert.Equal(new string('x', 59) + " [12]", cut);
        Assert.Equal(64, Encoding.UTF8.GetByteCount(cut));
    }

    // ---- 2. Uniqueness -----------------------------------------------------

    [Fact]
    public void ATakenNameGetsTheNextFreeSuffixWithinSixtyFourBytes()
    {
        Assert.Equal("Dress", AppearanceCopyNames.Choose("Dress", []));
        // Taken by another design's name anywhere (ignoring case), by a design in
        // the folder, or by a sub-folder of that name.
        Assert.Equal("Dress #2", AppearanceCopyNames.Choose("Dress", [Design("dress", "Other/dress")]));
        Assert.Equal("Dress #2", AppearanceCopyNames.Choose("Dress", [Design("Renamed", "rsyncstagecraft/DRESS")]));
        Assert.Equal("Dress #2", AppearanceCopyNames.Choose("Dress", [Design("Inner", "RSyncStageCraft/Dress/Inner")]));
        Assert.Equal("Dress #3", AppearanceCopyNames.Choose("Dress", [Design("Dress", "RSyncStageCraft/Dress"), Design("Dress #2", "RSyncStageCraft/Dress #2")]));
        // A similar name elsewhere does not count.
        Assert.Equal("Dress", AppearanceCopyNames.Choose("Dress", [Design("Dress 2", "RSyncStageCraft/Dress 2"), Design("Other", "RSyncStageCraft/Dressy/x")]));

        var full = new string('a', 64);
        var suffixed = AppearanceCopyNames.Choose(full, [Design(full, "RSyncStageCraft/" + full)])!;
        Assert.Equal(new string('a', 61) + " #2", suffixed);
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 16));
        var emojiSuffixed = AppearanceCopyNames.Choose(emoji, [Design(emoji, "RSyncStageCraft/" + emoji)])!;
        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F600", 15)) + " #2", emojiSuffixed);
        Assert.True(Encoding.UTF8.GetByteCount(emojiSuffixed) <= AppearanceCopyNames.MaximumNameBytes);
        // Whole characters only: the strict encoder accepts it.
        new UTF8Encoding(false, true).GetBytes(emojiSuffixed);

        var everything = new List<GlamourerDesignListing> { Design("N", "RSyncStageCraft/N") };
        for (var suffix = 2; suffix <= AppearanceCopyNames.MaximumSuffix; suffix++)
            everything.Add(Design($"N #{suffix}", $"RSyncStageCraft/N #{suffix}"));
        Assert.Null(AppearanceCopyNames.Choose("N", everything));
    }

    // ---- 3. Folder blocked --------------------------------------------------

    [Fact]
    public async Task ADesignInThePlaceOfTheFolderRefusesTheCopyWithoutAddDesign()
    {
        Assert.True(AppearanceCopyNames.FolderBlocked([Design("RSyncStageCraft", "rsyncstagecraft")]));
        Assert.False(AppearanceCopyNames.FolderBlocked([Design("RSyncStageCraft", "Mine/RSyncStageCraft"), Design("x", "RSyncStageCraft/x")]));

        using var group = new Group(this);
        var entry = await group.PublishAsync("state text");
        group.Glamourer.Designs.Add(Design("RSyncStageCraft", "RSyncStageCraft"));
        var outcome = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal(AppearanceCopyStatus.FolderBlocked, outcome.Status);
        Assert.Empty(group.Glamourer.Adds);
    }


    // ---- 4. The outcome after AddDesign -------------------------------------

    [Fact]
    public void OnlyTheReturnedIdConfirmsACreationAndNamesAreNeverEvidence()
    {
        var other = Design("Other", "Other");
        var before = new[] { other };
        var id = Guid.NewGuid();
        var expected = new GlamourerDesignListing(id, "Dress", "RSyncStageCraft/Dress");
        var success = new GlamourerAddAnswer(GlamourerCallOutcome.Answered, AppearanceCopyRules.GlamourerSuccess, id);

        var created = AppearanceCopyRules.Classify(success, "Dress", before, [other, expected]);
        Assert.Equal((AppearanceCopyStatus.Created, "RSyncStageCraft/Dress", (Guid?)id), (created.Status, created.FullPath, created.DesignId));
        Assert.Empty(created.Inspect);
        // The folder may differ in case (Glamourer reuses an existing folder).
        Assert.Equal(AppearanceCopyStatus.Created,
            AppearanceCopyRules.Classify(success, "Dress", before, [expected with { FullPath = "rsyncstagecraft/Dress" }]).Status);
        var elsewhere = AppearanceCopyRules.Classify(success, "Dress", before, [expected with { FullPath = "Dress" }]);
        Assert.Equal((AppearanceCopyStatus.CreatedElsewhere, "Dress"), (elsewhere.Status, elsewhere.FullPath));
        // A node Glamourer renamed is not the expected path either.
        Assert.Equal(AppearanceCopyStatus.CreatedElsewhere,
            AppearanceCopyRules.Classify(success, "Dress", before, [expected with { FullPath = "RSyncStageCraft/Dress (2)" }]).Status);
        // Success whose ID is missing afterwards (or no readable list): not confirmed.
        foreach (var missing in new[] { AppearanceCopyRules.Classify(success, "Dress", before, null), AppearanceCopyRules.Classify(success, "Dress", before, [other]) })
        {
            Assert.Equal((AppearanceCopyStatus.Uncertain, (Guid?)null, true), (missing.Status, missing.DesignId, missing.ReportedSuccess));
            Assert.False(missing.CreatedDesign);
        }

        // A returned ID that was already there before the call is unusable.
        var reused = AppearanceCopyRules.Classify(success, "Dress", [other, expected], [other, expected]);
        Assert.Equal((AppearanceCopyStatus.Uncertain, (Guid?)null), (reused.Status, reused.DesignId));

        // UnknownError (Glamourer 1.7.1.3 returns it with an empty ID), an
        // unknown code, Success without an ID, an exception: always uncertain,
        // even when exactly one new design with the requested name appears. That
        // design is only listed for the user to inspect, never claimed.
        foreach (var answer in new[]
        {
            new GlamourerAddAnswer(GlamourerCallOutcome.Answered, int.MaxValue, Guid.Empty),
            new GlamourerAddAnswer(GlamourerCallOutcome.Answered, int.MaxValue, id),
            new GlamourerAddAnswer(GlamourerCallOutcome.Answered, 42, id),
            new GlamourerAddAnswer(GlamourerCallOutcome.Answered, AppearanceCopyRules.GlamourerSuccess, Guid.Empty),
            new GlamourerAddAnswer(GlamourerCallOutcome.Threw, 0, Guid.Empty),
        })
        {
            var single = AppearanceCopyRules.Classify(answer, "Dress", before, [other, expected]);
            Assert.Equal((AppearanceCopyStatus.Uncertain, (Guid?)null, (string?)null), (single.Status, single.DesignId, single.FullPath));
            Assert.Equal(new[] { "RSyncStageCraft/Dress" }, single.Inspect);
            var unplaced = AppearanceCopyRules.Classify(answer, "Dress", before, [other, expected with { FullPath = "" }]);
            Assert.Equal((AppearanceCopyStatus.Uncertain, (Guid?)null), (unplaced.Status, unplaced.DesignId));
            Assert.Equal(new[] { "" }, unplaced.Inspect);
            var two = AppearanceCopyRules.Classify(answer, "Dress", before, [other, expected, expected with { Id = Guid.NewGuid(), FullPath = "Elsewhere/Dress" }]);
            Assert.Equal(AppearanceCopyStatus.Uncertain, two.Status);
            Assert.Equal(2, two.Inspect.Count);
            var unrelated = AppearanceCopyRules.Classify(answer, "Dress", before, [other, Design("Hat", "Hat")]);
            Assert.Equal((AppearanceCopyStatus.Uncertain, (Guid?)null), (unrelated.Status, unrelated.DesignId));
            Assert.Empty(unrelated.Inspect);
            Assert.Equal(AppearanceCopyStatus.Uncertain, AppearanceCopyRules.Classify(answer, "Dress", before, [other]).Status);
            var unreadable = AppearanceCopyRules.Classify(answer, "Dress", before, null);
            Assert.Equal(AppearanceCopyStatus.Uncertain, unreadable.Status);
            Assert.Empty(unreadable.Inspect);
            // A design that already existed is never listed as new.
            Assert.Empty(AppearanceCopyRules.Classify(answer, "Dress", [other, expected], [other, expected]).Inspect);
        }

        Assert.Equal(AppearanceCopyStatus.CouldNotParse, AppearanceCopyRules.Classify(
            new GlamourerAddAnswer(GlamourerCallOutcome.Answered, AppearanceCopyRules.GlamourerCouldNotParse, Guid.Empty), "Dress", before, [other]).Status);
        Assert.Equal(AppearanceCopyStatus.GlamourerUnavailable, AppearanceCopyRules.Classify(
            new GlamourerAddAnswer(GlamourerCallOutcome.Unavailable, 0, Guid.Empty), "Dress", before, [other]).Status);
    }

    [Fact]
    public async Task AnUnrelatedDesignWithTheRequestedNameAppearingDuringTheCallIsNeverClaimed()
    {
        using var group = new Group(this);
        var entry = await group.PublishAsync("state text");
        var glamourer = group.Glamourer;

        // During the call someone else creates "RSyncStageCraft/Evening look";
        // Glamourer then answers UnknownError (with no ID, as 1.7.1.3 does).
        Guid foreign = Guid.Empty;
        glamourer.Add = (text, name) =>
        {
            foreign = glamourer.Create(name);
            return new GlamourerAddAnswer(GlamourerCallOutcome.Answered, int.MaxValue, Guid.Empty);
        };
        var unknown = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal(AppearanceCopyStatus.Uncertain, unknown.Status);
        Assert.Null(unknown.DesignId);
        Assert.Null(unknown.FullPath);
        Assert.False(unknown.CreatedDesign);
        Assert.Equal(new[] { "RSyncStageCraft/Evening look" }, unknown.Inspect);
        Assert.Contains("RSC does not claim them", AppearanceCaptureText.CopyResult(unknown), StringComparison.Ordinal);
        Assert.DoesNotContain(foreign.ToString("N")[..8], AppearanceCaptureText.CopyResult(unknown), StringComparison.Ordinal);
        Assert.DoesNotContain(group.Log, line => line.Contains(foreign.ToString(), StringComparison.Ordinal));
        Assert.Single(glamourer.Adds);

        // The same with an exception instead of an answer.
        glamourer.Add = (text, name) =>
        {
            glamourer.Create(name);
            throw new InvalidOperationException("ipc");
        };
        var threw = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal((AppearanceCopyStatus.Uncertain, (Guid?)null), (threw.Status, threw.DesignId));
        Assert.Single(threw.Inspect);

        // Glamourer succeeds with RSC's own ID while an unrelated design with
        // the same name also appears: only the returned ID is claimed.
        Guid other = Guid.Empty;
        glamourer.Add = (text, name) =>
        {
            other = glamourer.Create(name);
            return new GlamourerAddAnswer(GlamourerCallOutcome.Answered, AppearanceCopyRules.GlamourerSuccess, glamourer.Create(name));
        };
        var created = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal(AppearanceCopyStatus.Created, created.Status);
        Assert.NotEqual(other, created.DesignId);
        Assert.Contains(glamourer.Designs, design => design.Id == created.DesignId);
        Assert.Empty(created.Inspect);

        // Success naming a design that existed before the call: unusable, uncertain.
        var existing = glamourer.Designs[0].Id;
        glamourer.Add = (text, name) => new GlamourerAddAnswer(GlamourerCallOutcome.Answered, AppearanceCopyRules.GlamourerSuccess, existing);
        var reused = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal((AppearanceCopyStatus.Uncertain, (Guid?)null), (reused.Status, reused.DesignId));

        // Never retried: one call per attempt, and nothing was removed or renamed.
        Assert.Equal(4, glamourer.Adds.Count);
        Assert.Contains(glamourer.Designs, design => design.Id == foreign && design.FullPath == "RSyncStageCraft/Evening look");
    }

    // ---- 5. Design-list failures --------------------------------------------

    [Fact]
    public async Task AnUnreadableDesignListRefusesBeforeCreationAndLeavesAPlacementUnverifiedAfter()
    {
        using var group = new Group(this);
        var entry = await group.PublishAsync("state text");

        group.Glamourer.List = call => null;
        Assert.Equal(AppearanceCopyStatus.DesignListFailed, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        group.Glamourer.List = call => throw new InvalidOperationException("list");
        Assert.Equal(AppearanceCopyStatus.DesignListFailed, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Empty(group.Glamourer.Adds);

        // After a reported success, an unreadable list leaves it unconfirmed: uncertain.
        group.Glamourer.ListCalls = 0;
        group.Glamourer.List = call => call == 1 ? group.Glamourer.Designs.ToList() : null;
        var outcome = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal((AppearanceCopyStatus.Uncertain, (Guid?)null, true), (outcome.Status, outcome.DesignId, outcome.ReportedSuccess));
        Assert.Single(group.Glamourer.Adds);
        Assert.StartsWith("Glamourer reported success", AppearanceCaptureText.CopyResult(outcome), StringComparison.Ordinal);
    }

    // ---- 6. Size and text ---------------------------------------------------

    [Fact]
    public void TheTextMustBeNonEmptyStrictUtf8WithinTheLimit()
    {
        Assert.Equal(AppearanceCopyStatus.InvalidText, AppearanceCopyRules.CheckText([], out _));
        Assert.Equal(AppearanceCopyStatus.TooLarge, AppearanceCopyRules.CheckText(new byte[Limit + 1], out _));
        foreach (var invalid in new byte[][] { [0xC3, 0x28], [0xC0, 0x80], [0xED, 0xA0, 0x80], [0xFF], [0x41, 0xE2, 0x82] })
        {
            Assert.Equal(AppearanceCopyStatus.InvalidText, AppearanceCopyRules.CheckText(invalid, out var text));
            Assert.Null(text);
        }
        var exact = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("é", Limit / 2)));
        Assert.Equal(Limit, exact.Length);
        Assert.Null(AppearanceCopyRules.CheckText(exact, out var accepted));
        Assert.Equal(exact, Encoding.UTF8.GetBytes(accepted!));
        // A byte-order mark is kept, not stripped.
        var marked = new byte[] { 0xEF, 0xBB, 0xBF, 0x41 };
        Assert.Null(AppearanceCopyRules.CheckText(marked, out var withMark));
        Assert.Equal(marked, Encoding.UTF8.GetBytes(withMark!));
    }

    [Fact]
    public async Task AnOversizedTextIsRefusedAtTheManifestAndOneOfExactlyTheLimitIsPassedThrough()
    {
        using var group = new Group(this);
        var oversized = await group.PublishAsync(new string('o', Limit + 1));
        group.Service.ClearRequests();
        var refused = await group.Copier.CopyAsync(oversized, CancellationToken.None);
        Assert.Equal(AppearanceCopyStatus.TooLarge, refused.Status);
        Assert.Equal(new[] { $"GET bundles/{oversized.BundleId.Value}/manifest" }, group.Requests);
        Assert.Empty(group.Glamourer.Adds);

        var exactText = string.Concat(Enumerable.Repeat("é", Limit / 2));
        var exact = await group.PublishAsync(exactText);
        var created = await group.Copier.CopyAsync(exact, CancellationToken.None);
        Assert.Equal(AppearanceCopyStatus.Created, created.Status);
        var add = Assert.Single(group.Glamourer.Adds);
        Assert.Equal(Limit, Encoding.UTF8.GetByteCount(add.Text));
        Assert.Equal(Encoding.UTF8.GetBytes(exactText), Encoding.UTF8.GetBytes(add.Text));
    }

    // ---- 7. Pass-through ----------------------------------------------------

    [Fact]
    public async Task TheGlamourerTextIsPassedToGlamourerByteForByte()
    {
        using var group = new Group(this);
        var texts = new[]
        {
            "H4sIAAAAAAAACq1W227aQBD9lWifqYR3fV0+1WIS0aSVJEIRb5U+eHn+1/n+5ba3c=",
            """{"FileVersion":2,"Identifier":"0b5d","Name":"Évening ✨","Description":"tags \U0001F600","Mods":[{"Name":"x","Directory":"y"}]}""",
        };
        foreach (var text in texts)
        {
            var entry = await group.PublishAsync(text);
            Assert.Equal(AppearanceCopyStatus.Created, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
            Assert.Equal(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(group.Glamourer.Adds[^1].Text));
        }
        Assert.Equal(texts.Length, group.Glamourer.Adds.Count);
    }

    // ---- 8. Gate ------------------------------------------------------------

    [Fact]
    public async Task TheGateFailsClosedAndAnUnavailableGlamourerIsNeverCalled()
    {
        var asked = 0;
        (int, int) Version(int major, int minor)
        {
            asked++;
            return (major, minor);
        }
        Assert.Equal(AppearanceApiState.Unavailable, GlamourerDesignGate.Evaluate(false, () => Version(1, 8), 1, 8));
        Assert.Equal(0, asked);
        Assert.Equal(AppearanceApiState.Unavailable, GlamourerDesignGate.Evaluate(true, () => throw new NotReady(), 1, 8));
        Assert.Equal(AppearanceApiState.Incompatible, GlamourerDesignGate.Evaluate(true, () => Version(2, 0), 1, 8));
        Assert.Equal(AppearanceApiState.Incompatible, GlamourerDesignGate.Evaluate(true, () => Version(1, 7), 1, 8));
        Assert.Equal(AppearanceApiState.Ready, GlamourerDesignGate.Evaluate(true, () => Version(1, 8), 1, 8));
        Assert.Equal(AppearanceApiState.Ready, GlamourerDesignGate.Evaluate(true, () => Version(1, 9), 1, 8));

        var id = Guid.NewGuid();
        Assert.Equal(new GlamourerAddAnswer(GlamourerCallOutcome.Answered, 0, id), GlamourerDesignGate.Call(() => (0, id), e => e is NotReady));
        Assert.Equal(GlamourerCallOutcome.Unavailable, GlamourerDesignGate.Call(() => throw new NotReady(), e => e is NotReady).Outcome);
        Assert.Equal(GlamourerCallOutcome.Threw, GlamourerDesignGate.Call(() => throw new InvalidOperationException(), e => e is NotReady).Outcome);

        using var group = new Group(this);
        var entry = await group.PublishAsync("state text");
        foreach (var (state, expected) in new[]
        {
            (AppearanceApiState.Unavailable, AppearanceCopyStatus.GlamourerUnavailable),
            (AppearanceApiState.Incompatible, AppearanceCopyStatus.GlamourerIncompatible),
        })
        {
            group.Glamourer.Gate = state;
            Assert.Equal(expected, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        }
        group.Glamourer.Gate = AppearanceApiState.Ready;
        group.Glamourer.GateThrows = true;
        Assert.Equal(AppearanceCopyStatus.GlamourerUnavailable, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Empty(group.Glamourer.Adds);
        Assert.Equal(0, group.Glamourer.ListCalls);

        // AddDesign never reached Glamourer (IpcNotReadyError): not created, no retry.
        group.Glamourer.GateThrows = false;
        group.Glamourer.Add = (_, _) => new GlamourerAddAnswer(GlamourerCallOutcome.Unavailable, 0, Guid.Empty);
        Assert.Equal(AppearanceCopyStatus.GlamourerUnavailable, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Single(group.Glamourer.Adds);
    }

    [Fact]
    public async Task GlamourerLostAfterTheButtonWasShownIsStillRefusedWithoutAddDesign()
    {
        // Make a copy was enabled from the last plugin status;
        // Glamourer goes away before the click. The copier's own gate refuses,
        // and AddDesign is never called (the existing backstop).
        using var group = new Group(this);
        var entry = await group.PublishAsync("state text");
        group.Glamourer.Gate = AppearanceApiState.Ready;

        group.Glamourer.Gate = AppearanceApiState.Unavailable;
        Assert.Equal(AppearanceCopyStatus.GlamourerUnavailable, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Empty(group.Glamourer.Adds);

        // Back again: one confirmation, one copy.
        group.Glamourer.Gate = AppearanceApiState.Ready;
        Assert.Equal(AppearanceCopyStatus.Created, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Single(group.Glamourer.Adds);
    }

    // ---- 9. One call per confirmation ---------------------------------------

    [Fact]
    public async Task OneConfirmationCallsAddDesignAtMostOnceAndNeverRetries()
    {
        using var group = new Group(this);
        var entry = await group.PublishAsync("state text");

        var created = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal((AppearanceCopyStatus.Created, "RSyncStageCraft/Evening look"), (created.Status, created.FullPath));
        Assert.Single(group.Glamourer.Adds);
        Assert.Equal("RSyncStageCraft/Evening look", group.Glamourer.Adds[0].Name);

        // A second confirmation is a second design, with the next free name.
        var second = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal((AppearanceCopyStatus.Created, "RSyncStageCraft/Evening look #2"), (second.Status, second.FullPath));
        Assert.Equal(2, group.Glamourer.Adds.Count);

        // An exception or an error answer is reported, never retried.
        group.Glamourer.Add = (_, _) => throw new InvalidOperationException("ipc");
        Assert.Equal(AppearanceCopyStatus.Uncertain, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Equal(3, group.Glamourer.Adds.Count);
        group.Glamourer.Add = (_, _) => new GlamourerAddAnswer(GlamourerCallOutcome.Answered, int.MaxValue, Guid.Empty);
        Assert.Equal(AppearanceCopyStatus.Uncertain, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Equal(4, group.Glamourer.Adds.Count);
        // UnknownError after a design with that name appeared: still uncertain
        // (only listed to inspect), and still one call.
        group.Glamourer.Add = (text, name) =>
        {
            group.Glamourer.Create(name);
            return new GlamourerAddAnswer(GlamourerCallOutcome.Answered, int.MaxValue, Guid.Empty);
        };
        var unknown = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal((AppearanceCopyStatus.Uncertain, (Guid?)null), (unknown.Status, unknown.DesignId));
        Assert.Single(unknown.Inspect);
        Assert.Equal(5, group.Glamourer.Adds.Count);
        group.Glamourer.Add = null;

        // While one attempt runs, a second (a double-click) is refused without any call.
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        group.Copier.TestHook = async point =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var first = group.Copier.CopyAsync(entry, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(group.Copier.Busy);
        Assert.Equal(AppearanceCopyStatus.Busy, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        release.SetResult();
        Assert.Equal(AppearanceCopyStatus.Created, (await first).Status);
        Assert.Equal(6, group.Glamourer.Adds.Count);
        group.Copier.TestHook = null;

        // Cancelled before the framework callback runs: no call at all.
        using var cancel = new CancellationTokenSource();
        group.Dispatcher.Before = () =>
        {
            cancel.Cancel();
            return Task.CompletedTask;
        };
        Assert.Equal(AppearanceCopyStatus.Cancelled, (await group.Copier.CopyAsync(entry, cancel.Token)).Status);
        // Unloading after the server steps, as the framework callback starts:
        // the callback itself makes no call.
        group.Dispatcher.Before = () =>
        {
            group.Unloading = true;
            return Task.CompletedTask;
        };
        Assert.Equal(AppearanceCopyStatus.Cancelled, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        group.Dispatcher.Before = null;
        // Unloading before anything: no request and no call.
        group.Service.ClearRequests();
        Assert.Equal(AppearanceCopyStatus.Cancelled, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Empty(group.Requests);
        Assert.Equal(6, group.Glamourer.Adds.Count);
    }

    // ---- 10. Own entries and an unknown own id ------------------------------

    [Fact]
    public async Task OwnEntriesAndAnUnknownOwnIdAreRefusedWithoutAnyRequest()
    {
        using var group = new Group(this);
        var entry = await group.PublishAsync("state text");
        group.Service.ClearRequests();

        // Alice's own entry, even though the server would serve it to her.
        group.Self = new RscUserId("alice");
        Assert.Equal(AppearanceCopyStatus.OwnEntry, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        group.Self = null;
        Assert.Equal(AppearanceCopyStatus.OwnerUnknown, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        group.Self = new RscUserId("bob");
        Assert.Equal(AppearanceCopyStatus.NotPublished, (await group.Copier.CopyAsync(entry with { Published = false }, CancellationToken.None)).Status);
        Assert.Empty(group.Requests);

        // The id becomes unknown (or turns out to be the owner) by the final check.
        group.Copier.TestHook = point =>
        {
            group.Self = null;
            return Task.CompletedTask;
        };
        Assert.Equal(AppearanceCopyStatus.OwnerUnknown, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        group.Self = new RscUserId("bob");
        group.Copier.TestHook = point =>
        {
            group.Self = new RscUserId("alice");
            return Task.CompletedTask;
        };
        Assert.Equal(AppearanceCopyStatus.OwnEntry, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Empty(group.Glamourer.Adds);
    }

    // ---- 11. Texts ----------------------------------------------------------

    [Fact]
    public async Task TheConfirmationResultAndCaptureTextsSayWhatACopyDoesAndCarries()
    {
        var facts = string.Join("\n", AppearanceCaptureText.CopyFacts);
        foreach (var statement in new[]
        {
            "local Glamourer design only",
            "Nothing is applied",
            "neither your character nor the owner's character changes",
            "No Penumbra files are downloaded",
            "description, tags",
            "Penumbra mod associations",
            "mods may be missing",
            "original name, Glamourer folder, dates and links",
            "stays in your Glamourer even if the owner unpublishes",
            "at most once",
            "Nothing is retried automatically",
        })
            Assert.Contains(statement, facts, StringComparison.Ordinal);

        foreach (var status in Enum.GetValues<AppearanceCopyStatus>())
        {
            var text = AppearanceCaptureText.CopyResult(new AppearanceCopyOutcome(status)
            {
                DesignName = "Dress",
                FullPath = "RSyncStageCraft/Dress",
                DesignId = Guid.NewGuid(),
                Transfer = new AppearanceTransferResult(AppearanceTransferStatus.CacheFull),
            });
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("Unknown result", text, StringComparison.Ordinal);
            if (new AppearanceCopyOutcome(status).CreatedDesign)
                Assert.Contains("Created", text, StringComparison.OrdinalIgnoreCase);
            else if (status is not (AppearanceCopyStatus.Uncertain or AppearanceCopyStatus.Busy))
                Assert.Contains("Nothing was created", text, StringComparison.Ordinal);
        }
        var uncertain = AppearanceCaptureText.CopyResult(new AppearanceCopyOutcome(AppearanceCopyStatus.Uncertain));
        Assert.Contains("unknown whether", uncertain, StringComparison.Ordinal);
        Assert.Contains("RSC claims no design", uncertain, StringComparison.Ordinal);
        Assert.DoesNotContain("Inspect them", uncertain, StringComparison.Ordinal);
        var withCandidate = AppearanceCaptureText.CopyResult(new AppearanceCopyOutcome(AppearanceCopyStatus.Uncertain) { Inspect = ["RSyncStageCraft/Dress", ""] });
        Assert.Contains("\"RSyncStageCraft/Dress\", (no folder)", withCandidate, StringComparison.Ordinal);
        Assert.Contains("RSC does not claim them", withCandidate, StringComparison.Ordinal);

        // The saved-design capture summary names what its text carries;
        // every "not included" statement is about mod files.
        var design = new AppearanceCaptureRead(AppearanceCaptureSource.SavedDesign, "design-base64", [], "Evening look");
        using var plan = await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"), design, Path.Combine(root, "work"));
        var warnings = AppearanceCaptureText.Warnings(plan);
        Assert.Contains(AppearanceCaptureText.SavedDesignMetadataNotice, warnings);
        Assert.Contains("Penumbra mod associations (mod folder names, names and settings, not files)", AppearanceCaptureText.SavedDesignMetadataNotice, StringComparison.Ordinal);
        Assert.Contains("Group members who can read this entry receive this text", AppearanceCaptureText.SavedDesignMetadataNotice, StringComparison.Ordinal);
        // The copy may carry the metadata, but nothing promises that associations
        // survive or that the copy looks the same.
        Assert.Contains("A copied saved design may include its description, tags and mod-association metadata",
            AppearanceCaptureText.SavedDesignMetadataNotice, StringComparison.Ordinal);
        Assert.Contains("Glamourer may not preserve every association", AppearanceCaptureText.SavedDesignMetadataNotice, StringComparison.Ordinal);
        Assert.Contains("Penumbra mod files are never included", AppearanceCaptureText.SavedDesignMetadataNotice, StringComparison.Ordinal);
        // Every saved-design disclosure (owner warning; confirmation and result,
        // which both show CopyFacts) says the same four things and promises nothing.
        var savedDesignFact = Assert.Single(AppearanceCaptureText.CopyFacts, fact => fact.Contains("saved design", StringComparison.Ordinal));
        foreach (var disclosure in new[] { AppearanceCaptureText.SavedDesignMetadataNotice, savedDesignFact })
        {
            Assert.Contains("may include", disclosure, StringComparison.Ordinal);
            Assert.Contains("metadata", disclosure, StringComparison.Ordinal);
            Assert.Contains("Glamourer may not preserve every", disclosure, StringComparison.Ordinal);
            Assert.Contains("association", disclosure, StringComparison.Ordinal);
            Assert.Contains("Penumbra mod files are never included", disclosure, StringComparison.Ordinal);
            Assert.Contains("appearance may differ", disclosure, StringComparison.Ordinal);
            foreach (var promise in new[] { "keeps", "preserves", "preserved", "can also carry", "will include", "identical", "same look", "faithful", "fidelity" })
                Assert.DoesNotContain(promise, disclosure, StringComparison.OrdinalIgnoreCase);
        }
        var summary = string.Join("\n", warnings.Append(AppearanceCaptureText.Disclosure(plan)));
        Assert.DoesNotContain("mods are not included", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("mods this appearance relies on", summary, StringComparison.Ordinal);
        Assert.Contains("mod files are not included", summary, StringComparison.Ordinal);

        // A current-character capture does not get the saved-design notice.
        var current = new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, "state", [], PenumbraModDirectory: root);
        using var currentPlan = await AppearanceCapturePlanner.PlanAsync(new RscUserId("alice"), current, Path.Combine(root, "work"));
        Assert.DoesNotContain(AppearanceCaptureText.SavedDesignMetadataNotice, AppearanceCaptureText.Warnings(currentPlan));
    }

    // ---- 12. Logging --------------------------------------------------------

    [Fact]
    public async Task TheLogNeverContainsTheGlamourerTextOrACachePath()
    {
        using var group = new Group(this);
        const string Sentinel = "SENTINEL-glamourer-text-7f3a";
        var entry = await group.PublishAsync("prefix " + Sentinel + " suffix");
        Assert.Equal(AppearanceCopyStatus.Created, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        group.Glamourer.Add = (_, _) => new GlamourerAddAnswer(GlamourerCallOutcome.Answered, AppearanceCopyRules.GlamourerCouldNotParse, Guid.Empty);
        Assert.Equal(AppearanceCopyStatus.CouldNotParse, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);

        Assert.Equal(2, group.Log.Count);
        Assert.Contains(group.Log, line => line.Contains(entry.BundleId.Value, StringComparison.Ordinal) && line.Contains("Created", StringComparison.Ordinal));
        foreach (var line in group.Log)
        {
            Assert.DoesNotContain(Sentinel, line, StringComparison.Ordinal);
            Assert.DoesNotContain(group.CacheRoot, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, line, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- 13. Only the Glamourer blob ----------------------------------------

    [Fact]
    public async Task OnlyTheManifestTheGlamourerBlobAndTheFinalCheckAreRequested()
    {
        using var group = new Group(this);
        var (entry, manifest) = await group.PublishWithFilesAsync();
        Assert.NotEmpty(manifest.Files);
        group.Service.ClearRequests();

        Assert.Equal(AppearanceCopyStatus.Created, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        var id = entry.BundleId.Value;
        Assert.Equal(new[]
        {
            $"GET bundles/{id}/manifest",
            $"GET bundles/{id}/blobs/{manifest.Glamourer.Hash.Value}",
            $"GET bundles/{id}",
        }, group.Requests);
        Assert.Equal(AppearanceCacheState.Present, (await group.Cache.TryGetAsync(manifest.Glamourer)).State);
        foreach (var file in manifest.Files.Where(file => file.Content.Hash != manifest.Glamourer.Hash))
            Assert.NotEqual(AppearanceCacheState.Present, (await group.Cache.TryGetAsync(file.Content)).State);
        Assert.Equal(1, (await group.Cache.RefreshAsync()).Entries);
    }

    // ---- 14. Authorization --------------------------------------------------

    [Fact]
    public async Task OnlyAnotherMembersCurrentlyPublishedEntryIsCopied()
    {
        using var group = new Group(this);
        var entry = await group.PublishAsync("state text");
        Assert.Equal(AppearanceCopyStatus.Created, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);

        // Unpublished since bob's list was read: refused at the server.
        var unpublished = await group.PublishAsync("other text");
        Assert.True((await group.Alice.Client.UpdateBundleAsync(unpublished.BundleId, new AppearanceBundleEdit(Published: false))).Succeeded);
        Assert.Equal(AppearanceCopyStatus.NotPublished, (await group.Copier.CopyAsync(unpublished, CancellationToken.None)).Status);

        // Deleted.
        var deleted = await group.PublishAsync("third text");
        Assert.True((await group.Alice.Client.DeleteBundleAsync(deleted.BundleId)).Succeeded);
        Assert.Equal(AppearanceCopyStatus.NotPublished, (await group.Copier.CopyAsync(deleted, CancellationToken.None)).Status);
        Assert.Single(group.Glamourer.Adds);
    }

    // ---- 15. Unpublished before the final check -----------------------------

    [Fact]
    public async Task UnpublishingBeforeTheFinalCheckRefusesEvenThoughTheBlobIsNowCached()
    {
        using var group = new Group(this);
        var (entry, manifest) = await group.PublishWithFilesAsync();
        group.Copier.TestHook = async point =>
        {
            Assert.Equal("before-final-check", point);
            Assert.True((await group.Alice.Client.UpdateBundleAsync(entry.BundleId, new AppearanceBundleEdit(Published: false))).Succeeded);
        };
        Assert.Equal(AppearanceCopyStatus.NotPublished, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Equal(AppearanceCacheState.Present, (await group.Cache.TryGetAsync(manifest.Glamourer)).State);
        Assert.Empty(group.Glamourer.Adds);
        Assert.Equal(0, group.Dispatcher.Calls);
    }

    // ---- 16. Final-check failures -------------------------------------------

    [Fact]
    public async Task AFailingFinalCheckRefusesWithoutGlamourer()
    {
        // Long enough that only the deliberately stalled request times out on a busy runner.
        using var group = new Group(this, TimeSpan.FromSeconds(3));
        var entry = await group.PublishAsync("state text");
        var final = $"GET bundles/{entry.BundleId.Value}";
        foreach (var (answer, expected) in new (Func<CancellationToken, Task<HttpResponseMessage>>, AppearanceCopyStatus)[]
        {
            (_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not found"}""") }), AppearanceCopyStatus.NotPublished),
            (_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":"x"}""") }), AppearanceCopyStatus.TransferFailed),
            (async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }, AppearanceCopyStatus.TransferFailed),
            (_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"bundle":{"bundleId":"nothex"}}""") }), AppearanceCopyStatus.TransferFailed),
        })
        {
            group.Service.Intercept = (request, token) => $"{request.Method} {FakeAppearanceService.Relative(request)}" == final ? answer(token) : null;
            Assert.Equal(expected, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        }
        Assert.Empty(group.Glamourer.Adds);
        Assert.Equal(0, group.Dispatcher.Calls);
    }

    // ---- 17. The cache does not authorize -----------------------------------

    [Fact]
    public async Task ACachedBlobNeverAuthorizesACopy()
    {
        using var group = new Group(this);
        var entry = await group.PublishAsync("state text");
        Assert.Equal(AppearanceCopyStatus.Created, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.True((await group.Alice.Client.UpdateBundleAsync(entry.BundleId, new AppearanceBundleEdit(Published: false))).Succeeded);
        group.Service.ClearRequests();

        Assert.Equal(AppearanceCopyStatus.NotPublished, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        Assert.Equal(new[] { $"GET bundles/{entry.BundleId.Value}/manifest" }, group.Requests);
        Assert.Single(group.Glamourer.Adds);
    }

    // ---- 18. Integrity ------------------------------------------------------

    [Fact]
    public async Task BytesThatDoNotMatchTheirHashNeverReachGlamourer()
    {
        using var group = new Group(this);
        var entry = await group.PublishAsync("state text that is long enough to tamper with");

        // From the server: the last body byte flipped.
        group.Service.Tamper = request => request.RequestUri!.AbsolutePath.Contains("/blobs/", StringComparison.Ordinal);
        var tampered = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal((AppearanceCopyStatus.TransferFailed, (AppearanceTransferStatus?)AppearanceTransferStatus.InvalidResponse), (tampered.Status, tampered.Transfer?.Status));
        Assert.Empty(group.Glamourer.Adds);

        // In the cache, after it was verified: re-hashed when read for the copy.
        group.Service.Tamper = null;
        Assert.Equal(AppearanceCopyStatus.Created, (await group.Copier.CopyAsync(entry, CancellationToken.None)).Status);
        var manifest = (await group.Bob.GetManifestAsync(entry.BundleId)).Manifest!;
        var (state, path) = await group.Cache.TryGetAsync(manifest.Glamourer);
        Assert.Equal(AppearanceCacheState.Present, state);
        var bytes = await File.ReadAllBytesAsync(path!);
        bytes[^1] ^= 0x20;
        await File.WriteAllBytesAsync(path!, bytes);
        var changed = await group.Copier.CopyAsync(entry, CancellationToken.None);
        Assert.Equal((AppearanceCopyStatus.TransferFailed, (AppearanceTransferStatus?)AppearanceTransferStatus.CacheUnavailable), (changed.Status, changed.Transfer?.Status));
        Assert.Single(group.Glamourer.Adds);
    }

    // ---- Helpers --------------------------------------------------------------------------

    private static GlamourerDesignListing Design(string name, string path) => new(Guid.NewGuid(), name, path);

    /// <summary>Alice publishes; Bob copies with a fake Glamourer and an inline dispatcher. The service records every request.</summary>
    private sealed class Group : IDisposable
    {
        private int published;

        public Group(AppearanceCopyTests owner, TimeSpan? inactivity = null)
        {
            var aliceCache = new AppearanceBlobCache(Path.Combine(owner.root, "alice-" + Guid.NewGuid().ToString("N")));
            Reader = new Reader();
            Alice = new AppearanceAuthoring(Reader,
                new AppearanceTransferClient(() => Service.Settings("alice"), aliceCache, null, Service),
                () => new RscUserId("alice"), aliceCache.TemporaryDirectory);
            Root = owner.root;
            CacheRoot = Path.Combine(owner.root, "bob-" + Guid.NewGuid().ToString("N"));
            Cache = new AppearanceBlobCache(CacheRoot);
            Bob = new AppearanceTransferClient(() => Service.Settings("bob"), Cache,
                inactivity is { } timeout ? new AppearanceTransferOptions { InactivityTimeout = timeout } : null, Service);
            Copier = new AppearanceCopier(Bob, Dispatcher, Glamourer, () => Self, () => Unloading, line =>
            {
                lock (Log)
                    Log.Add(line);
            });
        }

        public FakeAppearanceService Service { get; } = new();
        public Reader Reader { get; }
        public AppearanceAuthoring Alice { get; }
        public AppearanceTransferClient Bob { get; }
        public AppearanceBlobCache Cache { get; }
        public string Root { get; }
        public string CacheRoot { get; }
        public InlineDispatcher Dispatcher { get; } = new();
        public FakeGlamourer Glamourer { get; } = new();
        public AppearanceCopier Copier { get; }
        public List<string> Log { get; } = [];
        public RscUserId? Self { get; set; } = new RscUserId("bob");
        public bool Unloading { get; set; }

        /// <summary>
        /// Every request since the last <see cref="FakeAppearanceService.ClearRequests"/> except Alice's,
        /// as "METHOD path": Bob's, and any that carried no valid key.
        /// </summary>
        public List<string> Requests => [.. Service.Requests.Where(request => request.Account != "alice").Select(request => request.ToString())];

        /// <summary>A state-only entry named "Evening look", published; Bob's view of it.</summary>
        public async Task<AppearanceBundleSummary> PublishAsync(string glamourerText)
        {
            Reader.Read = new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, glamourerText, [], PenumbraModDirectory: Root);
            return (await UploadAndPublishAsync()).Entry;
        }

        /// <summary>An entry with Penumbra files, published.</summary>
        public async Task<(AppearanceBundleSummary Entry, AppearanceBundleManifest Manifest)> PublishWithFilesAsync()
        {
            var folder = Path.Combine(Root, "mods-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var model = Path.Combine(folder, "body.mdl");
            var texture = Path.Combine(folder, "body.tex");
            File.WriteAllBytes(model, RandomNumberGenerator.GetBytes(5000));
            File.WriteAllBytes(texture, RandomNumberGenerator.GetBytes(20_000));
            Reader.Read = new AppearanceCaptureRead(AppearanceCaptureSource.CurrentCharacter, "state with files " + Guid.NewGuid().ToString("N"),
            [
                new(model, ["chara/equipment/e0001/model/c0101e0001_top.mdl"]),
                new(texture, ["chara/equipment/e0001/texture/v01_c0101e0001_top_n.tex"]),
            ], PenumbraModDirectory: folder);
            return await UploadAndPublishAsync();
        }

        private async Task<(AppearanceBundleSummary Entry, AppearanceBundleManifest Manifest)> UploadAndPublishAsync()
        {
            var captured = await Alice.CaptureCurrentCharacterAsync(CancellationToken.None);
            using var plan = captured.Plan!;
            Assert.True(plan.CanUpload);
            var name = published++ == 0 ? "Evening look" : "Evening look " + published.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var uploaded = await Alice.UploadAsync(plan, AppearanceUploadTarget.NewEntry(name), CancellationToken.None);
            Assert.True(uploaded.Result.Succeeded);
            var id = uploaded.BundleId!.Value;
            Assert.True((await Alice.Client.UpdateBundleAsync(id, new AppearanceBundleEdit(Published: true))).Succeeded);
            var seen = (await Bob.ListBundlesAsync()).Bundles.Single(bundle => bundle.BundleId == id);
            Assert.True(seen.Published);
            return (seen, plan.Manifest!);
        }

        public void Dispose()
        {
            Bob.Dispose();
            Alice.Client.Dispose();
        }
    }

    private sealed class Reader : IAppearanceCaptureReader
    {
        public AppearanceCaptureRead? Read { get; set; }

        public Task<AppearanceReadResult<AppearanceCaptureRead>> ReadCurrentCharacterAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.Ok, Read));

        public Task<AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>> ListDesignsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>>(AppearanceReadStatus.Unavailable));

        public Task<AppearanceReadResult<AppearanceCaptureRead>> ReadDesignAsync(AppearanceDesignEntry design, CancellationToken cancellationToken) =>
            Task.FromResult(new AppearanceReadResult<AppearanceCaptureRead>(AppearanceReadStatus.Unavailable));
    }

    /// <summary>Simulates Glamourer's design list: AddDesign creates "RSyncStageCraft/leaf" with name "leaf".</summary>
    private sealed class FakeGlamourer : IGlamourerDesignIpc
    {
        public AppearanceApiState Gate { get; set; } = AppearanceApiState.Ready;
        public bool GateThrows { get; set; }
        public List<GlamourerDesignListing> Designs { get; } = [];
        public int ListCalls { get; set; }
        public Func<int, IReadOnlyList<GlamourerDesignListing>?>? List { get; set; }
        public List<(string Text, string Name)> Adds { get; } = [];
        public Func<string, string, GlamourerAddAnswer>? Add { get; set; }

        public AppearanceApiState State() => GateThrows ? throw new NotReady() : Gate;

        public IReadOnlyList<GlamourerDesignListing>? ListDesigns()
        {
            ListCalls++;
            return List is { } list ? list(ListCalls) : Designs.ToList();
        }

        public GlamourerAddAnswer AddDesign(string text, string name)
        {
            Adds.Add((text, name));
            if (Add is { } add)
                return add(text, name);
            return new GlamourerAddAnswer(GlamourerCallOutcome.Answered, AppearanceCopyRules.GlamourerSuccess, Create(name));
        }

        public Guid Create(string requested)
        {
            var slash = requested.LastIndexOf('/');
            var leaf = slash >= 0 ? requested[(slash + 1)..] : requested;
            var id = Guid.NewGuid();
            Designs.Add(new GlamourerDesignListing(id, leaf, requested));
            return id;
        }
    }

    /// <summary>Runs the framework callback inline, after an optional hook.</summary>
    private sealed class InlineDispatcher : IGameThreadDispatcher
    {
        public int Calls { get; private set; }
        public Func<Task>? Before { get; set; }

        public async Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            Calls++;
            if (Before is { } before)
                await before();
            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }
    }

    /// <summary>A stand-in for Dalamud's IpcNotReadyError (no Dalamud types in this project).</summary>
    private sealed class NotReady : Exception
    {
    }
}
