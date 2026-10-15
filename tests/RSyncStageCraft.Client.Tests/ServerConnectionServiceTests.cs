using System.Net.WebSockets;
using RSyncStageCraft.Core;
using RSyncStageCraft.Core.Authentication;
using RSyncStageCraft.Core.Connection;
using RSyncStageCraft.Core.Receiver;
using RSyncStageCraft.Core.Receiver.Ports;

namespace RSyncStageCraft.Tests;

/// <summary>
/// The connection service's own control flow, driven through scripted
/// sockets: user actions must interrupt work in flight at once, and a manual
/// Reconnect is a fresh start.
/// </summary>
public sealed class ServerConnectionServiceTests
{
    private const string Key = "rsc1.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherKey = "rsc1.cccccccccccccccccccccccccccccccc.dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string Endpoint = "wss://rsc.example.test/rsc/v1/control";
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(3);

    private static readonly ReceiverDependencySnapshot PluginsReady = new(true, true, true, true, false);

    // Long enough that only the user's action can explain a fast exit.
    private static ServerConnectionOptions SlowNetwork() => new()
    {
        ConnectTimeout = TimeSpan.FromMinutes(5),
        ReplyTimeout = TimeSpan.FromMinutes(5),
        PollInterval = TimeSpan.FromMilliseconds(50),
        MinimumBackoff = TimeSpan.FromMilliseconds(50),
        MaximumBackoff = TimeSpan.FromMilliseconds(100),
    };

    [Fact]
    public async Task DisconnectInterruptsAnUnansweredSnapshotRequest()
    {
        var settings = new Settings(Endpoint, Key, enabled: true);
        var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
        socket.Add(AuthenticationMessageCodec.EncodeAuthorization(new(2, 3, 4)));
        socket.ReceiveSteps.Enqueue(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return default;
        });
        var runtime = CreateRuntime();
        await using var service = new ServerConnectionService(
            runtime, settings.Read, _ => Task.FromResult(PluginsReady),
            (_, _) => Task.FromResult<WebSocket>(socket), options: SlowNetwork());
        service.Start();
        await WaitForAsync(service, status => status.State == ServerConnectionState.Syncing, TimeSpan.FromSeconds(10));

        settings.Enabled = false;
        service.Wake();

        await WaitForAsync(service, status => status.State == ServerConnectionState.Disabled, Quick);
        Assert.True(socket.Aborted || socket.Disposed);
        Assert.Equal(0, runtime.Authentication.Count);
        await service.StopAsync();
        await runtime.ShutdownAsync();
    }

    [Fact]
    public async Task AConnectFailureNamesTheHostAndOneActionButNeverTheException()
    {
        // The exception (which could name a local path)
        // never reaches the player text.
        const string sentinel = @"C:\Users\sentinel-user\AppData\secret.json SentinelExceptionMessage-7f3a";
        var settings = new Settings(Endpoint, Key, enabled: true);
        var runtime = CreateRuntime();
        await using var service = new ServerConnectionService(
            runtime, settings.Read, _ => Task.FromResult(PluginsReady),
            (_, _) => throw new IOException(sentinel), options: SlowNetwork());
        service.Start();

        // Checked inside the wait: after the backoff the status moves on to Connecting.
        ServerConnectionStatus? unreachable = null;
        await WaitForAsync(service, status =>
        {
            if (status.State != ServerConnectionState.Unreachable)
                return false;
            unreachable = status;
            return true;
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(
            "Could not reach rsc.example.test. Check the server address in Settings and your internet connection.",
            unreachable!.Detail);
        Assert.DoesNotContain("sentinel", unreachable.Detail, StringComparison.OrdinalIgnoreCase);
        await service.StopAsync();
        await runtime.ShutdownAsync();
    }

    [Fact]
    public async Task ChangingTheKeyInterruptsABlockedConnect()
    {
        var settings = new Settings(Endpoint, Key, enabled: true);
        var connects = 0;
        var runtime = CreateRuntime();
        await using var service = new ServerConnectionService(
            runtime, settings.Read, _ => Task.FromResult(PluginsReady),
            async (_, token) =>
            {
                Interlocked.Increment(ref connects);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
            options: SlowNetwork());
        service.Start();
        await WaitForAsync(service, status => status.State == ServerConnectionState.Connecting, Quick);
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 1, Quick);

        settings.AccessKey = OtherKey;
        service.Wake();

        // The blocked attempt is abandoned and a new one starts with the new key.
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 2, Quick);
        await service.StopAsync();
        await runtime.ShutdownAsync();
    }

    [Fact]
    public async Task ReconnectInterruptsABlockedConnect()
    {
        var settings = new Settings(Endpoint, Key, enabled: true);
        var connects = 0;
        var runtime = CreateRuntime();
        await using var service = new ServerConnectionService(
            runtime, settings.Read, _ => Task.FromResult(PluginsReady),
            async (_, token) =>
            {
                Interlocked.Increment(ref connects);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
            options: SlowNetwork());
        service.Start();
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 1, Quick);

        service.Reconnect();

        await WaitUntilAsync(() => Volatile.Read(ref connects) == 2, Quick);
        await service.StopAsync();
        await runtime.ShutdownAsync();
    }

    [Fact]
    public async Task UnrelatedWakeDoesNotDropAHealthyAttempt()
    {
        var settings = new Settings(Endpoint, Key, enabled: true);
        var connects = 0;
        var runtime = CreateRuntime();
        await using var service = new ServerConnectionService(
            runtime, settings.Read, _ => Task.FromResult(PluginsReady),
            async (_, token) =>
            {
                Interlocked.Increment(ref connects);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
            options: SlowNetwork());
        service.Start();
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 1, Quick);

        // A plugin list change wakes the service, but nothing the attempt
        // depends on changed.
        service.Wake();
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, Volatile.Read(ref connects));
        Assert.Equal(ServerConnectionState.Connecting, service.Status.State);
        await service.StopAsync();
        await runtime.ShutdownAsync();
    }

    [Fact]
    public async Task ReconnectAfterARefusalAllowsTheRetryAgain()
    {
        var settings = new Settings(Endpoint, Key, enabled: true);
        var connects = 0;
        var runtime = CreateRuntime();
        await using var service = new ServerConnectionService(
            runtime, settings.Read, _ => Task.FromResult(PluginsReady),
            (_, _) =>
            {
                Interlocked.Increment(ref connects);
                // The server closes right after the hello: a refused sign-in.
                var socket = new ClientAuthenticationCoordinatorTests.ScriptedSocket();
                socket.Add([], type: WebSocketMessageType.Close);
                return Task.FromResult<WebSocket>(socket);
            },
            options: SlowNetwork());
        service.Start();

        await WaitForAsync(service, status => status.State == ServerConnectionState.SignInRefused, Quick);
        Assert.Equal(2, Volatile.Read(ref connects));

        service.Reconnect();

        // A fresh start: one retry again before reporting the refusal.
        await WaitUntilAsync(() => Volatile.Read(ref connects) == 4, Quick);
        await WaitForAsync(service, status => status.State == ServerConnectionState.SignInRefused, Quick);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(4, Volatile.Read(ref connects));
        await service.StopAsync();
        await runtime.ShutdownAsync();
    }

    [Fact]
    public async Task AFirstRunWithoutAServerAddressAsksForOneAndNeverConnects()
    {
        // A new install has no default server: nothing is contacted until the
        // player enters an address in Settings.
        Assert.Empty(ServerConnectionSettings.DefaultEndpoint);
        var settings = new Settings(ServerConnectionSettings.DefaultEndpoint, Key, enabled: true);
        var runtime = CreateRuntime();
        var connects = 0;
        await using var service = new ServerConnectionService(
            runtime, settings.Read, _ => Task.FromResult(PluginsReady),
            (_, _) =>
            {
                Interlocked.Increment(ref connects);
                throw new IOException("unexpected connect");
            },
            options: SlowNetwork());
        service.Start();

        await WaitForAsync(service, status => status.State == ServerConnectionState.NotConfigured, Quick);
        Assert.Equal("Enter the server address (wss://...).", service.Status.Detail);
        service.Wake();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Equal(ServerConnectionState.NotConfigured, service.Status.State);
        Assert.Equal(0, Volatile.Read(ref connects));
        await service.StopAsync();
        await runtime.ShutdownAsync();
    }

    private static RSyncStageCraftRuntime CreateRuntime()
    {
        var ports = new TestGamePorts();
        return new RSyncStageCraftRuntime(ports, ports);
    }

    private static async Task WaitForAsync(
        ServerConnectionService service,
        Func<ServerConnectionStatus, bool> condition,
        TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        var seen = new List<string>();
        while (true)
        {
            var status = service.Status;
            if (condition(status))
                return;
            var text = $"{status.State}: {status.Detail}";
            if (seen.Count == 0 || seen[^1] != text)
                seen.Add(text);
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out. Statuses seen: " + string.Join(" | ", seen.Take(12)));
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for the condition.");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private sealed class Settings(string endpoint, string key, bool enabled)
    {
        private readonly object gate = new();
        private string accessKey = key;
        private bool isEnabled = enabled;

        public string AccessKey
        {
            set { lock (gate) accessKey = value; }
        }

        public bool Enabled
        {
            set { lock (gate) isEnabled = value; }
        }

        public ServerConnectionSettings Read()
        {
            lock (gate)
                return new(endpoint, accessKey, isEnabled);
        }
    }
}
