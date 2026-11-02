using System.Globalization;

namespace RSyncStageCraft.Core.Appearance;

/// <summary>
/// The owner-facing wording of a capture summary, kept here so it is
/// tested: the disclosure says plainly that the
/// approved files' contents leave the computer, a state-only saved design
/// never implies that mods are reproduced, and excluded meta changes are
/// always warned about.
/// </summary>
public static class AppearanceCaptureText
{
    public const string ReadOnlyNote =
        "Capturing only reads your own appearance on this computer. It never changes your character, your collections or your designs, and nothing is sent until you confirm.";

    public const string UnpublishNotice =
        "Unpublishing stops new downloads at once, but copies the other player already downloaded cannot be recalled.";

    public const string StateOnlyLabel = "State only - no Penumbra mod files";

    /// <summary>Shown with every saved-design capture summary.</summary>
    public const string SavedDesignMetadataNotice =
        "Its Glamourer text still includes the design's name, Glamourer folder, dates, description, tags, links to your other designs " +
        "and Penumbra mod associations (mod folder names, names and settings, not files). Group members who can read this entry " +
        "receive this text. A copied saved design may include its description, tags and mod-association metadata, but Glamourer " +
        "may not preserve every association. Penumbra mod files are never included, and the copy's appearance may differ.";

    /// <summary>The button on another member's published entry.</summary>
    public const string CopyButton = "Make a copy in Glamourer";

    /// <summary>
    /// What "Make a Copy" does and does not do, shown in the confirmation
    /// and again with every result.
    /// </summary>
    public static IReadOnlyList<string> CopyFacts { get; } =
    [
        "This creates a local Glamourer design only, in your Glamourer folder \"" + AppearanceCopyNames.Folder + "\".",
        "Nothing is applied: neither your character nor the owner's character changes.",
        "No Penumbra files are downloaded. Only this appearance's Glamourer text is copied.",
        "If the entry was captured from a saved design, the copy may include that design's description, tags, colour, " +
            "display and reset settings, and Penumbra mod associations as metadata (mod folder names, names and settings), " +
            "but Glamourer may not preserve every mod association. Penumbra mod files are never included. " +
            "The associated mods may be missing from your Penumbra, so the resulting appearance may differ; no exact match is promised. " +
            "The downloaded text also contains " +
            "the design's original name, Glamourer folder, dates and links to the owner's other designs; the copy gets its own name and place.",
        "Once created, the design stays in your Glamourer even if the owner unpublishes this entry. To remove it, delete it in Glamourer.",
        "Each confirmation calls Glamourer at most once and creates at most one design. Nothing is retried automatically.",
    ];

    /// <summary>The outcome line of a copy attempt (followed by <see cref="CopyFacts"/>).</summary>
    public static string CopyResult(AppearanceCopyOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var id = outcome.DesignId is { } guid ? guid.ToString("N")[..8] : null;
        var inspect = outcome.Inspect.Count == 0
            ? string.Empty
            : " Glamourer now lists new design(s) with this name at " +
              string.Join(", ", outcome.Inspect.Select(path => path.Length == 0 ? "(no folder)" : "\"" + path + "\"")) +
              ". RSC does not claim them: they may have been made by this copy, by you, by a Glamourer rule or by another plugin. Inspect them in Glamourer.";
        return outcome.Status switch
        {
            AppearanceCopyStatus.Created =>
                $"Created the Glamourer design \"{outcome.FullPath}\" ({id}).",
            AppearanceCopyStatus.CreatedElsewhere => outcome.FullPath is { Length: > 0 } path
                ? $"Created the Glamourer design \"{outcome.DesignName}\" ({id}), but Glamourer placed it at \"{path}\". Move it in Glamourer if you like."
                : $"Created the Glamourer design \"{outcome.DesignName}\" ({id}), but Glamourer did not place it in a folder. Check it in Glamourer.",
            AppearanceCopyStatus.Uncertain =>
                (outcome.ReportedSuccess ? "Glamourer reported success, but the design it reported could not be confirmed in its design list. " : string.Empty) +
                "It is unknown whether Glamourer created the design, and RSC claims no design. Check Glamourer (folder \"" + AppearanceCopyNames.Folder +
                "\") before trying again; nothing was retried, moved or deleted." + inspect,
            AppearanceCopyStatus.CouldNotParse => "Glamourer could not read this appearance. Nothing was created.",
            AppearanceCopyStatus.Busy => "A copy is already being made.",
            AppearanceCopyStatus.OwnerUnknown => "Not connected: your account is not known yet. Nothing was created.",
            AppearanceCopyStatus.OwnEntry => "This is your own entry; only other members' entries can be copied. Nothing was created.",
            AppearanceCopyStatus.NotPublished => "This entry is no longer published to you. Nothing was created.",
            AppearanceCopyStatus.TransferFailed => (outcome.Transfer?.Message ?? "The server could not be asked.") + " Nothing was created.",
            AppearanceCopyStatus.TooLarge =>
                $"This appearance's Glamourer text is larger than {Size(AppearanceCopyRules.MaximumTextBytes)}. Nothing was created.",
            AppearanceCopyStatus.InvalidText => "This appearance's Glamourer text is empty or not valid text. Nothing was created.",
            AppearanceCopyStatus.GlamourerUnavailable => "Glamourer is not available (it must be installed and enabled). Nothing was created.",
            AppearanceCopyStatus.GlamourerIncompatible => "The installed Glamourer version is not supported. Nothing was created.",
            AppearanceCopyStatus.DesignListFailed => "Glamourer's design list could not be read, so no name could be chosen safely. Nothing was created.",
            AppearanceCopyStatus.FolderBlocked =>
                $"A Glamourer design named \"{AppearanceCopyNames.Folder}\" is in the way of the folder. Rename or move it in Glamourer. Nothing was created.",
            AppearanceCopyStatus.TooManyCopies => "Too many copies with this name already exist in Glamourer. Nothing was created.",
            AppearanceCopyStatus.Cancelled => "Cancelled. Nothing was created.",
            _ => "Unknown result. Check Glamourer.",
        };
    }

    public static string Title(AppearanceCapturePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Source == AppearanceCaptureSource.SavedDesign
            ? $"Saved Glamourer design \"{plan.DesignName}\" ({StateOnlyLabel})"
            : "Your current character";
    }

    /// <summary>What is uploaded if the owner confirms, and what never leaves the computer.</summary>
    public static string Disclosure(AppearanceCapturePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.StateOnly)
        {
            return "If you confirm, the Glamourer state of this appearance (" + Size(plan.GlamourerStateBytes) +
                ") and its bundle manifest are uploaded to your private RSC server. No Penumbra mod files are captured or uploaded: " +
                "the mod files this appearance relies on are not included and will not be reproduced for the other player.";
        }
        return "If you confirm, these are uploaded to your private RSC server: the actual contents of the " +
            Count(plan.DistinctFiles, "modded file") + " listed below (" + Size(plan.TotalBytes - plan.GlamourerStateBytes) +
            "), their game paths and content hashes, the Glamourer state (" + Size(plan.GlamourerStateBytes) +
            ") and the bundle manifest. Where the files are on your computer (local disk paths) is never sent or logged.";
    }

    /// <summary>Warnings shown with every summary that has them.</summary>
    public static IReadOnlyList<string> Warnings(AppearanceCapturePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var warnings = new List<string>();
        if (plan.Source == AppearanceCaptureSource.SavedDesign)
        {
            warnings.Add("This saved design is captured as Glamourer state only. Its Penumbra mod files are not included.");
            // What the design's Glamourer text still carries.
            warnings.Add(SavedDesignMetadataNotice);
        }
        if (plan.PenumbraMetaExcluded)
            warnings.Add("Penumbra meta changes are not included; the captured appearance may be incomplete.");
        if (plan.SwappedPaths > 0)
            warnings.Add($"{Count(plan.SwappedPaths, "Penumbra file swap")} to other game files {(plan.SwappedPaths == 1 ? "is" : "are")} not included.");
        if (plan.Excluded.Count > 0)
            warnings.Add($"{Count(plan.Excluded.Count, "file")} could not be included (see the list of excluded files).");
        if (SkeletonGroup(plan) is { } group)
            warnings.Add(group);
        if (plan.PreviewOnly)
            warnings.Add("This capture includes a skeleton group. It can be reviewed in this development build, but uploading it is not enabled yet.");
        if (plan.Source == AppearanceCaptureSource.CurrentCharacter)
            warnings.Add("Penumbra reports the files loaded at your character's last redraw. If you changed mods since, redraw in Penumbra and capture again.");
        return warnings;
    }

    public static string Refusal(AppearanceCaptureRefusal refusal, string? conflictingGamePath = null) => refusal switch
    {
        AppearanceCaptureRefusal.None => string.Empty,
        AppearanceCaptureRefusal.NoGlamourerState => "Glamourer returned no state for this appearance.",
        AppearanceCaptureRefusal.GlamourerStateTooLarge => "The Glamourer state is larger than 64 MiB.",
        AppearanceCaptureRefusal.ConflictingGamePath => $"Two different files are loaded for the same game path ({conflictingGamePath}); this appearance cannot be captured.",
        AppearanceCaptureRefusal.TooManyFiles => $"More than {AppearanceLimits.MaximumFiles} files; this appearance cannot be captured.",
        AppearanceCaptureRefusal.BundleTooLarge => "More than 1 GiB of files; this appearance cannot be captured.",
        AppearanceCaptureRefusal.InvalidManifest => "The appearance could not be prepared for your account. Press Reconnect, then capture again.",
        AppearanceCaptureRefusal.WorkingFileUnavailable => "The appearance cache folder could not be written.",
        AppearanceCaptureRefusal.CacheFull => "The local appearance cache has no room within its size limit for this capture (appearances in use take the space, or the cache is over its selected limit).",
        _ => "This appearance cannot be captured.",
    };

    /// <summary>The skeleton group line, or null when there is nothing to say (always, unless skeleton groups may be captured).</summary>
    public static string? SkeletonGroup(AppearanceCapturePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var group = plan.SkeletonGroup;
        switch (group.State)
        {
            case AppearanceSkeletonGroupState.Captured:
                return FormattableString.Invariant($"Skeleton group for race c{group.RaceCode:D4}: {Count(group.Members, "file")}, captured together.");
            case AppearanceSkeletonGroupState.Refused:
                return "Skeleton group not captured: " + SkeletonGroupReason(group) +
                    (group.GamePath is { } path ? " (" + path + ")" : string.Empty) +
                    ". None of its files is included, and the skeleton changes will not be reproduced.";
        }
        return plan.UnsupportedSkeletonDependencies.Count > 0
            ? "Skeleton data this build cannot share is in use and will not be reproduced (" + string.Join(", ", plan.UnsupportedSkeletonDependencies) + ")."
            : null;
    }

    /// <summary>Why a skeleton group was refused, in words.</summary>
    public static string SkeletonGroupReason(AppearanceSkeletonGroupOutcome group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Reason switch
        {
            AppearanceSkeletonGroupReason.TooManyMembers => $"more than {SkeletonResourceGroupAssembler.MaximumMembers} skeleton files",
            AppearanceSkeletonGroupReason.UnsupportedDependency => "it depends on skeleton data this build cannot share",
            AppearanceSkeletonGroupReason.SwappedMember => "a skeleton file is swapped to another game file, which is never shared",
            AppearanceSkeletonGroupReason.DeformerNotAllowed => "the bone deformer cannot be shared by this build",
            AppearanceSkeletonGroupReason.RaceUnknown => "your character's race, clan and gender could not be read",
            AppearanceSkeletonGroupReason.RaceUnconfirmed => "Penumbra did not report one base skeleton for your character, so its race could not be confirmed",
            AppearanceSkeletonGroupReason.RaceMismatch => "Penumbra's base skeleton is for a different race than your character's; redraw and capture again",
            AppearanceSkeletonGroupReason.MemberOutsideModDirectory => "a skeleton file is not inside your Penumbra mod folder",
            AppearanceSkeletonGroupReason.MemberExtensionMismatch => "a skeleton file's type does not match its game path",
            AppearanceSkeletonGroupReason.MemberUnreadable => "a skeleton file could not be read",
            AppearanceSkeletonGroupReason.MemberEmpty => "a skeleton file is empty",
            AppearanceSkeletonGroupReason.MemberTooLarge => "a skeleton file is larger than 4 MiB",
            AppearanceSkeletonGroupReason.Invalid => group.Assembly switch
            {
                SkeletonGroupRefusal.Structure => "a skeleton file is not a valid file of its type",
                SkeletonGroupRefusal.RaceMismatch => "a skeleton file is for a race your character cannot use",
                SkeletonGroupRefusal.InconsistentSlot => "a skeleton and its physics file do not belong together",
                SkeletonGroupRefusal.MoreThanOneGroup => "the files form more than one skeleton",
                _ => "the skeleton files do not form one valid group",
            },
            _ => "the skeleton files could not be checked",
        };
    }

    public static string Exclusion(AppearanceCaptureExclusion reason) => reason switch
    {
        AppearanceCaptureExclusion.TypeNotAllowed => "file type not shared (only .mdl, .mtrl and .tex)",
        AppearanceCaptureExclusion.InvalidGamePath => "game path not supported",
        AppearanceCaptureExclusion.Unreadable => "file could not be read",
        AppearanceCaptureExclusion.Empty => "file is empty",
        AppearanceCaptureExclusion.TooLarge => "file is larger than 64 MiB",
        AppearanceCaptureExclusion.OutsideModDirectory => "file is not inside your Penumbra mod folder",
        AppearanceCaptureExclusion.ExtensionMismatch => "file type does not match the game path",
        AppearanceCaptureExclusion.SkeletonGroupRefused => "skeleton file not included: its skeleton group was not captured",
        AppearanceCaptureExclusion.UnsupportedSkeletonDependency => "skeleton data this build cannot share",
        _ => "not included",
    };

    public static string Read(AppearanceReadStatus status) => status switch
    {
        AppearanceReadStatus.Ok => string.Empty,
        AppearanceReadStatus.Unavailable => "Capture is not available: Penumbra and Glamourer must be installed, enabled and supported.",
        AppearanceReadStatus.PlayerNotFound => "Your character could not be found right now. Try again in a moment.",
        AppearanceReadStatus.StateLocked => "Another plugin has locked your Glamourer state, so it cannot be read.",
        AppearanceReadStatus.DesignNotFound => "That saved design no longer exists.",
        AppearanceReadStatus.InvalidData =>
            "Glamourer returned a design RSC cannot use (it may be too large or in an unexpected format). " +
            "Check the design in Glamourer, then capture again.",
        _ => "Your appearance could not be read.",
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KiB",
        < 1024L * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB",
        _ => (bytes / (1024d * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB",
    };

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
