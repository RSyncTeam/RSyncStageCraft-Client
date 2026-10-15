using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Ui;

/// <summary>
/// The Appearances tab of the main window (a window of its own
/// before, hence the name). Appearances: capture the owner's own appearance (read-only), review
/// the disclosure and exclusion summary, upload it as a new entry or an
/// explicit revision, and manage the owner's entries (rename, publish to this
/// private group, unpublish, delete). Other group members' published entries
/// are listed read-only; the server enforces ownership anyway. Nothing is
/// applied to any character or puppet here. Another member's
/// published entry can be copied into the recipient's own Glamourer as a new
/// local design, after an explicit confirmation (one design per confirmation).
/// <para>
/// Threading: work runs on the thread pool one operation at a time; its
/// completion is queued and applied by <see cref="ProcessCompletions"/>,
/// which the main window calls every frame while it is open, so every field
/// below is read and written on the draw thread only.
/// </para>
/// <para>
/// Lifecycle: entering the tab, or opening the main
/// window on it, refreshes the list as opening the window did. Switching to
/// another tab keeps a capture and a running operation. Closing the main
/// window does what closing this window did.
/// </para>
/// </summary>
internal sealed class AppearanceWindow
{
    private const string NewEntryDefaultName = "My appearance";
    private const string DesignEntryDefaultName = "My saved design";
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Waiting = new(0.95f, 0.8f, 0.35f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.4f, 1f);

    private readonly PluginConnection connection;
    private readonly Func<AppearanceGate> gate;
    private readonly Action<string>? log;
    private AppearanceGate allowed = new(true, true, []);
    // The message the current capture set (its review or refusal text), so
    // closing can clear exactly that one when it discards the capture.
    private string? planMessage;
    private readonly ConcurrentQueue<Action> completions = new();
    private readonly Dictionary<ContentHash, int> fileCounts = [];
    private readonly Dictionary<ContentHash, string> renameDrafts = [];
    private string? message;
    private AppearanceUsage? usage;
    private IReadOnlyList<AppearanceBundleSummary> bundles = [];
    private IReadOnlyList<AppearanceDesignEntry> designs = [];
    private AppearanceCapturePlan? plan;
    private CancellationTokenSource? operation;
    private string? busyLabel;
    private Guid? selectedDesign;
    private string newName = NewEntryDefaultName;
    private bool asRevision;
    private ContentHash? revisionTarget;
    private ContentHash? confirmDelete;
    private ContentHash? confirmCopy;
    private bool copyRunning;
    private string? copyResult;
    private bool refreshAfter;

    // Set from the upload's thread: while a commit request is in flight the
    // upload can no longer be cancelled (-1 = no upload phase).
    private int uploadPhase = -1;

    /// <param name="gate">Which actions the plugins allow now.</param>
    internal AppearanceWindow(PluginConnection connection, Func<AppearanceGate> gate, Action<string>? log = null)
    {
        this.connection = connection;
        this.gate = gate;
        this.log = log;
    }

    private bool Busy => busyLabel is not null;

    /// <summary>
    /// Applies finished operations. The main window calls this every frame
    /// while it is open, whichever tab is shown, as the Appearances window's
    /// own draw did while it was open.
    /// </summary>
    internal void ProcessCompletions()
    {
        while (completions.TryDequeue(out var completion))
            completion();
    }

    /// <summary>The tab was entered, or the main window opened on it.</summary>
    internal void Entered()
    {
        ProcessCompletions();
        Refresh();
    }

    internal void Draw()
    {
        ProcessCompletions();
        allowed = gate();
        if (connection.Appearance is not { } authoring)
        {
            ImGui.TextColored(Bad, "Appearances are unavailable: the appearance cache folder could not be prepared.");
            return;
        }

        // Not being connected is shown by the main window's banner.
        var self = connection.Service.SelfUserId;
        if (busyLabel is { } label)
        {
            var phase = Volatile.Read(ref uploadPhase);
            var committing = phase is (int)AppearanceUploadPhase.CheckingServer or (int)AppearanceUploadPhase.Finishing;
            ImGui.TextColored(Waiting, phase switch
            {
                (int)AppearanceUploadPhase.CheckingServer => "Checking with the server...",
                (int)AppearanceUploadPhase.UploadingFiles => "Uploading files (re-read and checked while they are sent)...",
                (int)AppearanceUploadPhase.Finishing => "Finishing upload...",
                _ => label,
            });
            ImGui.SameLine();
            // A commit request already sent cannot be recalled.
            using (ImRaii.Disabled(committing))
            {
                if (ImGui.Button("Cancel"))
                    operation?.Cancel();
            }
        }
        if (message is { } text)
            ImGui.TextWrapped(text);
        if (copyResult is { } result)
            DrawCopyResult(result);

        DrawUsage();
        ImGui.Spacing();
        ImGui.Separator();
        DrawCapture(authoring);
        ImGui.Spacing();
        ImGui.Separator();
        DrawEntries(authoring, self);
    }

    /// <summary>The main window closed: what closing the Appearances window did.</summary>
    internal void MainWindowClosed()
    {
        ProcessCompletions();
        // A capture that was not uploaded is discarded (its working file too),
        // and with it its own review message; no other message.
        if (!Busy && plan is not null)
        {
            DiscardPlan();
            message = AppearanceOperationText.AfterCloseDiscard(message, planMessage);
            planMessage = null;
        }
        // A copy still asking the server is cancelled; once Glamourer is
        // called the attempt finishes on its own (no partial step).
        if (copyRunning)
            operation?.Cancel();
        confirmCopy = null;
    }

    private void DrawUsage()
    {
        using var disabled = ImRaii.Disabled(Busy);
        if (ImGui.Button("Refresh"))
            Refresh();
        ImGui.SameLine();
        if (usage is { } current)
        {
            ImGui.TextUnformatted(
                $"Storage: {AppearanceCaptureText.Size(current.UsedBytes)} of {AppearanceCaptureText.Size(current.QuotaBytes)} used · " +
                $"{current.Entries} of {current.EntryLimit} entries");
        }
        else
        {
            ImGui.TextDisabled("Storage: could not be read (press Refresh).");
        }
    }

    private void DrawCapture(AppearanceAuthoring authoring)
    {
        ImGui.TextUnformatted("Capture");
        ImGui.TextWrapped(AppearanceCaptureText.ReadOnlyNote);
        // One line per missing plugin, saying what it disables (no banner),
        // also while a capture is under review (Make a copy is below).
        foreach (var line in allowed.MissingLines)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(Waiting, line);
            ImGui.PopTextWrapPos();
        }
        if (plan is { } current)
        {
            DrawSummary(authoring, current);
            return;
        }
        using (ImRaii.Disabled(Busy || !allowed.CanCaptureCurrent))
        {
            if (ImGui.Button("Capture my current character"))
                Run("Reading your appearance...", authoring.CaptureCurrentCharacterAsync, OnCaptured);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(Busy || !allowed.CanUseSavedDesigns))
        {
            if (ImGui.Button("Load saved Glamourer designs"))
                Run("Reading your saved designs...", authoring.ListDesignsAsync, OnDesigns);
        }
        if (designs.Count == 0)
            return;
        ImGui.TextDisabled($"A saved design is captured as Glamourer state only ({AppearanceCaptureText.StateOnlyLabel}).");
        using (var child = ImRaii.Child("##appearance-designs", new Vector2(-1, 140), true))
        {
            if (child.Success)
            {
                foreach (var design in designs)
                {
                    using var id = ImRaii.PushId(design.Id.ToString("N"));
                    if (ImGui.Selectable($"{design.Name}##design", selectedDesign == design.Id))
                        selectedDesign = design.Id;
                }
            }
        }
        var chosen = designs.FirstOrDefault(design => design.Id == selectedDesign);
        using (ImRaii.Disabled(Busy || chosen is null || !allowed.CanUseSavedDesigns))
        {
            if (ImGui.Button("Capture design (state only)") && chosen is not null)
                Run("Reading the design...", token => authoring.CaptureDesignAsync(chosen, token), OnCaptured);
        }
    }

    private void DrawSummary(AppearanceAuthoring authoring, AppearanceCapturePlan current)
    {
        ImGui.Spacing();
        ImGui.TextColored(current.StateOnly ? Waiting : Good, AppearanceCaptureText.Title(current));
        if (!current.CanUpload)
        {
            ImGui.TextColored(Bad, AppearanceCaptureText.Refusal(current.Refusal, current.ConflictingGamePath));
            DrawExcluded(current);
            using (ImRaii.Disabled(Busy))
            {
                if (ImGui.Button("Discard"))
                    DiscardPlan();
            }
            return;
        }

        ImGui.TextWrapped(AppearanceCaptureText.Disclosure(current));
        if (!current.StateOnly)
        {
            var types = string.Join(", ", current.FilesByType.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Value} {pair.Key}"));
            ImGui.TextUnformatted($"{current.FileCount} game paths from {current.DistinctFiles} distinct files ({types}); " +
                $"{AppearanceCaptureText.Size(current.TotalBytes)} in total.");
            ImGui.TextDisabled($"Unmodified game files in use (not uploaded): {current.VanillaPaths}");
        }
        foreach (var warning in AppearanceCaptureText.Warnings(current))
            ImGui.TextColored(Waiting, warning);
        if (!current.StateOnly && ImGui.CollapsingHeader($"Files to upload ({current.FileCount})##appearance-included"))
        {
            using var child = ImRaii.Child("##appearance-included-list", new Vector2(-1, 160), true);
            if (child.Success)
            {
                foreach (var file in current.Manifest!.Files)
                    ImGui.TextUnformatted($"{file.GamePath}  ({AppearanceCaptureText.Size(file.Content.Size)})");
            }
        }
        DrawExcluded(current);

        ImGui.Spacing();
        using (ImRaii.Disabled(Busy))
        {
            if (ImGui.RadioButton("New entry", !asRevision))
                asRevision = false;
            ImGui.SameLine();
            if (ImGui.RadioButton("Revision of one of my entries", asRevision))
                asRevision = true;
            if (!asRevision)
            {
                ImGui.SetNextItemWidth(320);
                ImGui.InputText("Name", ref newName, 64);
            }
            else
            {
                DrawRevisionTargets();
            }
            var ready = asRevision ? revisionTarget is not null : AppearanceAuthoring.IsValidName(newName);
            using (ImRaii.Disabled(!ready))
            {
                if (ImGui.Button("Confirm and upload"))
                {
                    var target = asRevision ? AppearanceUploadTarget.Revision(revisionTarget!.Value) : AppearanceUploadTarget.NewEntry(newName);
                    Volatile.Write(ref uploadPhase, -1);
                    Run("Checking that your appearance has not changed...",
                        token => authoring.UploadAsync(current, target, token, phase => Volatile.Write(ref uploadPhase, (int)phase)), OnUploaded);
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Discard"))
                DiscardPlan();
        }
    }

    private void DrawRevisionTargets()
    {
        var self = connection.Service.SelfUserId;
        var mine = bundles.Where(bundle => self is { } user && bundle.Owner == user.Value).ToList();
        if (mine.Count == 0)
        {
            ImGui.TextDisabled("You have no entries to revise.");
            return;
        }
        ImGui.TextDisabled("Choose the entry this revises (it keeps that entry's name and published state):");
        using var child = ImRaii.Child("##appearance-revision", new Vector2(-1, 110), true);
        if (!child.Success)
            return;
        foreach (var bundle in mine)
        {
            using var id = ImRaii.PushId("rev-" + bundle.BundleId.Value);
            if (ImGui.Selectable($"{Label(bundle)}##revision", revisionTarget == bundle.BundleId))
                revisionTarget = bundle.BundleId;
        }
    }

    private static void DrawExcluded(AppearanceCapturePlan current)
    {
        if (current.Excluded.Count == 0 || !ImGui.CollapsingHeader($"Excluded files ({current.Excluded.Count})##appearance-excluded"))
            return;
        using var child = ImRaii.Child("##appearance-excluded-list", new Vector2(-1, 140), true);
        if (!child.Success)
            return;
        foreach (var item in current.Excluded)
            ImGui.TextUnformatted($"{item.GamePath}: {AppearanceCaptureText.Exclusion(item.Reason)}");
    }

    private void DrawEntries(AppearanceAuthoring authoring, RscUserId? self)
    {
        var mine = bundles.Where(bundle => self is { } user && bundle.Owner == user.Value).ToList();
        var others = bundles.Where(bundle => self is not { } user || bundle.Owner != user.Value).ToList();

        ImGui.TextUnformatted($"My appearances ({mine.Count})");
        ImGui.TextDisabled(AppearanceCaptureText.UnpublishNotice);
        foreach (var bundle in mine)
            DrawOwnEntry(authoring, bundle);

        ImGui.Spacing();
        ImGui.TextUnformatted($"Published by others in the group ({others.Count})");
        foreach (var bundle in others)
        {
            using var id = ImRaii.PushId("other-" + bundle.BundleId.Value);
            ImGui.TextUnformatted(Label(bundle));
            ImGui.SameLine();
            ImGui.TextDisabled($"by {OwnerLabel(bundle)} · {AppearanceCaptureText.Size(bundle.TotalBytes)}");
            // Only another member's published entry, and only while this
            // account's own id is known (fails closed otherwise).
            if (connection.AppearanceCopy is not { } copier || self is not { } user || bundle.Owner == user.Value || !bundle.Published)
                continue;
            if (confirmCopy == bundle.BundleId)
            {
                DrawCopyConfirmation(copier, bundle);
            }
            else
            {
                using (ImRaii.Disabled(Busy || !allowed.CanCopy))
                {
                    if (ImGui.Button(AppearanceCaptureText.CopyButton))
                        confirmCopy = bundle.BundleId;
                }
            }
        }
    }

    private void DrawCopyConfirmation(AppearanceCopier copier, AppearanceBundleSummary bundle)
    {
        ImGui.Indent(0f);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted($"Make a copy of \"{Label(bundle)}\" by {OwnerLabel(bundle)} in your Glamourer?");
        foreach (var fact in AppearanceCaptureText.CopyFacts)
            ImGui.TextUnformatted("- " + fact);
        ImGui.PopTextWrapPos();
        using (ImRaii.Disabled(Busy || !allowed.CanCopy))
        {
            if (ImGui.Button("Create the copy"))
            {
                // One confirmation, one attempt: the confirmation closes on its
                // first click, the window runs one operation at a time, and the
                // copier refuses a second attempt while one runs.
                confirmCopy = null;
                copyResult = null;
                copyRunning = true;
                Run("Making a copy in Glamourer...", token => copier.CopyAsync(bundle, token), OnCopied);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##copy"))
            confirmCopy = null;
        ImGui.Unindent(0f);
    }

    private void DrawCopyResult(string result)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(result);
        foreach (var fact in AppearanceCaptureText.CopyFacts)
            ImGui.TextUnformatted("- " + fact);
        ImGui.PopTextWrapPos();
        if (ImGui.Button("Dismiss##copy-result"))
            copyResult = null;
    }

    private void OnCopied(AppearanceCopyOutcome outcome)
    {
        copyRunning = false;
        copyResult = AppearanceCaptureText.CopyResult(outcome);
    }

    private void DrawOwnEntry(AppearanceAuthoring authoring, AppearanceBundleSummary bundle)
    {
        using var id = ImRaii.PushId(bundle.BundleId.Value);
        ImGui.Separator();
        ImGui.TextUnformatted(Label(bundle));
        ImGui.SameLine();
        if (bundle.Published)
            ImGui.TextColored(Good, "published to the group");
        else
            ImGui.TextDisabled("private");
        ImGui.TextDisabled($"{AppearanceCaptureText.Size(bundle.TotalBytes)} · created {bundle.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}");

        using var disabled = ImRaii.Disabled(Busy);
        if (!renameDrafts.TryGetValue(bundle.BundleId, out var draft))
            draft = bundle.Name;
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputText("##name", ref draft, 64))
            renameDrafts[bundle.BundleId] = draft;
        ImGui.SameLine();
        using (ImRaii.Disabled(!AppearanceAuthoring.IsValidName(draft) || draft.Trim() == bundle.Name))
        {
            if (ImGui.Button("Rename"))
            {
                var name = draft.Trim();
                Edit("Renaming...", token => authoring.Client.UpdateBundleAsync(bundle.BundleId, new AppearanceBundleEdit(Name: name), token), "Renamed.");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button(bundle.Published ? "Unpublish" : "Publish to the group"))
        {
            var published = !bundle.Published;
            Edit(published ? "Publishing..." : "Unpublishing...",
                token => authoring.Client.UpdateBundleAsync(bundle.BundleId, new AppearanceBundleEdit(Published: published), token),
                published ? "Published to the group." : "Unpublished. " + AppearanceCaptureText.UnpublishNotice);
        }
        ImGui.SameLine();
        if (confirmDelete == bundle.BundleId)
        {
            if (ImGui.Button("Really delete"))
            {
                confirmDelete = null;
                Edit("Deleting...", token => authoring.Client.DeleteBundleAsync(bundle.BundleId, token), "Deleted.");
            }
            ImGui.SameLine();
            if (ImGui.Button("Keep"))
                confirmDelete = null;
        }
        else if (ImGui.Button("Delete"))
        {
            confirmDelete = bundle.BundleId;
        }
    }

    private string Label(AppearanceBundleSummary bundle)
    {
        var name = bundle.Name.Length > 0 ? bundle.Name : "(unnamed) " + bundle.BundleId.Value[..12];
        return fileCounts.TryGetValue(bundle.BundleId, out var files) && files == 0
            ? $"{name}  [{AppearanceCaptureText.StateOnlyLabel}]"
            : name;
    }

    private string OwnerLabel(AppearanceBundleSummary bundle) =>
        connection.Service.OwnerDisplayName(new RscUserId(bundle.Owner)) ?? bundle.Owner;

    private void OnCaptured(AppearanceCaptureOutcome outcome)
    {
        if (outcome.Status != AppearanceReadStatus.Ok || outcome.Plan is null)
        {
            message = AppearanceCaptureText.Read(outcome.Status);
            return;
        }
        DiscardPlan();
        plan = outcome.Plan;
        // A neutral default: the local design name is selection data and goes
        // to the server only if the owner types it as the entry's name.
        newName = outcome.Plan.Source == AppearanceCaptureSource.SavedDesign ? DesignEntryDefaultName : NewEntryDefaultName;
        asRevision = false;
        revisionTarget = null;
        message = outcome.Plan.CanUpload
            ? "Review what would be uploaded, then confirm or discard."
            : AppearanceCaptureText.Refusal(outcome.Plan.Refusal, outcome.Plan.ConflictingGamePath);
        planMessage = message;
    }

    private void OnDesigns(AppearanceReadResult<IReadOnlyList<AppearanceDesignEntry>> result)
    {
        if (!result.Ok)
        {
            message = AppearanceCaptureText.Read(result.Status);
            return;
        }
        designs = result.Value!.OrderBy(design => design.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        message = designs.Count == 0 ? "You have no saved Glamourer designs." : null;
    }

    private void OnUploaded(AppearanceUploadOutcome outcome)
    {
        if (outcome.Result.Status == AppearanceTransferStatus.CaptureChanged)
        {
            // The summary no longer describes the appearance: it is invalidated.
            DiscardPlan();
            message = outcome.Result.Message;
            return;
        }
        if (!outcome.Result.Succeeded)
        {
            // The capture stays for another try (or discard); nothing was committed.
            message = outcome.Result.Message;
            return;
        }
        DiscardPlan();
        message = outcome.Revision
            ? "Revised. The entry keeps its name and published state."
            : outcome.AlreadyExisted
                ? outcome.NameApplied
                    ? "This appearance was already uploaded but unnamed; it now has the name you chose. Its published state is unchanged."
                    : "This appearance is already one of your entries. Nothing new was created; its name and published state are unchanged."
                : outcome.NameApplied
                    ? "Uploaded. The entry is private until you publish it."
                    : "Uploaded, but the name could not be set; the entry is private and unnamed.";
        refreshAfter = true;
    }

    private void DiscardPlan()
    {
        plan?.Dispose();
        plan = null;
    }

    private void Edit(string label, Func<CancellationToken, Task<AppearanceTransferResult>> action, string success) =>
        Run(label, action, result =>
        {
            message = result.Succeeded ? success : result.Message;
            refreshAfter = true;
        });

    /// <summary>Reloads usage and the entry list (and which entries carry files).</summary>
    private void Refresh()
    {
        if (Busy || connection.Appearance is not { } authoring)
            return;
        var known = fileCounts.Keys.ToHashSet();
        Run("Loading appearances...", async token =>
        {
            var usageRead = await authoring.Client.GetUsageAsync(token).ConfigureAwait(false);
            var list = await authoring.Client.ListBundlesAsync(token).ConfigureAwait(false);
            var counts = new Dictionary<ContentHash, int>();
            if (list.Result.Succeeded)
            {
                foreach (var bundle in list.Bundles.Where(bundle => !known.Contains(bundle.BundleId)))
                {
                    var manifest = await authoring.Client.GetManifestAsync(bundle.BundleId, token).ConfigureAwait(false);
                    if (manifest.Manifest is { } read)
                        counts[bundle.BundleId] = read.Files.Count;
                }
            }
            return (usageRead, list, counts);
        }, loaded =>
        {
            if (loaded.usageRead.Usage is { } current)
                usage = current;
            if (!loaded.list.Result.Succeeded)
            {
                message = loaded.list.Result.Message;
                return;
            }
            bundles = loaded.list.Bundles;
            renameDrafts.Clear();
            var present = bundles.Select(bundle => bundle.BundleId).ToHashSet();
            foreach (var stale in fileCounts.Keys.Where(id => !present.Contains(id)).ToList())
                fileCounts.Remove(stale);
            foreach (var (id, count) in loaded.counts)
                fileCounts[id] = count;
        }, keepMessage: true);
    }

    /// <summary>
    /// Runs one operation at a time off the framework thread; Cancel stops it.
    /// The completion is queued and applied on the draw thread.
    /// </summary>
    private void Run<T>(string label, Func<CancellationToken, Task<T>> action, Action<T> completed, bool keepMessage = false)
    {
        if (busyLabel is not null)
            return;
        busyLabel = label;
        var cancel = new CancellationTokenSource();
        operation = cancel;
        if (!keepMessage)
            message = null;
        _ = Task.Run(async () =>
        {
            Action apply;
            try
            {
                var result = await action(cancel.Token).ConfigureAwait(false);
                apply = () => completed(result);
            }
            catch (OperationCanceledException)
            {
                apply = () => message = "Cancelled.";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Never the exception text (it could name a local path); its
                // type goes to the log.
                var type = exception.GetType().Name;
                apply = () =>
                {
                    log?.Invoke($"RSC appearance operation \"{label}\" failed ({type}).");
                    message = AppearanceOperationText.Failed(copyRunning);
                };
            }
            completions.Enqueue(() =>
            {
                try
                {
                    apply();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    log?.Invoke($"RSC appearance result could not be applied ({exception.GetType().Name}).");
                    message = AppearanceOperationText.Failed(copyRunning);
                }
                finally
                {
                    operation = null;
                    busyLabel = null;
                    copyRunning = false;
                    Volatile.Write(ref uploadPhase, -1);
                    cancel.Dispose();
                }
                // A change was made: reload the list once the operation is over.
                if (refreshAfter)
                {
                    refreshAfter = false;
                    Refresh();
                }
            });
        });
    }
}
