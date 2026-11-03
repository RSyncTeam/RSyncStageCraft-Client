namespace RSyncStageCraft.Core.Appearance;

/// <summary>The outcome of one appearance transfer operation.</summary>
public enum AppearanceTransferStatus
{
    Succeeded,

    /// <summary>Appearance transfer is off: the control endpoint has no derivable appearance address, or no well-formed key is set. Nothing was sent.</summary>
    Disabled,
    /// <summary>The caller cancelled. Nothing was published; temporary files were removed.</summary>
    Cancelled,

    // Retryable: the operation may succeed if the caller tries again later. Nothing is retried automatically.
    /// <summary>The server answered 503 (busy or temporarily unavailable).</summary>
    ServerBusy,
    /// <summary>The connection failed or was interrupted.</summary>
    ConnectionInterrupted,
    /// <summary>No progress within the inactivity timeout (connect, response headers or transfer).</summary>
    TimedOut,

    /// <summary>The server refused the access key (401).</summary>
    InvalidCredentials,
    /// <summary>The server answered with a redirect, which is never followed.</summary>
    Redirected,
    /// <summary>The bundle or blob does not exist or is not visible to this account (404).</summary>
    NotFound,
    /// <summary>Only the bundle's owner may do this (403).</summary>
    Forbidden,
    StorageQuota,
    EntryLimit,
    /// <summary>The server still misses blobs after they were uploaded.</summary>
    MissingBlobs,
    RevisionTargetExists,
    ReplacedNotFound,
    /// <summary>The server refused an upload (its code is in <see cref="AppearanceTransferResult.ServerCode"/>).</summary>
    Refused,
    /// <summary>The entry is still referenced by a scene or catalogue object; it can be unpublished instead.</summary>
    StillReferenced,
    /// <summary>The server refused the metadata (for example a name that is too long).</summary>
    InvalidMetadata,
    /// <summary>The server found the stored blob corrupt (<c>blobCorrupt</c>).</summary>
    BlobCorrupt,
    /// <summary>The server cannot read the stored blob (<c>blobUnavailable</c>).</summary>
    BlobUnavailable,
    /// <summary>Another server error.</summary>
    ServerError,
    /// <summary>The response is not what was asked for (wrong bytes, length, hash, envelope or format). Nothing was published.</summary>
    InvalidResponse,
    /// <summary>A local source file is missing, unreadable, too large, or not the content the manifest names.</summary>
    LocalContentInvalid,
    /// <summary>The local cache could not be read or written.</summary>
    CacheUnavailable,
    /// <summary>
    /// The local cache has no room within its size limit: appearances in
    /// use and files being written leave too little. Nothing was downloaded.
    /// </summary>
    CacheFull,
    /// <summary>
    /// The local cache is over its selected limit (the limit was lowered below
    /// what is in use, or another program added files): new downloads are
    /// paused until it is within the limit. Nothing was downloaded.
    /// </summary>
    CacheOverLimit,
    /// <summary>The appearance changed since its summary; nothing was sent. Capture again.</summary>
    CaptureChanged,
    /// <summary>The capture holds a skeleton group, which can only be previewed; nothing was sent.</summary>
    SkeletonGroupUploadNotEnabled,
    /// <summary>The bundle's skeleton group holds the bone deformer, which this receiver does not accept; nothing was downloaded.</summary>
    SkeletonGroupNotAllowed,
    /// <summary>The bundle's skeleton group is for another race than the character it would be shown on; nothing was downloaded.</summary>
    SkeletonGroupRaceMismatch,
    /// <summary>The bundle's skeleton group failed the receiver's own checks; nothing is held.</summary>
    SkeletonGroupInvalid,
}

/// <summary>
/// A typed transfer result. It never carries exception objects, exception
/// text or free text from the server, so it cannot expose the access key;
/// a server code is kept only when it is a short plain identifier.
/// </summary>
public sealed record AppearanceTransferResult(AppearanceTransferStatus Status, string? ServerCode = null, ContentHash? Blob = null)
{
    public static readonly AppearanceTransferResult Success = new(AppearanceTransferStatus.Succeeded);

    public bool Succeeded => Status == AppearanceTransferStatus.Succeeded;

    /// <summary>
    /// A successful bundle commit found the bundle already in the catalogue
    /// (the server answered 200, not 201): nothing new was created.
    /// </summary>
    public bool AlreadyExisted { get; init; }

    /// <summary>Trying again later may succeed (503, an interrupted connection, a timeout). Nothing retries automatically.</summary>
    public bool IsRetryable => Status is AppearanceTransferStatus.ServerBusy or AppearanceTransferStatus.ConnectionInterrupted or AppearanceTransferStatus.TimedOut;

    public string Message => Status switch
    {
        AppearanceTransferStatus.Succeeded => "Done.",
        AppearanceTransferStatus.Disabled => "Appearance transfer is off: the server address is not a standard RSC address, or no access key is set.",
        AppearanceTransferStatus.Cancelled => "Cancelled.",
        AppearanceTransferStatus.ServerBusy => "The server is busy; try again later.",
        AppearanceTransferStatus.ConnectionInterrupted => "The connection was interrupted; try again later.",
        AppearanceTransferStatus.TimedOut => "The server stopped responding; try again later.",
        AppearanceTransferStatus.InvalidCredentials => "The server did not accept the access key. Press Reconnect.",
        AppearanceTransferStatus.Redirected => "The server answered with a redirect, which is not followed. Ask the host.",
        AppearanceTransferStatus.NotFound => "Not found, or not shared with this account. Refresh the list.",
        AppearanceTransferStatus.Forbidden => "Only the owner may do this. Refresh the list.",
        AppearanceTransferStatus.StorageQuota => "The account's storage quota is full.",
        AppearanceTransferStatus.EntryLimit => "The account has reached its appearance entry limit.",
        AppearanceTransferStatus.MissingBlobs => "The server is still missing files of this appearance.",
        AppearanceTransferStatus.RevisionTargetExists => "The revised appearance already exists as another entry.",
        AppearanceTransferStatus.ReplacedNotFound => "The appearance to replace was not found.",
        AppearanceTransferStatus.Refused => "The server refused the upload. Refresh the list.",
        AppearanceTransferStatus.StillReferenced => "The appearance is still in use; unpublish it instead.",
        AppearanceTransferStatus.InvalidMetadata => "The server refused the name, description or folder.",
        AppearanceTransferStatus.BlobCorrupt => "A file of this appearance is damaged on the server; the owner must upload it again.",
        AppearanceTransferStatus.BlobUnavailable => "A file of this appearance is not available on the server; the owner must upload it again.",
        AppearanceTransferStatus.ServerError => "The server reported an error. Ask the host.",
        AppearanceTransferStatus.InvalidResponse => "The server's answer did not match what was requested. Ask the host.",
        AppearanceTransferStatus.LocalContentInvalid => "A local file is missing, unreadable, or changed.",
        AppearanceTransferStatus.CacheUnavailable => "The local appearance cache could not be used. Free space on the drive or delete unused appearances.",
        AppearanceTransferStatus.CacheFull => "The local appearance cache has no room within its size limit; appearances in use take the space. Free space on the drive or delete unused appearances.",
        AppearanceTransferStatus.CacheOverLimit => "The local appearance cache is over its selected limit, so new downloads are paused until appearances in use are released. Free space on the drive or delete unused appearances.",
        AppearanceTransferStatus.CaptureChanged => "Your appearance changed since the summary. Nothing was sent; capture again.",
        AppearanceTransferStatus.SkeletonGroupUploadNotEnabled => "This capture includes a skeleton group, which can only be previewed in this build. Nothing was sent.",
        AppearanceTransferStatus.SkeletonGroupNotAllowed => "This appearance includes skeleton data this build does not accept. Nothing was downloaded.",
        AppearanceTransferStatus.SkeletonGroupRaceMismatch => "This appearance's skeleton data is for another race than this character. Nothing was downloaded.",
        AppearanceTransferStatus.SkeletonGroupInvalid => "This appearance's skeleton data failed its checks, so none of it is used.",
        _ => "Unknown result.",
    };

    public override string ToString() =>
        Blob is { } blob ? $"{Status}{(ServerCode is null ? "" : $" ({ServerCode})")}: {Message} Blob {blob.Value}." :
        $"{Status}{(ServerCode is null ? "" : $" ({ServerCode})")}: {Message}";

    /// <summary>A server error code if it is a short ASCII identifier, otherwise null (never echoed text).</summary>
    public static string? SafeCode(string? code)
    {
        if (code is null || code.Length is 0 or > 40)
            return null;
        foreach (var character in code)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z')))
                return null;
        }
        return code;
    }
}
