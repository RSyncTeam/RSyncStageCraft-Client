using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Protocol;

namespace RSyncStageCraft.Tests;

/// <summary>One request as the fake received it.</summary>
internal sealed record FakeAppearanceRequest(string? Account, string Method, string Host, string Path, bool Authorized)
{
    public override string ToString() => $"{Method} {Path}";
}

/// <summary>
/// An in-memory appearance HTTP API, written from the client's side of the
/// contract: every account it issues a key to is a member of one private
/// group; each account must upload every blob of its own bundles in full; an
/// entry is visible to its owner, and to the other members only while it is
/// published. Uploaded envelopes are decoded and verified before they are
/// kept. Every request is recorded. Shared by several clients: disposing one
/// of them leaves it usable.
/// </summary>
internal sealed class FakeAppearanceService(long quotaBytes = long.MaxValue, int entryLimit = int.MaxValue) : HttpMessageHandler
{
    public const string Host = "appearance.example.test";
    public const string ControlEndpoint = "wss://" + Host + "/rsc/v1/control";
    private const string Prefix = "/rsc/v1/appearance/";
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object gate = new();
    private readonly Dictionary<string, string> accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<ContentHash>> proved = new(StringComparer.Ordinal);
    private readonly Dictionary<ContentHash, (byte[] Envelope, AppearanceBlobCodec Codec)> blobs = [];
    private readonly Dictionary<ContentHash, Entry> entries = [];
    private readonly List<FakeAppearanceRequest> requests = [];
    private int created;

    /// <summary>Answers a request itself (after it is recorded) when it returns a task; null passes it on.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>?>? Intercept { get; set; }

    /// <summary>Test-only: commits format-2 manifests (a skeleton group) too. Off by default, as the format-1 server.</summary>
    public bool AcceptSkeletonGroups { get; set; }

    /// <summary>Flips the last byte of a successful blob download when it returns true.</summary>
    public Func<HttpRequestMessage, bool>? Tamper { get; set; }

    public IReadOnlyList<FakeAppearanceRequest> Requests
    {
        get
        {
            lock (gate)
                return [.. requests];
        }
    }

    /// <summary>An account's requests as "METHOD path" (relative to the appearance address, without the query).</summary>
    public List<string> RequestsOf(string user)
    {
        lock (gate)
            return [.. requests.Where(request => request.Account == user).Select(request => request.ToString())];
    }

    public void ClearRequests()
    {
        lock (gate)
            requests.Clear();
    }

    /// <summary>A well-formed key for <paramref name="user"/> (the same every time); the account joins the group.</summary>
    public string Key(string user)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fake-appearance-key:" + user))).ToLowerInvariant();
        var key = "rsc1." + digest[..32] + "." + digest;
        lock (gate)
            accounts[key] = user;
        return key;
    }

    public ServerConnectionSettings Settings(string user) => new(ControlEndpoint, Key(user), true);

    /// <summary>Whether a verified copy of the blob is held (uploaded in full by any account).</summary>
    public bool Holds(ContentHash hash)
    {
        lock (gate)
            return blobs.ContainsKey(hash);
    }

    /// <summary>The codec of the envelope that was uploaded for the blob.</summary>
    public AppearanceBlobCodec? StoredCodec(ContentHash hash)
    {
        lock (gate)
            return blobs.TryGetValue(hash, out var stored) ? stored.Codec : null;
    }

    public static string Relative(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path.StartsWith(Prefix, StringComparison.Ordinal) ? path[Prefix.Length..] : path;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var authorization = request.Headers.Authorization;
        var authorized = authorization is { Scheme: "Bearer", Parameter.Length: > 0 };
        string? account;
        lock (gate)
        {
            account = authorized ? accounts.GetValueOrDefault(authorization!.Parameter!) : null;
            requests.Add(new(account, request.Method.Method, uri.Host, Relative(request), authorized));
        }
        if (Intercept?.Invoke(request, cancellationToken) is { } answer)
            return await answer;
        if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith(Prefix, StringComparison.Ordinal))
            return Error(HttpStatusCode.NotFound, "notFound");
        if (account is null)
            return Error(HttpStatusCode.Unauthorized, "unauthorized");
        if (uri.Query.Length > 0)
            return Error(HttpStatusCode.BadRequest, "unsupported");

        var parts = Relative(request).Split('/');
        var method = request.Method;
        switch (parts)
        {
            case ["bundles"] when method == HttpMethod.Get:
                return List(account);
            case ["bundles"] when method == HttpMethod.Post:
                return Commit(account, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            case ["bundles", var id] when method == HttpMethod.Get:
                return Info(account, id);
            case ["bundles", var id] when method == HttpMethod.Patch:
                return Edit(account, id, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            case ["bundles", var id] when method == HttpMethod.Delete:
                return Delete(account, id);
            case ["bundles", var id, "manifest"] when method == HttpMethod.Get:
                return Manifest(account, id);
            case ["bundles", var id, "blobs", var hash] when method == HttpMethod.Get:
                return Blob(request, account, id, hash);
            case ["blobs", var hash] when method == HttpMethod.Put:
                return await UploadAsync(request, account, hash, cancellationToken);
            default:
                return Error(HttpStatusCode.NotFound, "notFound");
        }
    }

    /// <summary>The fake outlives each client that uses it.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    private HttpResponseMessage Commit(string account, byte[] body)
    {
        var readOptions = AcceptSkeletonGroups ? AppearanceManifestReadOptions.AllowSkeletonGroups : AppearanceManifestReadOptions.FormatOneOnly;
        if (AppearanceBundleManifest.TryParse(body, readOptions, out _) is not { } manifest || !manifest.CanonicalBytes.AsSpan().SequenceEqual(body))
            return Error(HttpStatusCode.BadRequest, "invalidManifest");
        if (manifest.OwnerId.Value != account)
            return Error(HttpStatusCode.Forbidden, "notOwner");
        lock (gate)
        {
            if (entries.ContainsKey(manifest.BundleId))
                return Json(HttpStatusCode.OK, writer => writer.WriteString("bundleId", manifest.BundleId.Value));
            var own = entries.Values.Where(entry => entry.Owner == account).ToList();
            if (own.Count >= entryLimit)
                return Error((HttpStatusCode)507, "entryLimit");
            var used = own.SelectMany(entry => entry.Manifest.Blobs).Concat(manifest.Blobs).DistinctBy(blob => blob.Hash).Sum(blob => blob.Size);
            if (used > quotaBytes)
                return Error((HttpStatusCode)507, "storageQuota");
            var mine = proved.GetValueOrDefault(account) ?? [];
            var missing = manifest.Blobs.Select(blob => blob.Hash).Distinct().Where(hash => !mine.Contains(hash)).ToList();
            if (missing.Count > 0)
            {
                return Json(HttpStatusCode.Conflict, writer =>
                {
                    writer.WriteString("code", "missingBlobs");
                    writer.WriteStartArray("missing");
                    foreach (var hash in missing)
                        writer.WriteStringValue(hash.Value);
                    writer.WriteEndArray();
                });
            }
            entries[manifest.BundleId] = new Entry(account, manifest, Epoch.AddMinutes(created++));
            return Json(HttpStatusCode.Created, writer => writer.WriteString("bundleId", manifest.BundleId.Value));
        }
    }

    private async Task<HttpResponseMessage> UploadAsync(HttpRequestMessage request, string account, string hashText, CancellationToken cancellationToken)
    {
        if (!ContentHash.TryParse(hashText, out var hash))
            return Error(HttpStatusCode.BadRequest, "invalidHash");
        if (request.Content?.Headers.ContentLength is not { } length || length > AppearanceBlobEnvelopeHeader.MaximumEnvelopeBytes)
            return Error((HttpStatusCode)411, "lengthRequired");
        // The whole body, or the exception that interrupted it: nothing partial is kept.
        using var body = new MemoryStream();
        await request.Content.CopyToAsync(body, cancellationToken);
        var envelope = body.ToArray();
        if (envelope.Length != length ||
            AppearanceBlobEnvelopeHeader.TryRead(envelope, out var header) != AppearanceEnvelopeStatus.Valid ||
            header!.UncompressedHash != hash ||
            await AppearanceBlobEnvelope.DecodeAsync(new MemoryStream(envelope), Stream.Null, header.Blob, cancellationToken)
                is not (AppearanceEnvelopeStatus.Valid, AppearancePayloadStatus.Valid))
            return Error(HttpStatusCode.BadRequest, "blobInvalid");
        lock (gate)
        {
            blobs.TryAdd(hash, (envelope, header.Codec));
            if (!proved.TryGetValue(account, out var mine))
                proved[account] = mine = [];
            mine.Add(hash);
        }
        return Json(HttpStatusCode.Created, writer => writer.WriteString("status", "uploaded"));
    }

    private HttpResponseMessage List(string account)
    {
        lock (gate)
        {
            var visible = entries.Values.Where(entry => Visible(entry, account)).OrderBy(entry => entry.CreatedAt).ToList();
            return Json(HttpStatusCode.OK, writer =>
            {
                writer.WriteStartArray("bundles");
                foreach (var entry in visible)
                    WriteSummary(writer, entry);
                writer.WriteEndArray();
            });
        }
    }

    private HttpResponseMessage Info(string account, string id)
    {
        lock (gate)
        {
            if (Find(account, id) is not { } entry)
                return Error(HttpStatusCode.NotFound, "notFound");
            return Json(HttpStatusCode.OK, writer =>
            {
                writer.WritePropertyName("bundle");
                WriteSummary(writer, entry);
            });
        }
    }

    private HttpResponseMessage Edit(string account, string id, byte[] body)
    {
        JsonElement edit;
        try
        {
            using var document = JsonDocument.Parse(body);
            edit = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error(HttpStatusCode.BadRequest, "invalidMetadata");
        }
        if (edit.ValueKind != JsonValueKind.Object)
            return Error(HttpStatusCode.BadRequest, "invalidMetadata");
        lock (gate)
        {
            if (Find(account, id) is not { } entry || entry.Owner != account)
                return Error(HttpStatusCode.NotFound, "notFound");
            foreach (var property in edit.EnumerateObject())
            {
                switch (property.Name, property.Value.ValueKind)
                {
                    case ("name", JsonValueKind.String):
                        entry.Name = property.Value.GetString()!;
                        break;
                    case ("description", JsonValueKind.String):
                        entry.Description = property.Value.GetString()!;
                        break;
                    case ("folder", JsonValueKind.String):
                        entry.Folder = property.Value.GetString()!;
                        break;
                    case ("published", JsonValueKind.True or JsonValueKind.False):
                        entry.Published = property.Value.GetBoolean();
                        break;
                    default:
                        return Error(HttpStatusCode.BadRequest, "invalidMetadata");
                }
            }
            return Json(HttpStatusCode.OK, writer =>
            {
                writer.WritePropertyName("bundle");
                WriteSummary(writer, entry);
            });
        }
    }

    private HttpResponseMessage Delete(string account, string id)
    {
        lock (gate)
        {
            if (Find(account, id) is not { } entry || entry.Owner != account)
                return Error(HttpStatusCode.NotFound, "notFound");
            entries.Remove(entry.Manifest.BundleId);
            return Json(HttpStatusCode.OK, writer => writer.WriteString("status", "deleted"));
        }
    }

    private HttpResponseMessage Manifest(string account, string id)
    {
        lock (gate)
        {
            return Find(account, id) is { } entry
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(entry.Manifest.CanonicalBytes) }
                : Error(HttpStatusCode.NotFound, "notFound");
        }
    }

    private HttpResponseMessage Blob(HttpRequestMessage request, string account, string id, string hashText)
    {
        byte[] envelope;
        lock (gate)
        {
            if (Find(account, id) is not { } entry || !ContentHash.TryParse(hashText, out var hash) ||
                !entry.Manifest.Blobs.Any(blob => blob.Hash == hash) || !blobs.TryGetValue(hash, out var stored))
                return Error(HttpStatusCode.NotFound, "notFound");
            envelope = stored.Envelope.ToArray();
        }
        if (Tamper?.Invoke(request) == true)
            envelope[^1] ^= 0x01;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(envelope) };
    }

    private Entry? Find(string account, string id) =>
        ContentHash.TryParse(id, out var bundleId) && entries.TryGetValue(bundleId, out var entry) && Visible(entry, account) ? entry : null;

    /// <summary>The owner's own entry, or another group member's published one.</summary>
    private bool Visible(Entry entry, string account) =>
        entry.Owner == account || (entry.Published && accounts.ContainsValue(account) && accounts.ContainsValue(entry.Owner));

    private static void WriteSummary(Utf8JsonWriter writer, Entry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("bundleId", entry.Manifest.BundleId.Value);
        writer.WriteString("owner", entry.Owner);
        writer.WriteNumber("totalBytes", entry.Manifest.TotalBytes);
        writer.WriteNumber("storedBytes", entry.Manifest.TotalBytes);
        writer.WriteString("createdAt", entry.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteBoolean("published", entry.Published);
        writer.WriteString("name", entry.Name);
        writer.WriteString("description", entry.Description);
        writer.WriteString("folder", entry.Folder);
        writer.WriteEndObject();
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code) => Json(status, writer =>
    {
        writer.WriteString("error", code);
        writer.WriteString("code", code);
    });

    private static HttpResponseMessage Json(HttpStatusCode status, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new("application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed class Entry(string owner, AppearanceBundleManifest manifest, DateTimeOffset createdAt)
    {
        public string Owner { get; } = owner;
        public AppearanceBundleManifest Manifest { get; } = manifest;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public bool Published { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
    }
}
