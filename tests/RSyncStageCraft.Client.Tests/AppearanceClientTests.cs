using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RSyncStageCraft.Core.Appearance;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Protocol;
using static RSyncStageCraft.Tests.TestEnvelopes;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The client's appearance transfer (upload, download, cache) against an
/// in-memory appearance service, plus stubbed responses for what a real
/// server never sends.
/// </summary>
public sealed class AppearanceClientTests : IDisposable
{
    private const string RemoteEndpoint = "wss://rsc.example.test/rsc/v1/control";
    private readonly string root = Path.Combine(Path.GetTempPath(), "rsc-appearance-client", Guid.NewGuid().ToString("N"));
    private readonly string key = "rsc1." + Hex(16) + "." + Hex(32);

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

    // ---- Endpoint derivation ------------------------------------------------

    [Theory]
    [InlineData("wss://rsc.example.test/rsc/v1/control", "https://rsc.example.test/rsc/v1/appearance/")]
    [InlineData("wss://rsc.example.test/rsc/v1/control/", "https://rsc.example.test/rsc/v1/appearance/")]
    [InlineData("  wss://rsc.example.test/rsc/v1/control  ", "https://rsc.example.test/rsc/v1/appearance/")]
    [InlineData("wss://rsc.example.test:8443/rsc/v1/control", "https://rsc.example.test:8443/rsc/v1/appearance/")]
    [InlineData("ws://127.0.0.1:5000/rsc/v1/control", "http://127.0.0.1:5000/rsc/v1/appearance/")]
    [InlineData("ws://127.0.0.1:5000/rsc/v1/control/", "http://127.0.0.1:5000/rsc/v1/appearance/")]
    [InlineData("ws://localhost:5000/rsc/v1/control", "http://localhost:5000/rsc/v1/appearance/")]
    [InlineData("ws://[::1]:5000/rsc/v1/control", "http://[::1]:5000/rsc/v1/appearance/")]
    // Plaintext beyond loopback.
    [InlineData("ws://rsc.example.test/rsc/v1/control", null)]
    [InlineData("ws://10.0.0.5/rsc/v1/control", null)]
    [InlineData("ws://localhost.example.test/rsc/v1/control", null)]
    // Query, fragment, user information (also empty ones).
    [InlineData("wss://rsc.example.test/rsc/v1/control?x=1", null)]
    [InlineData("wss://rsc.example.test/rsc/v1/control?", null)]
    [InlineData("wss://rsc.example.test/rsc/v1/control#part", null)]
    [InlineData("wss://rsc.example.test/rsc/v1/control#", null)]
    [InlineData("wss://user@rsc.example.test/rsc/v1/control", null)]
    [InlineData("wss://user:secret@rsc.example.test/rsc/v1/control", null)]
    [InlineData("wss://@rsc.example.test/rsc/v1/control", null)]
    // Anything but the canonical path, with at most one trailing slash.
    [InlineData("wss://rsc.example.test/rsc/v1/control//", null)]
    [InlineData("wss://rsc.example.test/prefix/rsc/v1/control", null)]
    [InlineData("wss://rsc.example.test/rsc/v1/./control", null)]
    [InlineData("wss://rsc.example.test/x/../rsc/v1/control", null)]
    [InlineData("wss://rsc.example.test/RSC/v1/control", null)]
    [InlineData("wss://rsc.example.test/rsc/v1/contro%6C", null)]
    [InlineData("wss://rsc.example.test/rsc/v1/control/extra", null)]
    [InlineData("wss://rsc.example.test/", null)]
    [InlineData("wss://rsc.example.test", null)]
    // Other schemes and non-addresses.
    [InlineData("https://rsc.example.test/rsc/v1/control", null)]
    [InlineData("http://127.0.0.1/rsc/v1/control", null)]
    [InlineData("wss:\\\\rsc.example.test\\rsc\\v1\\control", null)]
    [InlineData("/rsc/v1/control", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheAppearanceAddressIsDerivedStructurallyFromTheControlEndpoint(string? endpoint, string? expected)
    {
        Assert.Equal(expected, AppearanceEndpoint.TryDerive(endpoint)?.AbsoluteUri);
    }

    [Fact]
    public async Task NothingIsSentWhileAppearanceTransferIsDisabled()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("no request may be sent"));
        var manifest = Manifest("alice", Encoding.UTF8.GetBytes("state"));
        foreach (var (endpoint, credential) in new[]
        {
            ("wss://rsc.example.test/rsc/v1/control?x=1", key),
            ("ws://rsc.example.test/rsc/v1/control", key),
            ("wss://rsc.example.test/other", key),
            (RemoteEndpoint, ""),
            (RemoteEndpoint, "rsc1.not-a-key"),
        })
        {
            using var client = Client(handler, endpoint, credential);
            Assert.Equal(AppearanceTransferStatus.Disabled, (await client.DownloadBundleAsync(manifest.BundleId)).Result.Status);
            Assert.Equal(AppearanceTransferStatus.Disabled,
                (await client.UploadBundleAsync(manifest, new Dictionary<ContentHash, string>())).Status);
        }
        // The user turned the connection off: nothing is sent either.
        using (var off = new AppearanceTransferClient(() => new ServerConnectionSettings(RemoteEndpoint, key, false),
            new AppearanceBlobCache(Folder("cache")), null, handler))
        {
            Assert.Equal(AppearanceTransferStatus.Disabled, (await off.DownloadBundleAsync(manifest.BundleId)).Result.Status);
            Assert.Equal(AppearanceTransferStatus.Disabled, (await off.UploadBundleAsync(manifest, new Dictionary<ContentHash, string>())).Status);
        }
        Assert.Empty(handler.Requests);
    }

    // ---- Redirects ----------------------------------------------------------

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task RedirectsAreRefusedWithoutForwardingTheKey(int status)
    {
        // The production handler never follows a redirect on its own.
        using (var production = AppearanceTransferClient.CreateHandler(new AppearanceTransferOptions()))
            Assert.False(production.AllowAutoRedirect);
        var service = new FakeAppearanceService();
        var redirects = 0;
        service.Intercept = (request, _) =>
        {
            Interlocked.Increment(ref redirects);
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("https://elsewhere.example.test" + request.RequestUri!.AbsolutePath);
            return Task.FromResult(response);
        };
        var manifest = Manifest("alice", Encoding.UTF8.GetBytes("state"));
        using var client = ServiceClient(service, "alice");

        var download = await client.DownloadBundleAsync(manifest.BundleId);
        var upload = await client.UploadBundleAsync(manifest, new Dictionary<ContentHash, string>());

        Assert.Equal(AppearanceTransferStatus.Redirected, download.Result.Status);
        Assert.Equal(AppearanceTransferStatus.Redirected, upload.Status);
        Assert.Equal(2, redirects);
        // Nothing was sent to the redirect target, with or without the key.
        Assert.Equal(2, service.Requests.Count);
        Assert.All(service.Requests, request => Assert.Equal(FakeAppearanceService.Host, request.Host));
    }

    // ---- End to end against the fake service --------------------------------

    [Fact]
    public async Task AnOwnerUploadsAndAnotherGroupMemberDownloadsOnlyAfterPublishing()
    {
        var service = new FakeAppearanceService();
        var (manifest, sources, contents) = Bundle("alice");
        using var alice = ServiceClient(service, "alice");
        using var bob = ServiceClient(service, "bob");

        Assert.Equal(AppearanceTransferStatus.Succeeded, (await alice.UploadBundleAsync(manifest, sources)).Status);
        // The 5% rule: compressible content travels as Brotli, random content raw.
        Assert.Equal(AppearanceBlobCodec.Brotli, service.StoredCodec(ContentHash.Of(contents["model"])));
        Assert.Equal(AppearanceBlobCodec.Raw, service.StoredCodec(ContentHash.Of(contents["texture"])));
        // Committing the same bundle again is fine.
        Assert.Equal(AppearanceTransferStatus.Succeeded, (await alice.UploadBundleAsync(manifest, sources)).Status);

        // Private until the owner publishes it, even knowing every hash.
        Assert.Equal(AppearanceTransferStatus.NotFound, (await bob.DownloadBundleAsync(manifest.BundleId)).Result.Status);
        Assert.Empty(Directory.EnumerateFiles(bob.Cache.BlobsDirectory, "*", SearchOption.AllDirectories));
        Assert.True((await alice.UpdateBundleAsync(manifest.BundleId, new AppearanceBundleEdit(Published: true))).Succeeded);

        var download = await bob.DownloadBundleAsync(manifest.BundleId);
        Assert.Equal(AppearanceTransferStatus.Succeeded, download.Result.Status);
        Assert.Equal(manifest.CanonicalBytes, download.ManifestBytes);
        Assert.Equal(manifest.BundleId, download.Manifest!.BundleId);
        foreach (var content in contents.Values)
        {
            var (state, path) = await bob.Cache.TryGetAsync(Ref(content));
            Assert.Equal(AppearanceCacheState.Present, state);
            Assert.Equal(content, await File.ReadAllBytesAsync(path!));
        }
        Assert.Empty(Directory.EnumerateFiles(bob.Cache.TemporaryDirectory));
        Assert.Empty(Directory.EnumerateFiles(alice.Cache.TemporaryDirectory));
        // Everything is cached now: a second download needs only the manifest.
        service.ClearRequests();
        Assert.Equal(AppearanceTransferStatus.Succeeded, (await bob.DownloadBundleAsync(manifest.BundleId)).Result.Status);
        Assert.Equal(new[] { $"GET bundles/{manifest.BundleId.Value}/manifest" }, service.RequestsOf("bob"));
    }

    [Fact]
    public async Task AKnownHashIsStillUploadedInFullByAnotherAccount()
    {
        var service = new FakeAppearanceService();
        var (manifest, sources, contents) = Bundle("alice");
        using var alice = ServiceClient(service, "alice");
        Assert.Equal(AppearanceTransferStatus.Succeeded, (await alice.UploadBundleAsync(manifest, sources)).Status);

        // Bob's own bundle of the same content: the service holds every blob,
        // but Bob must still prove the content by uploading each one in full.
        var bobManifest = AppearanceBundleManifest.TryCreate(new RscUserId("bob"), manifest.Glamourer, null, null, manifest.Files, out _)!;
        var counting = new CountingHandler(service);
        using var bob = new AppearanceTransferClient(() => service.Settings("bob"), new AppearanceBlobCache(Folder("bob-cache")), null, counting);
        Assert.Equal(AppearanceTransferStatus.Succeeded, (await bob.UploadBundleAsync(bobManifest, sources)).Status);
        Assert.Equal(contents.Count, counting.Uploads.Count);
        foreach (var content in contents.Values)
            Assert.Contains(counting.Uploads, upload => upload.Hash == ContentHash.Of(content).Value && upload.Length >= AppearanceBlobEnvelopeHeader.Length + 1);
    }

    [Theory]
    [InlineData("encode")]
    [InlineData("upload")]
    public async Task CancellingDuringCompressionOrUploadSendsNoBlobAndLeavesNoTemporaryFiles(string point)
    {
        var service = new FakeAppearanceService();
        // Every blob is large and incompressible, so the cancellation after the
        // first chunk always comes before the whole body was sent.
        var (manifest, sources, contents) = Bundle("alice", compressible: false);
        Assert.All(contents.Values, content => Assert.True(content.Length > 2 * 81920));
        using var alice = ServiceClient(service, "alice");
        using var cancel = new CancellationTokenSource();
        var reached = 0;
        alice.TestHook = async hook =>
        {
            if (hook == point && Interlocked.Exchange(ref reached, 1) == 0)
                await cancel.CancelAsync();
        };

        var result = await alice.UploadBundleAsync(manifest, sources, cancellationToken: cancel.Token);

        Assert.Equal(1, reached);
        Assert.Equal(AppearanceTransferStatus.Cancelled, result.Status);
        Assert.Empty(Directory.EnumerateFiles(alice.Cache.TemporaryDirectory));
        // Nothing complete reached the service, so nothing was stored there.
        foreach (var content in contents.Values)
            Assert.False(service.Holds(ContentHash.Of(content)));
    }

    [Theory]
    [InlineData("download")]
    [InlineData("decode")]
    public async Task CancellingDuringDownloadOrDecodePublishesNothing(string point)
    {
        var service = new FakeAppearanceService();
        var (manifest, sources, _) = Bundle("alice");
        using var alice = ServiceClient(service, "alice");
        Assert.Equal(AppearanceTransferStatus.Succeeded, (await alice.UploadBundleAsync(manifest, sources)).Status);
        Assert.True((await alice.UpdateBundleAsync(manifest.BundleId, new AppearanceBundleEdit(Published: true))).Succeeded);
        using var bob = ServiceClient(service, "bob");
        using var cancel = new CancellationTokenSource();
        var reached = 0;
        bob.TestHook = async hook =>
        {
            if (hook == point && Interlocked.Exchange(ref reached, 1) == 0)
                await cancel.CancelAsync();
        };

        var result = await bob.DownloadBundleAsync(manifest.BundleId, cancel.Token);

        Assert.Equal(1, reached);
        Assert.Equal(AppearanceTransferStatus.Cancelled, result.Result.Status);
        Assert.Empty(Directory.EnumerateFiles(bob.Cache.BlobsDirectory, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(bob.Cache.TemporaryDirectory));
    }

    [Fact]
    public async Task QuotaEntryLimitAndLocalContentProblemsAreTypedResults()
    {
        var small = new FakeAppearanceService(quotaBytes: 1000);
        var (manifest, sources, _) = Bundle("alice");
        using (var alice = ServiceClient(small, "alice"))
        {
            var result = await alice.UploadBundleAsync(manifest, sources);
            Assert.Equal(AppearanceTransferStatus.StorageQuota, result.Status);
            Assert.Equal("storageQuota", result.ServerCode);
        }

        var service = new FakeAppearanceService(entryLimit: 1);
        using var client = ServiceClient(service, "alice");
        var (first, firstSources, _) = Bundle("alice", "first");
        Assert.Equal(AppearanceTransferStatus.Succeeded, (await client.UploadBundleAsync(first, firstSources)).Status);
        var (second, secondSources, secondContents) = Bundle("alice", "second");
        var limited = await client.UploadBundleAsync(second, secondSources);
        Assert.Equal(AppearanceTransferStatus.EntryLimit, limited.Status);

        // A source that is not exactly the manifest's content is refused before it is sent.
        var other = new FakeAppearanceService();
        using var owner = ServiceClient(other, "alice");
        var changed = secondSources.ToDictionary(pair => pair.Key, pair => pair.Value);
        var texture = ContentHash.Of(secondContents["texture"]);
        var altered = secondContents["texture"].ToArray();
        altered[10] ^= 0xFF;
        var alteredPath = Path.Combine(Folder("altered"), "texture.tex");
        await File.WriteAllBytesAsync(alteredPath, altered);
        changed[texture] = alteredPath;
        var refused = await owner.UploadBundleAsync(second, changed);
        Assert.Equal(AppearanceTransferStatus.LocalContentInvalid, refused.Status);
        Assert.Equal(texture, refused.Blob);
        Assert.False(other.Holds(texture));
        changed[texture] = Path.Combine(Folder("altered"), "absent.tex");
        Assert.Equal(AppearanceTransferStatus.LocalContentInvalid, (await owner.UploadBundleAsync(second, changed)).Status);
        changed.Remove(texture);
        Assert.Equal(AppearanceTransferStatus.LocalContentInvalid, (await owner.UploadBundleAsync(second, changed)).Status);
        Assert.Empty(Directory.EnumerateFiles(owner.Cache.TemporaryDirectory));
        Assert.DoesNotContain($"PUT blobs/{texture.Value}", other.RequestsOf("alice"));
    }

    // ---- Stubbed responses -------------------------------------------------

    [Theory]
    [InlineData("exact-declared", AppearanceTransferStatus.Succeeded)]
    [InlineData("exact-absent", AppearanceTransferStatus.Succeeded)]
    [InlineData("short-declared", AppearanceTransferStatus.InvalidResponse)]
    [InlineData("short-absent", AppearanceTransferStatus.InvalidResponse)]
    [InlineData("long-declared", AppearanceTransferStatus.InvalidResponse)]
    [InlineData("long-absent", AppearanceTransferStatus.InvalidResponse)]
    [InlineData("declared-shorter-than-body", AppearanceTransferStatus.InvalidResponse)]
    [InlineData("declared-above-cap", AppearanceTransferStatus.InvalidResponse)]
    [InlineData("declared-below-header", AppearanceTransferStatus.InvalidResponse)]
    [InlineData("endless-absent", AppearanceTransferStatus.InvalidResponse)]
    [InlineData("endless-garbage", AppearanceTransferStatus.InvalidResponse)]
    public async Task ResponseLengthsAreEnforcedUnderTheStreamingCap(string shape, AppearanceTransferStatus expected)
    {
        var raw = RandomNumberGenerator.GetBytes(120_000);
        var manifest = Manifest("alice", raw);
        var envelope = RawEnvelope(raw);
        CountingStream? body = null;
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal))
                return Task.FromResult(Bytes(manifest.CanonicalBytes));
            (byte[] Content, long? Declared, bool Endless) spec = shape switch
            {
                "exact-declared" => (envelope, envelope.Length, false),
                "exact-absent" => (envelope, null, false),
                "short-declared" => (envelope[..^10], envelope.Length, false),
                "short-absent" => (envelope[..^10], null, false),
                "long-declared" => (envelope.Concat(new byte[10]).ToArray(), envelope.Length + 10, false),
                "long-absent" => (envelope.Concat(new byte[10]).ToArray(), null, false),
                "declared-shorter-than-body" => (envelope, envelope.Length - 10, false),
                "declared-above-cap" => (envelope, AppearanceBlobEnvelopeHeader.Length + raw.Length + 1, true),
                "declared-below-header" => (envelope, 10, false),
                "endless-absent" => (envelope, null, true),
                "endless-garbage" => (Encoding.ASCII.GetBytes("not an envelope"), null, true),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
            body = new CountingStream(spec.Content, spec.Endless);
            return Task.FromResult(Streamed(body, spec.Declared));
        });
        using var client = Client(handler, RemoteEndpoint, key);

        var result = await client.DownloadBundleAsync(manifest.BundleId);

        Assert.Equal(expected, result.Result.Status);
        Assert.NotNull(body);
        // Never read past the cap (one envelope plus one read buffer).
        Assert.True(body!.BytesRead <= envelope.Length + 81920 + 1, $"read {body.BytesRead}");
        if (shape == "declared-above-cap")
            Assert.Equal(0, body.BytesRead);
        var published = File.Exists(client.Cache.PathOf(ContentHash.Of(raw)));
        Assert.Equal(expected == AppearanceTransferStatus.Succeeded, published);
        if (published)
            Assert.Equal(raw, await File.ReadAllBytesAsync(client.Cache.PathOf(ContentHash.Of(raw))));
        Assert.Empty(Directory.EnumerateFiles(client.Cache.TemporaryDirectory));
    }

    [Fact]
    public async Task OnlyTheServersExactCanonicalManifestBytesAreAccepted()
    {
        var raw = Encoding.UTF8.GetBytes("glamourer state for the canonical test");
        var manifest = Manifest("alice", raw);
        var canonical = manifest.CanonicalBytes;
        using var document = JsonDocument.Parse(canonical);
        var indented = JsonSerializer.SerializeToUtf8Bytes(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        var trailing = canonical.Append((byte)'\n').ToArray();
        var other = Manifest("alice", Encoding.UTF8.GetBytes("another state")).CanonicalBytes;
        Assert.NotEqual(canonical, indented);

        foreach (var (requested, served, expected) in new (ContentHash, byte[], AppearanceTransferStatus)[]
        {
            (manifest.BundleId, canonical, AppearanceTransferStatus.Succeeded),
            (manifest.BundleId, indented, AppearanceTransferStatus.InvalidResponse),
            (ContentHash.Of(indented), indented, AppearanceTransferStatus.InvalidResponse),
            (manifest.BundleId, trailing, AppearanceTransferStatus.InvalidResponse),
            (ContentHash.Of(trailing), trailing, AppearanceTransferStatus.InvalidResponse),
            (manifest.BundleId, other, AppearanceTransferStatus.InvalidResponse),
        })
        {
            var blobRequests = 0;
            var handler = new StubHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal))
                    return Task.FromResult(Bytes(served));
                Interlocked.Increment(ref blobRequests);
                return Task.FromResult(Bytes(RawEnvelope(raw)));
            });
            using var client = Client(handler, RemoteEndpoint, key);
            var result = await client.DownloadBundleAsync(requested);
            Assert.Equal(expected, result.Result.Status);
            if (expected == AppearanceTransferStatus.Succeeded)
            {
                Assert.Equal(canonical, result.ManifestBytes);
                Assert.Equal(1, blobRequests);
            }
            else
            {
                Assert.Null(result.Manifest);
                Assert.Equal(0, blobRequests);
            }
        }
    }

    [Fact]
    public async Task TheKeyIsNeverExposedThroughExceptionsOrServerText()
    {
        var secret = key.Split('.')[2];
        var raw = Encoding.UTF8.GetBytes("state");
        var manifest = Manifest("alice", raw);
        var source = Path.Combine(Folder("sources"), "state");
        await File.WriteAllBytesAsync(source, raw);
        var sources = new Dictionary<ContentHash, string> { [ContentHash.Of(raw)] = source };
        var results = new List<AppearanceTransferResult>();

        async Task Run(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            var handler = new StubHandler((request, _) => respond(request));
            using var client = Client(handler, RemoteEndpoint, key);
            results.Add((await client.DownloadBundleAsync(manifest.BundleId)).Result);
            results.Add(await client.UploadBundleAsync(manifest, sources));
        }

        // Nested transport exceptions whose text contains the key.
        await Run(_ => throw new HttpRequestException($"failed with {key}", new IOException($"inner {key}", new InvalidOperationException(secret))));
        Assert.All(results, result => Assert.Equal(AppearanceTransferStatus.ConnectionInterrupted, result.Status));
        // A body that fails mid-read with the key in its text.
        await Run(_ => Task.FromResult(Streamed(new FailingStream(key), null)));
        // Server text and codes echoing the key are never kept.
        await Run(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, $$"""{"error":"bad {{key}}","code":"{{secret}}"}""")));
        await Run(_ => Task.FromResult(Json(HttpStatusCode.Unauthorized, $$"""{"error":"{{key}}"}""")));
        await Run(_ => Task.FromResult(Json(HttpStatusCode.InternalServerError, $$"""{"error":"{{key}}","code":"blobCorrupt"}""")));

        Assert.Equal(10, results.Count);
        Assert.Contains(results, result => result.Status == AppearanceTransferStatus.Refused && result.ServerCode is null);
        Assert.Contains(results, result => result.Status == AppearanceTransferStatus.InvalidCredentials);
        Assert.Contains(results, result => result.ServerCode == "blobCorrupt");
        foreach (var result in results)
        {
            foreach (var text in new[] { result.ToString(), result.Message, result.ServerCode ?? string.Empty })
            {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(key, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task BusyInterruptedAndStalledTransfersAreRetryableResultsWithoutAutomaticRetries()
    {
        var raw = RandomNumberGenerator.GetBytes(100_000);
        var manifest = Manifest("alice", raw);
        var source = Path.Combine(Folder("sources"), "state");
        await File.WriteAllBytesAsync(source, raw);
        var sources = new Dictionary<ContentHash, string> { [ContentHash.Of(raw)] = source };
        var fast = new AppearanceTransferOptions { InactivityTimeout = TimeSpan.FromMilliseconds(300) };

        var busy = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, """{"error":"busy"}""")));
        using (var client = Client(busy, RemoteEndpoint, key))
        {
            var result = (await client.DownloadBundleAsync(manifest.BundleId)).Result;
            Assert.Equal(AppearanceTransferStatus.ServerBusy, result.Status);
            Assert.True(result.IsRetryable);
        }
        Assert.Single(busy.Requests);

        var broken = new StubHandler((_, _) => throw new HttpRequestException("reset"));
        using (var client = Client(broken, RemoteEndpoint, key))
        {
            var result = await client.UploadBundleAsync(manifest, sources);
            Assert.Equal(AppearanceTransferStatus.ConnectionInterrupted, result.Status);
            Assert.True(result.IsRetryable);
        }
        Assert.Single(broken.Requests);

        var stalled = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using (var client = Client(stalled, RemoteEndpoint, key, fast))
        {
            var result = (await client.DownloadBundleAsync(manifest.BundleId)).Result;
            Assert.Equal(AppearanceTransferStatus.TimedOut, result.Status);
            Assert.True(result.IsRetryable);
        }
        Assert.Single(stalled.Requests);

        // A handler that tries to send an upload body twice: the second attempt
        // fails instead of replaying it, and nothing more is sent.
        long firstCopy = 0;
        var replaying = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
                return Json(HttpStatusCode.Conflict, $$"""{"error":"blobs missing","code":"missingBlobs","missing":["{{ContentHash.Of(raw).Value}}"]}""");
            var sink = new MemoryStream();
            await request.Content!.CopyToAsync(sink, cancellationToken);
            firstCopy = sink.Length;
            await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            return Json(HttpStatusCode.Created, """{"status":"uploaded"}""");
        });
        using (var client = Client(replaying, RemoteEndpoint, key))
        {
            var result = await client.UploadBundleAsync(manifest, sources);
            Assert.Equal(AppearanceTransferStatus.ConnectionInterrupted, result.Status);
            Assert.True(result.IsRetryable);
            Assert.Equal(ContentHash.Of(raw), result.Blob);
            Assert.Empty(Directory.EnumerateFiles(client.Cache.TemporaryDirectory));
        }
        Assert.Equal(AppearanceBlobEnvelopeHeader.Length + raw.Length, firstCopy);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Put }, replaying.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task ASlowButProgressingUploadAndASlowAnswerAfterItAreNotTimedOut()
    {
        var raw = RandomNumberGenerator.GetBytes(300_000);
        var manifest = Manifest("alice", raw);
        var source = Path.Combine(Folder("sources"), "state");
        await File.WriteAllBytesAsync(source, raw);
        var commits = 0;
        long received = 0;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Interlocked.Increment(ref commits) == 1
                    ? Json(HttpStatusCode.Conflict, $$"""{"code":"missingBlobs","missing":["{{ContentHash.Of(raw).Value}}"]}""")
                    : Json(HttpStatusCode.Created, $$"""{"bundleId":"{{manifest.BundleId.Value}}"}""");
            }
            // The body arrives slowly (each write takes a while), then the
            // server takes a while to answer; each pause is shorter than the
            // inactivity timeout, the total is several times longer.
            var sink = new SlowSink(TimeSpan.FromMilliseconds(500));
            await request.Content!.CopyToAsync(sink, cancellationToken);
            received = sink.Length;
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
            return Json(HttpStatusCode.Created, """{"status":"uploaded"}""");
        });
        using var client = Client(handler, RemoteEndpoint, key, new AppearanceTransferOptions { InactivityTimeout = TimeSpan.FromSeconds(2.5) });
        var started = DateTime.UtcNow;
        var result = await client.UploadBundleAsync(manifest, new Dictionary<ContentHash, string> { [ContentHash.Of(raw)] = source });
        Assert.Equal(AppearanceTransferStatus.Succeeded, result.Status);
        Assert.Equal(AppearanceBlobEnvelopeHeader.Length + raw.Length, received);
        Assert.True(DateTime.UtcNow - started > TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task ASlowButProgressingDownloadIsNotTimedOut()
    {
        var raw = RandomNumberGenerator.GetBytes(150_000);
        var manifest = Manifest("alice", raw);
        var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal)
                ? Bytes(manifest.CanonicalBytes)
                : Streamed(new TrickleStream(RawEnvelope(raw), chunk: 20_000, delay: TimeSpan.FromMilliseconds(400)), null)));
        // Each pause is far below the inactivity timeout (a loaded runner may
        // delay continuations); the whole transfer is longer than it.
        using var client = Client(handler, RemoteEndpoint, key, new AppearanceTransferOptions { InactivityTimeout = TimeSpan.FromSeconds(2) });
        var started = DateTime.UtcNow;
        var result = await client.DownloadBundleAsync(manifest.BundleId);
        Assert.Equal(AppearanceTransferStatus.Succeeded, result.Result.Status);
        Assert.True(DateTime.UtcNow - started > TimeSpan.FromSeconds(2));
    }

    // ---- Helpers -------------------------------------------------------------

    private string Folder(string name)
    {
        var path = Path.Combine(root, name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private AppearanceTransferClient Client(HttpMessageHandler handler, string endpoint, string credential, AppearanceTransferOptions? options = null) =>
        new(() => new ServerConnectionSettings(endpoint, credential, true), new AppearanceBlobCache(Folder("cache")), options, handler);

    private AppearanceTransferClient ServiceClient(FakeAppearanceService service, string user) =>
        new(() => service.Settings(user), new AppearanceBlobCache(Folder(user + "-cache")), null, service);

    private static AppearanceBundleManifest Manifest(string owner, byte[] glamourer)
    {
        var manifest = AppearanceBundleManifest.TryCreate(new RscUserId(owner), Ref(glamourer), null, null, [], out var error);
        Assert.Null(error);
        return manifest!;
    }

    /// <summary>A bundle with compressible and incompressible files written to local source files.</summary>
    private (AppearanceBundleManifest Manifest, Dictionary<ContentHash, string> Sources, Dictionary<string, byte[]> Contents) Bundle(
        string owner, string label = "look", bool compressible = true)
    {
        var contents = new Dictionary<string, byte[]>
        {
            ["glamourer"] = RandomNumberGenerator.GetBytes(300_000),
            ["model"] = compressible
                ? Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat($"chara/{label}/model vertex data ", 8000)))
                : RandomNumberGenerator.GetBytes(260_000),
            ["texture"] = RandomNumberGenerator.GetBytes(250_000),
        };
        var folder = Folder(owner + "-" + label);
        var sources = new Dictionary<ContentHash, string>();
        foreach (var (name, content) in contents)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, content);
            sources[ContentHash.Of(content)] = path;
        }
        var manifest = AppearanceBundleManifest.TryCreate(new RscUserId(owner), Ref(contents["glamourer"]), null, null,
        [
            new AppearanceFileEntry($"chara/{label}/body.mdl", Ref(contents["model"])),
            new AppearanceFileEntry($"chara/{label}/body.tex", Ref(contents["texture"])),
        ], out var error);
        Assert.Null(error);
        return (manifest!, sources, contents);
    }

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private static HttpResponseMessage Streamed(Stream body, long? declared)
    {
        var content = new StreamContent(body);
        if (declared is { } length)
            content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Hex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return respond(request, cancellationToken);
        }
    }

    /// <summary>Records each upload's blob hash and declared length, then passes the request on.</summary>
    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(string Hash, long Length)> Uploads { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                lock (Uploads)
                    Uploads.Add((request.RequestUri!.Segments[^1], request.Content!.Headers.ContentLength ?? -1));
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>A non-seekable body (no computed length) that counts what was read; optionally endless.</summary>
    private sealed class CountingStream(byte[] content, bool endless) : Stream
    {
        private int position;

        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = 0;
            if (position < content.Length)
            {
                read = Math.Min(count, content.Length - position);
                Array.Copy(content, position, buffer, offset, read);
                position += read;
            }
            else if (endless)
            {
                read = count;
                Array.Fill(buffer, (byte)0xAB, offset, count);
            }
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = new byte[buffer.Length];
            var read = Read(array, 0, array.Length);
            array.AsSpan(0, read).CopyTo(buffer.Span);
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Delivers a body slowly, in chunks with a pause before each.</summary>
    private sealed class TrickleStream(byte[] content, int chunk, TimeSpan delay) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position >= content.Length)
                return 0;
            await Task.Delay(delay, cancellationToken);
            var read = Math.Min(Math.Min(chunk, buffer.Length), content.Length - position);
            content.AsMemory(position, read).CopyTo(buffer);
            position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A write-only sink that pauses on every write.</summary>
    private sealed class SlowSink(TimeSpan delay) : Stream
    {
        private long length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            length += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A body that fails on its first read with the key in the exception text.</summary>
    private sealed class FailingStream(string secret) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("broken " + secret, new HttpRequestException(secret));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("broken " + secret, new HttpRequestException(secret));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("broken " + secret, new HttpRequestException(secret));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
