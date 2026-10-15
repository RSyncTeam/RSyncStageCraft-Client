using RSyncStageCraft.Core.Actors;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Dependencies;
using RSyncStageCraft.Core.Furniture;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Ui;

namespace RSyncStageCraft.Tests;

/// <summary>
/// Every player text says what happened and gives one action,
/// with no enum name, exception text or local path.
/// </summary>
public sealed class MessageTextTests
{
    // A local path and an exception message that must never reach a player.
    private const string SentinelPath = @"C:\Users\sentinel-user\AppData\Roaming\XIVLauncher\secret.json";
    private const string SentinelException = "SentinelExceptionMessage-7f3a";

    // Compound enum names (two or more capitals, e.g. "StaleRevision"): plain
    // words such as "Cancelled" are ordinary English in a text.
    private static readonly string[] RejectNames =
        Enum.GetNames<ProtocolRejectReason>().Where(name => name.Count(char.IsUpper) > 1).ToArray();

    private static void AssertNoLeak(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("sentinel", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SentinelException, text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", text, StringComparison.Ordinal);
    }

    // ---- Server refusals (ServerCommands) -----------------------------------

    public static TheoryData<int> EveryRejectReason()
    {
        var data = new TheoryData<int>();
        foreach (var reason in Enum.GetValues<ProtocolRejectReason>())
            data.Add((int)reason);
        data.Add(999);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRejectReason))]
    public void EveryRejectReasonHasATextWithoutAnEnumName(int value)
    {
        var text = new ServerCommandResult(ServerCommandStatus.Rejected, RejectReason: (ProtocolRejectReason)value,
            Detail: SentinelPath + " " + SentinelException).Describe();

        AssertNoLeak(text);
        foreach (var name in RejectNames)
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullOrUnknownReasonIsRefusedByTheServer()
    {
        Assert.Equal("Refused by the server.", new ServerCommandResult(ServerCommandStatus.Rejected).Describe());
        Assert.Equal("Refused by the server.",
            new ServerCommandResult(ServerCommandStatus.Rejected, RejectReason: ProtocolRejectReason.None).Describe());
        Assert.Equal("Refused by the server.",
            new ServerCommandResult(ServerCommandStatus.Rejected, RejectReason: (ProtocolRejectReason)999).Describe());
    }

    [Fact]
    public void TheFiveExistingRefusalTextsAreKeptWordForWord()
    {
        Assert.Equal("Refused: this scene belongs to another place.", ServerCommandResult.DescribeRejection(ProtocolRejectReason.LocationMismatch));
        Assert.Equal("Refused: the scene has no location.", ServerCommandResult.DescribeRejection(ProtocolRejectReason.LocationRequired));
        Assert.Equal("Refused: not a valid furniture item or light.", ServerCommandResult.DescribeRejection(ProtocolRejectReason.InvalidModelIdentity));
        Assert.Equal("Refused: only its owner can change it.", ServerCommandResult.DescribeRejection(ProtocolRejectReason.NotOwner));
        Assert.Equal("Refused: the scene is full (actor or object limit reached).", ServerCommandResult.DescribeRejection(ProtocolRejectReason.CapacityExceeded));
        Assert.DoesNotContain("try again", ServerCommandResult.DescribeRejection(ProtocolRejectReason.CapacityExceeded), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ProtocolRejectReason.StaleRevision, "Refused: someone changed it first. The current state is shown; try again.")]
    [InlineData(ProtocolRejectReason.RequestConflict, "Refused: someone changed it first. The current state is shown; try again.")]
    [InlineData(ProtocolRejectReason.NotReady, "Refused: the scene is still loading. Try again in a moment.")]
    [InlineData(ProtocolRejectReason.SyncNotReady, "Refused: the scene is still loading. Try again in a moment.")]
    [InlineData(ProtocolRejectReason.InvalidSession, "Refused: you are not signed in. Press Reconnect.")]
    [InlineData(ProtocolRejectReason.NotAuthenticated, "Refused: you are not signed in. Press Reconnect.")]
    [InlineData(ProtocolRejectReason.UnknownObject, "Refused: it no longer exists.")]
    [InlineData(ProtocolRejectReason.Deleted, "Refused: it no longer exists.")]
    [InlineData(ProtocolRejectReason.WrongObjectKind, "Refused: that does not apply to this kind of object.")]
    [InlineData(ProtocolRejectReason.MessageTooLarge, "Refused: the change is too large.")]
    [InlineData(ProtocolRejectReason.InvalidPatch, "Refused: the change is not valid.")]
    [InlineData(ProtocolRejectReason.NotAuthorized, "Refused: the server does not allow that scene action.")]
    [InlineData(ProtocolRejectReason.UnsupportedFeature, "Refused: this server does not support that. Ask the host to update the server.")]
    [InlineData(ProtocolRejectReason.IncompatibleProtocol, "Refused: the plugin and server versions do not match. Update the plugin, or ask the host to update the server.")]
    [InlineData(ProtocolRejectReason.ProtocolMismatch, "Refused: the plugin and server versions do not match. Update the plugin, or ask the host to update the server.")]
    public void EachNewRefusalTextMatchesTheDesign(ProtocolRejectReason reason, string text) =>
        Assert.Equal(text, ServerCommandResult.DescribeRejection(reason));

    [Fact]
    public void AnUndefinedCommandStatusIsRefusedByTheServerAndShowsNoEnumName()
    {
        // Never the status name, never the detail.
        var text = new ServerCommandResult((ServerCommandStatus)99, Detail: SentinelPath).Describe();

        Assert.Equal("Refused by the server.", text);
        Assert.Equal("Refused by the server.", new ServerCommandResult((ServerCommandStatus)99).Describe());
    }

    [Fact]
    public void TheKeptCommandTextsAreUnchanged()
    {
        Assert.Equal("Done.", new ServerCommandResult(ServerCommandStatus.Completed).Describe());
        Assert.Equal("Not connected to the server.", new ServerCommandResult(ServerCommandStatus.NotConnected).Describe());
        Assert.Equal("Still sending earlier changes; try again.", new ServerCommandResult(ServerCommandStatus.Busy).Describe());
        Assert.Equal("Cancelled.", new ServerCommandResult(ServerCommandStatus.Cancelled).Describe());
        Assert.Equal("The change could not be sent; check the connection and try again.", ServerCommandResult.NotSentText);
    }

    [Theory]
    [InlineData("Your location is not known.")]
    [InlineData("Not a furniture item.")]
    [InlineData("Not a creature.")]
    [InlineData("Nothing to change.")]
    [InlineData("Invalid transform.")]
    [InlineData("Invalid position.")]
    [InlineData("A light value is out of range.")]
    [InlineData("An actor value is out of range.")]
    [InlineData("An actor control is out of range.")]
    [InlineData("Look-at is not supported.")]
    [InlineData("Freeze is not supported.")]
    public void TheEditorsReasonIsShownForAnInvalidChange(string detail) =>
        Assert.Equal(detail, new ServerCommandResult(ServerCommandStatus.Invalid, Detail: detail).Describe());

    [Fact]
    public void AnInvalidChangeWithoutAReasonKeepsTheGeneralText() =>
        Assert.Equal("The change is not valid.", new ServerCommandResult(ServerCommandStatus.Invalid).Describe());

    // ---- Connection --------------------------------------------------------

    [Fact]
    public void ConnectionTextsMatchTheDesign()
    {
        Assert.Equal("Could not reach rsc.example.test. Check the server address in Settings and your internet connection.",
            ConnectionText.CouldNotReach("rsc.example.test"));
        Assert.Equal("Sign-in did not finish. Press Reconnect; if it keeps happening, ask the host to check the server.",
            ConnectionText.SignInUnfinished);
        Assert.Equal("Lost the connection to the server; reconnecting automatically.", ConnectionText.LostConnection);
        Assert.Equal("The scene could not be loaded. Press Reconnect; if it keeps happening, ask the host.",
            ConnectionText.SceneNotLoaded);
        Assert.Equal("This access key is not valid. Paste the key you were given in Settings, then press Save.",
            ConnectionText.KeyNotValid);
        Assert.Equal("Finishing the previous connection; reconnecting in a moment.", ConnectionText.FinishingPrevious);
        Assert.EndsWith("You will need to paste it again after restarting the game.", ConnectionText.KeyNotStoredSecurely, StringComparison.Ordinal);
        Assert.DoesNotContain("too large to hold until rendering", ConnectionText.SceneNotLoaded, StringComparison.Ordinal);
    }

    // ---- Appearances -------------------------------------------------------

    public static TheoryData<int> EveryTransferStatus()
    {
        var data = new TheoryData<int>();
        foreach (var status in Enum.GetValues<AppearanceTransferStatus>())
            data.Add((int)status);
        data.Add(999);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTransferStatus))]
    public void EveryTransferStatusHasATextWithoutAnEnumName(int value)
    {
        var text = new AppearanceTransferResult((AppearanceTransferStatus)value, ServerCode: "SentinelCode").Message;

        AssertNoLeak(text);
        foreach (var name in Enum.GetNames<AppearanceTransferStatus>().Where(name => name.Count(char.IsUpper) > 1))
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppearanceTransferStatus.InvalidCredentials, "Press Reconnect.")]
    [InlineData(AppearanceTransferStatus.Redirected, "Ask the host.")]
    [InlineData(AppearanceTransferStatus.ServerError, "Ask the host.")]
    [InlineData(AppearanceTransferStatus.InvalidResponse, "Ask the host.")]
    [InlineData(AppearanceTransferStatus.NotFound, "Refresh the list.")]
    [InlineData(AppearanceTransferStatus.Forbidden, "Refresh the list.")]
    [InlineData(AppearanceTransferStatus.Refused, "Refresh the list.")]
    [InlineData(AppearanceTransferStatus.BlobUnavailable, "the owner must upload it again.")]
    [InlineData(AppearanceTransferStatus.CacheUnavailable, "Free space on the drive or delete unused appearances.")]
    [InlineData(AppearanceTransferStatus.CacheFull, "Free space on the drive or delete unused appearances.")]
    [InlineData(AppearanceTransferStatus.CacheOverLimit, "Free space on the drive or delete unused appearances.")]
    public void TransferResultsWithoutANextStepNowGiveOne(AppearanceTransferStatus status, string step)
    {
        var text = new AppearanceTransferResult(status).Message;
        Assert.EndsWith(step, text, StringComparison.Ordinal);
        Assert.DoesNotContain("the limit is raised", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatchAllAndCaptureTextsGiveANextStepAndPromiseNothing()
    {
        Assert.Equal("Something went wrong (details in the log). Refresh the list to see the current state, then try again.",
            AppearanceOperationText.Failed(copy: false));
        Assert.Equal("Something went wrong (details in the log). Check Glamourer for the copy before trying again.",
            AppearanceOperationText.Failed(copy: true));
        foreach (var text in new[] { AppearanceOperationText.FailedText, AppearanceOperationText.CopyFailedText })
            Assert.DoesNotContain("nothing", text, StringComparison.OrdinalIgnoreCase);

        Assert.EndsWith("Press Reconnect, then capture again.",
            AppearanceCaptureText.Refusal(AppearanceCaptureRefusal.InvalidManifest, null), StringComparison.Ordinal);
        var invalid = AppearanceCaptureText.Read(AppearanceReadStatus.InvalidData);
        Assert.EndsWith("Check the design in Glamourer, then capture again.", invalid, StringComparison.Ordinal);
        Assert.DoesNotContain("Update Glamourer", invalid, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingClearsOnlyTheDiscardedCapturesOwnMessage()
    {
        var review = new string("Review what would be uploaded, then confirm or discard.".AsSpan());
        Assert.Null(AppearanceOperationText.AfterCloseDiscard(review, review));
        // A later message (for example a rename result) is not the capture's: kept.
        Assert.Equal("Renamed.", AppearanceOperationText.AfterCloseDiscard("Renamed.", review));
        // An equal text set by something else is not the capture's own message.
        var other = new string("Review what would be uploaded, then confirm or discard.".AsSpan());
        Assert.Same(other, AppearanceOperationText.AfterCloseDiscard(other, review));
        Assert.Null(AppearanceOperationText.AfterCloseDiscard(null, review));
        Assert.Equal("Cancelled.", AppearanceOperationText.AfterCloseDiscard("Cancelled.", null));
    }

    // ---- Plugins and the dependency matrix --------------------------------

    private static DependencyHealth Ready(string name) => new(name, true, true);

    private static DependencyHealth Missing(string name) => new(name, false, false, "Dependency unavailable or incompatible.");

    private static DependencyHealth Incompatible(string name) => new(name, true, false, "Dependency unavailable or incompatible.");

    private static DependencyHealth Unreadable(string name) =>
        new(name, false, false, "Dependency health probe failed: " + SentinelException + " at " + SentinelPath);

    [Fact]
    public void WithBothPluginsEverythingIsAllowedAndNoLineIsShown()
    {
        var gate = AppearanceGate.From([Ready("Penumbra"), Ready("Glamourer")]);

        Assert.True(gate.CanCaptureCurrent);
        Assert.True(gate.CanUseSavedDesigns);
        Assert.True(gate.CanCopy);
        Assert.Empty(gate.MissingLines);
    }

    [Fact]
    public void WithoutPenumbraOnlyCurrentCharacterCaptureIsDisabledAndSavedDesignsStillWork()
    {
        foreach (var penumbra in new[] { Missing("Penumbra"), Incompatible("Penumbra"), Unreadable("Penumbra") })
        {
            var gate = AppearanceGate.From([penumbra, Ready("Glamourer")]);

            Assert.False(gate.CanCaptureCurrent);
            // Saved-design capture and Make a copy need only Glamourer.
            Assert.True(gate.CanUseSavedDesigns);
            Assert.True(gate.CanCopy);
            var line = Assert.Single(gate.MissingLines);
            Assert.StartsWith("Penumbra", line, StringComparison.Ordinal);
            Assert.Contains("Capturing your current character is disabled; saved Glamourer designs can still be captured.",
                line, StringComparison.Ordinal);
            AssertNoLeak(line);
        }
    }

    [Fact]
    public void WithoutGlamourerEveryCaptureAndCopyIsDisabled()
    {
        foreach (var glamourer in new[] { Missing("Glamourer"), Incompatible("Glamourer"), Unreadable("Glamourer") })
        {
            var gate = AppearanceGate.From([Ready("Penumbra"), glamourer]);

            Assert.False(gate.CanCaptureCurrent);
            Assert.False(gate.CanUseSavedDesigns);
            Assert.False(gate.CanCopy);
            var line = Assert.Single(gate.MissingLines);
            Assert.StartsWith("Glamourer", line, StringComparison.Ordinal);
            Assert.Contains("Capturing appearances and Make a copy are disabled.", line, StringComparison.Ordinal);
            AssertNoLeak(line);
        }
    }

    [Fact]
    public void WithNeitherPluginOrNoObservationEverythingIsDisabledWithOneLineEach()
    {
        foreach (var dependencies in new DependencyHealth[][] { [Missing("Penumbra"), Missing("Glamourer")], [] })
        {
            var gate = AppearanceGate.From(dependencies);

            Assert.False(gate.CanCaptureCurrent);
            Assert.False(gate.CanUseSavedDesigns);
            Assert.False(gate.CanCopy);
            Assert.Equal(2, gate.MissingLines.Count);
            Assert.StartsWith("Penumbra", gate.MissingLines[0], StringComparison.Ordinal);
            Assert.StartsWith("Glamourer", gate.MissingLines[1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PluginLinesFollowTheFlagsAndNeverShowTheFailureText()
    {
        Assert.Equal("Penumbra: ready", PluginLines.Line(Ready("Penumbra")));
        Assert.Equal(
            "Penumbra is not available. Capturing your current character is disabled; saved Glamourer designs can still be " +
            "captured. Install or enable Penumbra, then press Refresh plugin status.",
            PluginLines.Line(Missing("Penumbra")));
        Assert.Equal(
            "Glamourer is not available. Capturing appearances and Make a copy are disabled. Install or enable Glamourer, " +
            "then press Refresh plugin status.",
            PluginLines.Line(Missing("Glamourer")));
        Assert.StartsWith("Glamourer is a version RSC cannot use. Update Glamourer, then press Refresh plugin status.",
            PluginLines.Line(Incompatible("Glamourer")), StringComparison.Ordinal);
        var unreadable = PluginLines.Line(Unreadable("Penumbra"));
        Assert.StartsWith("Penumbra's status could not be read. Press Refresh plugin status.", unreadable, StringComparison.Ordinal);
        AssertNoLeak(unreadable);
        Assert.DoesNotContain("probe", unreadable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Customize+: optional; not used by RSC.", PluginLines.Optional("Customize+"));
    }

    // ---- Not drawn and the Actors tab ----------------------------------------

    [Fact]
    public void FurnitureNotDrawnTextsMatchTheDesignAndKeepThePartsForTheLog()
    {
        var multipart = new FurnitureSupport(FurnitureSupportStatus.Multipart) { Detail = "2 Model " + SentinelPath };
        Assert.Equal("Has several parts (for example a built-in light); this version draws single-model items only.",
            multipart.Describe());
        Assert.Equal("Multipart item: 2 Model " + SentinelPath, multipart.DescribeForLog());
        Assert.Equal("Its model file is missing on this client; repair the game files in the launcher.",
            new FurnitureSupport(FurnitureSupportStatus.ModelMissing).Describe());
        // Every other furniture text is kept.
        Assert.Equal("Unknown item on this client",
            new FurnitureSupport(FurnitureSupportStatus.NotInCatalog).Describe());
    }

    [Fact]
    public void HiddenActorNoticesAreBuiltFromTheFlagsWithoutTheFailure()
    {
        Assert.Null(ActorNotices.Hidden(ActorSuppression.None));
        Assert.Equal("Actors are hidden on this client during GPose and cutscenes.",
            ActorNotices.Hidden(new ActorSuppression(true, false, false, false)));
        Assert.Equal("Actors are hidden on this client during GPose and cutscenes.",
            ActorNotices.Hidden(new ActorSuppression(false, false, true, true)));
        var unchecked_ = ActorNotices.Hidden(ActorSuppression.Failed(SentinelException + " " + SentinelPath));
        Assert.Equal(
            "Actors are hidden on this client because RSC could not check for GPose or cutscenes; they reappear when it can.",
            unchecked_);
        AssertNoLeak(unchecked_!);
        foreach (var flag in new[] { "GPose (", "OccupiedInCutSceneEvent", "WatchingCutscene" })
            Assert.DoesNotContain(flag, ActorNotices.Hidden(new ActorSuppression(true, true, true, true))!, StringComparison.Ordinal);
    }

    [Fact]
    public void LeftUntouchedObjectsAreCountedWithoutTechnicalDetail()
    {
        Assert.Null(ActorNotices.LeftUntouched(0));
        Assert.Equal("2 object(s) were left untouched for safety; they disappear when you change area.",
            ActorNotices.LeftUntouched(2));
    }

    // ---- Place tooltips and the banner -------------------------------------

    [Fact]
    public void ADisabledPlaceButtonSaysWhyInOrder()
    {
        Assert.Equal("Connect first.", PlaceBlock.Reason(false, false, "x", false));
        Assert.Equal("Your location is not known here.", PlaceBlock.Reason(true, false, "x", false));
        Assert.Equal("This item cannot be shared: Has several parts (for example a built-in light); this version draws " +
                     "single-model items only.",
            PlaceBlock.Reason(true, true, FurnitureSupport.MultipartText, false));
        Assert.Equal("Wait until the current move is saved.", PlaceBlock.Reason(true, true, null, false));
        Assert.Null(PlaceBlock.Reason(true, true, null, true));
    }
}
