using System.Globalization;
using System.Text;
using RSyncStageCraft.Core.Protocol;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Core.Appearance;

/// <summary>One design in the recipient's Glamourer: its GUID, name and full path in Glamourer's design tree.</summary>
public readonly record struct GlamourerDesignListing(Guid Id, string Name, string FullPath);

public enum GlamourerCallOutcome
{
    /// <summary>Glamourer answered with a result code.</summary>
    Answered,
    /// <summary>Nothing provides the function (<c>IpcNotReadyError</c>): the call never reached Glamourer.</summary>
    Unavailable,
    /// <summary>The call threw something else: whether Glamourer acted is unknown.</summary>
    Threw,
}

/// <summary>The answer to one <c>Glamourer.AddDesign</c> call.</summary>
public readonly record struct GlamourerAddAnswer(GlamourerCallOutcome Outcome, int Code, Guid Id);

/// <summary>
/// The approved Glamourer calls for "Make a Copy", exactly as the pinned
/// Glamourer.Api 2.8.2 subscribers expose them: <c>Glamourer.ApiVersion.V2</c>,
/// <c>Glamourer.AddDesign</c> and the read-only
/// <c>Glamourer.GetDesignListExtended</c>. Nothing here applies, moves,
/// renames or deletes a design. Framework thread only.
/// </summary>
public interface IGlamourerDesignIpc
{
    /// <summary>Both subscribers are valid and the API passes the existing version gate.</summary>
    AppearanceApiState State();

    /// <summary>Every design (GUID, name, full path), or null if the list could not be read.</summary>
    IReadOnlyList<GlamourerDesignListing>? ListDesigns();

    /// <summary>One <c>Glamourer.AddDesign</c> call; never throws.</summary>
    GlamourerAddAnswer AddDesign(string text, string name);
}

/// <summary>
/// The adapter's gate and call rules, kept free of Dalamud types so they
/// are tested. <c>Valid</c> only means a subscriber exists; a missing provider
/// throws <c>IpcNotReadyError</c> when invoked, which fails closed.
/// </summary>
public static class GlamourerDesignGate
{
    /// <summary>
    /// Unavailable unless every subscriber is valid (the version is then not
    /// asked) and the version call answers; Ready only for the required major
    /// and at least the minimum minor version.
    /// </summary>
    public static AppearanceApiState Evaluate(bool subscribersValid, Func<(int Major, int Minor)> version, int requiredMajor, int minimumMinor)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!subscribersValid)
            return AppearanceApiState.Unavailable;
        try
        {
            var (major, minor) = version();
            return major == requiredMajor && minor >= minimumMinor ? AppearanceApiState.Ready : AppearanceApiState.Incompatible;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return AppearanceApiState.Unavailable;
        }
    }

    /// <summary>
    /// One <c>AddDesign</c> call: its code and GUID, or Unavailable when
    /// <paramref name="notReady"/> recognizes the exception (the call never
    /// reached Glamourer), or Threw for any other exception (unknown whether
    /// Glamourer acted). Never throws, never retries.
    /// </summary>
    public static GlamourerAddAnswer Call(Func<(int Code, Guid Id)> add, Func<Exception, bool> notReady)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(notReady);
        try
        {
            var (code, id) = add();
            return new(GlamourerCallOutcome.Answered, code, id);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(notReady(exception) ? GlamourerCallOutcome.Unavailable : GlamourerCallOutcome.Threw, 0, Guid.Empty);
        }
    }
}

public enum AppearanceCopyStatus
{
    /// <summary>The design was created at <c>RSyncStageCraft/&lt;name&gt;</c>.</summary>
    Created,
    /// <summary>The design was created, but Glamourer placed it somewhere else (or in no folder).</summary>
    CreatedElsewhere,
    /// <summary>It is unknown whether Glamourer created a design; the user must check Glamourer.</summary>
    Uncertain,
    /// <summary>Glamourer could not read the appearance; nothing was created.</summary>
    CouldNotParse,
    /// <summary>A copy is already being made.</summary>
    Busy,
    /// <summary>This account's own id is not known (not connected).</summary>
    OwnerUnknown,
    /// <summary>The entry is this account's own; only other members' entries are copied.</summary>
    OwnEntry,
    /// <summary>The entry is not (or no longer) published to this account.</summary>
    NotPublished,
    /// <summary>The server or the local cache failed (see the transfer result).</summary>
    TransferFailed,
    /// <summary>The Glamourer text is larger than the copy limit.</summary>
    TooLarge,
    /// <summary>The Glamourer text is empty or not valid UTF-8.</summary>
    InvalidText,
    /// <summary>Glamourer is missing, or a required function is not available.</summary>
    GlamourerUnavailable,
    /// <summary>The installed Glamourer API fails the version gate.</summary>
    GlamourerIncompatible,
    /// <summary>Glamourer's design list could not be read before creation.</summary>
    DesignListFailed,
    /// <summary>A design named RSyncStageCraft at the top level blocks the folder.</summary>
    FolderBlocked,
    /// <summary>Every name up to the highest suffix is taken.</summary>
    TooManyCopies,
    /// <summary>Cancelled before Glamourer was called.</summary>
    Cancelled,
}

/// <summary>The outcome of one copy attempt (never carries the Glamourer text or a local path).</summary>
public sealed record AppearanceCopyOutcome(AppearanceCopyStatus Status)
{
    public AppearanceTransferResult? Transfer { get; init; }

    /// <summary>The design's name as RSC requested it.</summary>
    public string? DesignName { get; init; }

    /// <summary>The design's full path in Glamourer's design tree (not a disk path); empty when it is in no folder.</summary>
    public string? FullPath { get; init; }

    public Guid? DesignId { get; init; }

    /// <summary>The GlamourerApiEc code, when Glamourer answered.</summary>
    public int? GlamourerCode { get; init; }

    /// <summary>
    /// Uncertain or unverified only: full paths (in Glamourer's tree; empty
    /// for no folder) of designs that were not in the before snapshot and
    /// carry the requested name. Mentioned for the user to inspect; never
    /// claimed as RSC's, since a user, a Glamourer rule or another plugin may
    /// have created them.
    /// </summary>
    public IReadOnlyList<string> Inspect { get; init; } = [];

    /// <summary>Uncertain only: Glamourer answered Success with an ID, but that design is not (exactly once) in its list afterwards.</summary>
    public bool ReportedSuccess { get; init; }

    public bool CreatedDesign => Status is AppearanceCopyStatus.Created or AppearanceCopyStatus.CreatedElsewhere;
}

/// <summary>
/// The design name and folder rules, grounded in Glamourer 1.7.1.3
/// and Luna dcc4ee7.
/// </summary>
public static class AppearanceCopyNames
{
    /// <summary>The single Glamourer folder every copy goes into.</summary>
    public const string Folder = "RSyncStageCraft";

    /// <summary>The longest design name (without the folder), in UTF-8 bytes.</summary>
    public const int MaximumNameBytes = 64;

    /// <summary>The highest numerical suffix tried.</summary>
    public const int MaximumSuffix = 99;

    /// <summary>
    /// The entry name made safe for Glamourer's design tree: separators become
    /// <c>-</c>; control, format, line/paragraph separator characters and lone
    /// surrogates are removed; trimmed; cut to 64 UTF-8 bytes at a whole
    /// character; a trailing <c>( … )</c> and <c>&lt;None&gt;</c> are escaped;
    /// empty becomes <c>Copy &lt;8&gt;</c>.
    /// </summary>
    public static string Clean(string? entryName, ContentHash bundleId)
    {
        var text = entryName ?? string.Empty;
        var builder = new StringBuilder(text.Length);
        var span = text.AsSpan();
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                // A lone surrogate: removed.
                span = span[Math.Max(consumed, 1)..];
                continue;
            }
            span = span[consumed..];
            if (rune.Value is '/' or '\\')
            {
                builder.Append('-');
                continue;
            }
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate)
                continue;
            builder.Append(rune.ToString());
        }
        var name = Cut(builder.ToString().Trim(), MaximumNameBytes);
        name = EscapeMarkers(name);
        if (name.Length == 0)
            name = "Copy " + (bundleId.IsValid ? bundleId.Value[..8] : "appearance");
        return name;
    }

    /// <summary>
    /// The first free name: <paramref name="cleaned"/>, else <c>cleaned #2</c>
    /// … <c>#99</c> (the base shortened to stay within 64 bytes); null when all
    /// are taken. A name is taken by any design's name, by a design at
    /// <c>RSyncStageCraft/name</c>, or by a sub-folder <c>RSyncStageCraft/name/</c>,
    /// compared like Glamourer does (ordinal, ignoring case).
    /// </summary>
    public static string? Choose(string cleaned, IReadOnlyList<GlamourerDesignListing> designs)
    {
        ArgumentNullException.ThrowIfNull(cleaned);
        ArgumentNullException.ThrowIfNull(designs);
        if (!Taken(cleaned, designs))
            return cleaned;
        for (var suffix = 2; suffix <= MaximumSuffix; suffix++)
        {
            var tail = " #" + suffix.ToString(CultureInfo.InvariantCulture);
            var stem = Cut(cleaned, MaximumNameBytes - Encoding.UTF8.GetByteCount(tail));
            var candidate = stem + tail;
            if (!Taken(candidate, designs))
                return candidate;
        }
        return null;
    }

    /// <summary>A design whose full path is the folder's own name would make Glamourer put the copy at the top level.</summary>
    public static bool FolderBlocked(IReadOnlyList<GlamourerDesignListing> designs)
    {
        ArgumentNullException.ThrowIfNull(designs);
        return designs.Any(design => string.Equals(design.FullPath, Folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The full path Glamourer gives a design created as <c>RSyncStageCraft/name</c> (the folder compared ignoring case).</summary>
    public static bool IsExpectedPath(string? fullPath, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return fullPath is not null && fullPath.Length == Folder.Length + 1 + name.Length &&
            fullPath.AsSpan(0, Folder.Length).Equals(Folder, StringComparison.OrdinalIgnoreCase) &&
            fullPath[Folder.Length] == '/' &&
            fullPath.AsSpan(Folder.Length + 1).SequenceEqual(name);
    }

    private static bool Taken(string name, IReadOnlyList<GlamourerDesignListing> designs)
    {
        var path = Folder + "/" + name;
        var prefix = path + "/";
        return designs.Any(design =>
            string.Equals(design.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(design.FullPath, path, StringComparison.OrdinalIgnoreCase) ||
            (design.FullPath ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>At most <paramref name="maximumBytes"/> UTF-8 bytes, cut at a whole character, then trimmed.</summary>
    private static string Cut(string text, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maximumBytes)
            return text;
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes)
                break;
            bytes += rune.Utf8SequenceLength;
            builder.Append(rune.ToString());
        }
        return builder.ToString().Trim();
    }

    /// <summary>
    /// Glamourer's duplicate marker (a trailing " (…)") and Luna's empty-name
    /// sentinel are escaped with brackets (one byte each, like parentheses).
    /// </summary>
    private static string EscapeMarkers(string name)
    {
        if (name.EndsWith(')'))
        {
            var open = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (open >= 0)
            {
                var characters = name.ToCharArray();
                characters[open + 1] = '[';
                characters[^1] = ']';
                name = new string(characters);
            }
        }
        return string.Equals(name, "<None>", StringComparison.OrdinalIgnoreCase) ? "[None]" : name;
    }
}

/// <summary>The Glamourer text checks and the outcome rules after <c>AddDesign</c>.</summary>
public static class AppearanceCopyRules
{
    /// <summary>The largest Glamourer text copied: 256 KiB of UTF-8.</summary>
    public const int MaximumTextBytes = 256 * 1024;

    /// <summary>GlamourerApiEc values used here.</summary>
    public const int GlamourerSuccess = 0;
    public const int GlamourerCouldNotParse = 8;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The text Glamourer receives: non-empty, at most 256 KiB, strict UTF-8,
    /// and encoding back to exactly <paramref name="bytes"/>. Nothing else
    /// about it is examined.
    /// </summary>
    public static AppearanceCopyStatus? CheckText(byte[] bytes, out string? text)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        text = null;
        if (bytes.Length == 0)
            return AppearanceCopyStatus.InvalidText;
        if (bytes.Length > MaximumTextBytes)
            return AppearanceCopyStatus.TooLarge;
        string decoded;
        try
        {
            decoded = Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return AppearanceCopyStatus.InvalidText;
        }
        byte[] again;
        try
        {
            again = Strict.GetBytes(decoded);
        }
        catch (EncoderFallbackException)
        {
            return AppearanceCopyStatus.InvalidText;
        }
        if (!again.AsSpan().SequenceEqual(bytes))
            return AppearanceCopyStatus.InvalidText;
        text = decoded;
        return null;
    }

    /// <summary>
    /// What happened after one <c>AddDesign</c> call.
    /// The ID Glamourer returns is the only evidence that a design is RSC's:
    /// creation is confirmed only for <c>Success</c> with a non-empty returned
    /// ID that was absent from <paramref name="before"/> and is present in
    /// <paramref name="after"/>. Glamourer 1.7.1.3 returns the new design's ID
    /// only with <c>Success</c> (<c>DesignsApi.AddDesign</c> returns an empty ID
    /// with <c>UnknownError</c>), so every <c>UnknownError</c>, unknown code,
    /// empty or reused ID, and exception is uncertain. Names are never
    /// evidence: a new design with the requested name is only listed in
    /// <see cref="AppearanceCopyOutcome.Inspect"/> for the user to check.
    /// </summary>
    public static AppearanceCopyOutcome Classify(
        GlamourerAddAnswer answer, string name, IReadOnlyList<GlamourerDesignListing> before, IReadOnlyList<GlamourerDesignListing>? after)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(before);
        int? code = answer.Outcome == GlamourerCallOutcome.Answered ? answer.Code : null;
        if (answer.Outcome == GlamourerCallOutcome.Unavailable)
            return new(AppearanceCopyStatus.GlamourerUnavailable) { DesignName = name };
        if (answer.Outcome == GlamourerCallOutcome.Answered && answer.Code == GlamourerCouldNotParse)
            return new(AppearanceCopyStatus.CouldNotParse) { DesignName = name, GlamourerCode = code };

        var known = before.Select(design => design.Id).ToHashSet();
        var inspect = after is null
            ? new List<string>()
            : after.Where(design => !known.Contains(design.Id) && string.Equals(design.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(design => design.FullPath ?? string.Empty)
                .ToList();

        if (answer.Outcome == GlamourerCallOutcome.Answered && answer.Code == GlamourerSuccess &&
            answer.Id != Guid.Empty && !known.Contains(answer.Id))
        {
            var matches = after?.Where(design => design.Id == answer.Id).ToList();
            if (matches is { Count: 1 })
                return new(AppearanceCopyNames.IsExpectedPath(matches[0].FullPath, name) ? AppearanceCopyStatus.Created : AppearanceCopyStatus.CreatedElsewhere)
                {
                    DesignName = name,
                    DesignId = answer.Id,
                    FullPath = matches[0].FullPath ?? string.Empty,
                    GlamourerCode = code,
                };
            // Glamourer reported this ID, but it is not (once) in the list, or
            // the list could not be read: not confirmed, so uncertain.
            return new(AppearanceCopyStatus.Uncertain) { DesignName = name, GlamourerCode = code, ReportedSuccess = true, Inspect = inspect };
        }

        // UnknownError, an unknown code, an empty or reused ID, or an exception.
        return new(AppearanceCopyStatus.Uncertain) { DesignName = name, GlamourerCode = code, Inspect = inspect };
    }
}

/// <summary>
/// "Make a Copy": creates one local
/// Glamourer design from another group member's currently published entry,
/// from its Glamourer text only.
/// <list type="bullet">
/// <item>Only the manifest and the Glamourer blob are read; no Penumbra file
/// is downloaded. Authorization is asked of the server for every attempt, and
/// again immediately before creation; cached data never authorizes.</item>
/// <item>The text is passed to Glamourer unchanged, within 256 KiB of
/// UTF-8.</item>
/// <item>The design list, one <c>AddDesign</c> call and the design list again
/// run in one framework-thread callback. There is never a retry, and RSC never
/// applies, moves, renames or deletes a design.</item>
/// <item>One attempt at a time.</item>
/// </list>
/// </summary>
public sealed class AppearanceCopier
{
    private readonly AppearanceTransferClient client;
    private readonly IGameThreadDispatcher dispatcher;
    private readonly IGlamourerDesignIpc ipc;
    private readonly Func<RscUserId?> self;
    private readonly Func<bool> unloading;
    private readonly Action<string>? log;
    private int running;

    public AppearanceCopier(
        AppearanceTransferClient client, IGameThreadDispatcher dispatcher, IGlamourerDesignIpc ipc, Func<RscUserId?> self,
        Func<bool>? unloading = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(ipc);
        ArgumentNullException.ThrowIfNull(self);
        this.client = client;
        this.dispatcher = dispatcher;
        this.ipc = ipc;
        this.self = self;
        this.unloading = unloading ?? (() => false);
        this.log = log;
    }

    /// <summary>Test seam: awaited at "before-final-check". Null in production.</summary>
    internal Func<string, Task>? TestHook { get; set; }

    public bool Busy => Volatile.Read(ref running) != 0;

    /// <summary>One attempt for one explicit confirmation. A second attempt while one runs is refused.</summary>
    public async Task<AppearanceCopyOutcome> CopyAsync(AppearanceBundleSummary entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return new(AppearanceCopyStatus.Busy);
        try
        {
            var blob = new ContentHash?[1];
            AppearanceCopyOutcome outcome;
            try
            {
                outcome = await CopyCoreAsync(entry, blob, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = new(AppearanceCopyStatus.Cancelled);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Only the steps before creation can throw here (the framework
                // callback catches everything): nothing was created.
                outcome = new(AppearanceCopyStatus.TransferFailed) { Transfer = new(AppearanceTransferStatus.CacheUnavailable) };
            }
            try
            {
                log?.Invoke($"RSC appearance copy: bundle {entry.BundleId.Value}, Glamourer blob {blob[0]?.Value ?? "-"}, " +
                    $"outcome {outcome.Status}, Glamourer code {(outcome.GlamourerCode is { } code ? code.ToString(CultureInfo.InvariantCulture) : "-")}, " +
                    $"design {(outcome.DesignId is { } id ? id.ToString() : "-")}, path {(outcome.FullPath is { } path ? "\"" + path + "\"" : "-")}" +
                    (outcome.Inspect.Count > 0 ? $", {outcome.Inspect.Count} unclaimed new design(s) with the requested name to inspect" : string.Empty) + ".");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A failing log never hides the outcome (it may be a created design).
            }
            return outcome;
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private async Task<AppearanceCopyOutcome> CopyCoreAsync(AppearanceBundleSummary entry, ContentHash?[] blob, CancellationToken cancellationToken)
    {
        if (Refuse(entry) is { } refused)
            return refused;
        if (unloading() || cancellationToken.IsCancellationRequested)
            return new(AppearanceCopyStatus.Cancelled);

        // The manifest (server authorization) and only its Glamourer blob.
        var read = await client.ReadGlamourerStateAsync(entry.BundleId, AppearanceCopyRules.MaximumTextBytes, cancellationToken).ConfigureAwait(false);
        blob[0] = read.Blob;
        if (read.TooLarge)
            return new(AppearanceCopyStatus.TooLarge);
        if (!read.Result.Succeeded || read.Bytes is null)
            return Failed(read.Result);
        if (AppearanceCopyRules.CheckText(read.Bytes, out var text) is { } invalid)
            return new(invalid);

        if (TestHook is { } hook)
            await hook("before-final-check").ConfigureAwait(false);

        // Final authorization, immediately before creation.
        var info = await client.GetBundleAsync(entry.BundleId, cancellationToken).ConfigureAwait(false);
        if (!info.Result.Succeeded || info.Bundle is not { } current)
            return Failed(info.Result);
        if (current.BundleId != entry.BundleId)
            return new(AppearanceCopyStatus.TransferFailed) { Transfer = new(AppearanceTransferStatus.InvalidResponse) };
        if (Refuse(current) is { } nowRefused)
            return nowRefused;
        var cleaned = AppearanceCopyNames.Clean(current.Name, current.BundleId);
        if (unloading() || cancellationToken.IsCancellationRequested)
            return new(AppearanceCopyStatus.Cancelled);

        var attempt = new Attempt();
        try
        {
            return await dispatcher.InvokeAsync(() => CreateOnFramework(text!, cleaned, attempt, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Volatile.Read(ref attempt.Called) ? new(AppearanceCopyStatus.Uncertain) : new(AppearanceCopyStatus.Cancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The dispatcher could not run it (for example while unloading).
            return Volatile.Read(ref attempt.Called) ? new(AppearanceCopyStatus.Uncertain) : new(AppearanceCopyStatus.GlamourerUnavailable);
        }
    }

    /// <summary>Framework thread: the gate, the before list, one AddDesign call and the after list.</summary>
    private AppearanceCopyOutcome CreateOnFramework(string text, string cleaned, Attempt attempt, CancellationToken cancellationToken)
    {
        // The dispatcher runs inline (off the framework thread) once unloading.
        if (unloading() || cancellationToken.IsCancellationRequested)
            return new(AppearanceCopyStatus.Cancelled);
        AppearanceApiState state;
        try
        {
            state = ipc.State();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            state = AppearanceApiState.Unavailable;
        }
        if (state == AppearanceApiState.Unavailable)
            return new(AppearanceCopyStatus.GlamourerUnavailable);
        if (state != AppearanceApiState.Ready)
            return new(AppearanceCopyStatus.GlamourerIncompatible);

        var before = List();
        if (before is null)
            return new(AppearanceCopyStatus.DesignListFailed);
        if (AppearanceCopyNames.FolderBlocked(before))
            return new(AppearanceCopyStatus.FolderBlocked);
        if (AppearanceCopyNames.Choose(cleaned, before) is not { } name)
            return new(AppearanceCopyStatus.TooManyCopies) { DesignName = cleaned };

        Volatile.Write(ref attempt.Called, true);
        GlamourerAddAnswer answer;
        try
        {
            answer = ipc.AddDesign(text, AppearanceCopyNames.Folder + "/" + name);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            answer = new(GlamourerCallOutcome.Threw, 0, Guid.Empty);
        }
        return AppearanceCopyRules.Classify(answer, name, before, List());
    }

    private IReadOnlyList<GlamourerDesignListing>? List()
    {
        try
        {
            return ipc.ListDesigns();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Only another member's published entry, and only while this account's own id is known (fails closed).</summary>
    private AppearanceCopyOutcome? Refuse(AppearanceBundleSummary entry)
    {
        if (self() is not { IsValid: true } me)
            return new(AppearanceCopyStatus.OwnerUnknown);
        if (string.Equals(entry.Owner, me.Value, StringComparison.Ordinal))
            return new(AppearanceCopyStatus.OwnEntry);
        return entry.Published ? null : new(AppearanceCopyStatus.NotPublished);
    }

    private static AppearanceCopyOutcome Failed(AppearanceTransferResult result) => result.Status switch
    {
        AppearanceTransferStatus.Cancelled => new(AppearanceCopyStatus.Cancelled),
        AppearanceTransferStatus.NotFound or AppearanceTransferStatus.Forbidden => new(AppearanceCopyStatus.NotPublished) { Transfer = result },
        _ => new(AppearanceCopyStatus.TransferFailed) { Transfer = result.Succeeded ? new(AppearanceTransferStatus.InvalidResponse) : result },
    };

    private sealed class Attempt
    {
        public bool Called;
    }
}
