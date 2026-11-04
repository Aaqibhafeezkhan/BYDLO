using System.Buffers.Binary;
using System.Security.Cryptography;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines;

namespace Bydlo.Tests;

public sealed class PrivilegedHelperTests
{
    private static readonly HelperBuild ExpectedBuild = new("0.3.2", "current-bundle");
    [Fact]
    public void WindowsAcl_RestrictsCallerAndForbidsRemoteAccessAndServerCreation()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string userSid = "S-1-5-21-1-2-3-1001";
        var security = Bydlo.Platform.HelperConnection.CreateSecurity(userSid);
        var rules = security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.IO.Pipes.PipeAccessRule>().ToArray();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal("S-1-5-18", security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))!.Value);
        Assert.Equal(3, rules.Length);
        var entries = new List<(string Sid, int Rights, int Type)>();
        foreach (var rule in rules) { entries.Add((rule.IdentityReference.Value, (int)rule.PipeAccessRights, (int)rule.AccessControlType)); }
        var user = Assert.Single(entries, rule => rule.Sid == userSid);
        Assert.Equal((int)System.Security.AccessControl.AccessControlType.Allow, user.Type);
        Assert.Equal(0, user.Rights & (int)System.IO.Pipes.PipeAccessRights.CreateNewInstance);
        var deny = (int)System.Security.AccessControl.AccessControlType.Deny;
        Assert.Contains(entries, rule => rule.Sid == "S-1-5-2" && rule.Type == deny);
    }

    [Fact]
    public async Task AvailabilityProbe_DisposesItsLeaseSoEngineListsCannotExhaustTheService()
    {
        var probe = new FakeOperations();
        await using var engine = new HelperEngine(new FakeHelperEngine(), () => probe, () => true, ExpectedBuild);
        var availability = await engine.CheckAvailabilityAsync(new AppSettings { Mode = ConnectionMode.Tun });
        Assert.True(availability.IsReady);
        Assert.True(probe.Disposed);
        var request = Assert.Single(probe.Requests);
        Assert.Equal(HelperOperation.Check, request.Operation);
        Assert.Equal(ExpectedBuild, request.ExpectedBuild);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("0.3.0", "current-bundle")]
    [InlineData("0.3.2", "old-bundle")]
    public async Task AvailabilityProbe_ReportsOutdatedHelper(string? version, string? bundle)
    {
        var probe = new FakeOperations { Build = version is not null && bundle is not null ? new(version, bundle) : null };
        await using var engine = new HelperEngine(new FakeHelperEngine(), () => probe, () => true, ExpectedBuild);
        var availability = await engine.CheckAvailabilityAsync(new AppSettings { Mode = ConnectionMode.Tun });
        Assert.False(availability.IsReady);
        Assert.Equal(HelperProtocol.Outdated, availability.LocalizationKey);
    }

    [Fact]
    public async Task Stop_requires_remote_confirmation_and_retains_failed_lease_for_retry()
    {
        var remote = new FakeOperations { StopReply = new HelperReply { State = EngineState.Failed, Error = "helper.error" } };
        await using var engine = AttachLease(remote);

        await Assert.ThrowsAsync<EngineException>(() => engine.StopAsync());
        Assert.Equal(EngineState.Failed, engine.State);
        Assert.False(remote.Disposed);

        remote.StopReply = new HelperReply { State = EngineState.Stopped };
        await engine.StopAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.True(remote.Disposed);
        Assert.Equal(2, remote.Requests.Count(request => request.Operation == HelperOperation.Stop));
    }

    [Fact]
    public async Task Canceling_list_after_send_preserves_the_lease_for_stop()
    {
        var pending = new TaskCompletionSource<HelperReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new FakeOperations { OnList = token => pending.Task.WaitAsync(token) };
        await using var engine = AttachLease(remote);
        using var page = new CancellationTokenSource();
        var list = engine.ListConnectionsAsync(page.Token);
        Assert.Equal(HelperOperation.ListConnections, Assert.Single(remote.Requests).Operation);
        page.Cancel();
        pending.SetResult(new HelperReply { State = EngineState.Running });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => list);
        Assert.False(remote.Disposed);
        Assert.Equal(EngineState.Running, engine.State);
        await engine.StopAsync();
        Assert.Equal(HelperOperation.Stop, remote.Requests.Last().Operation);
    }

    [Fact]
    public async Task Lost_transport_publishes_failure_and_stop_does_not_reuse_the_lease()
    {
        var remote = new FakeOperations { OnList = _ => Task.FromException<HelperReply>(new ObjectDisposedException("pipe")) };
        await using var engine = AttachLease(remote);
        EngineState? reported = null;
        engine.StateChanged += (_, args) => reported = args.State;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.ListConnectionsAsync());
        Assert.Equal(EngineState.Failed, reported);
        Assert.True(remote.Disposed);
        await engine.StopAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.Single(remote.Requests);
    }

    private static HelperEngine AttachLease(FakeOperations remote)
    {
        // Attach the fake established lease without invoking privileged startup.
        var engine = new HelperEngine(new FakeHelperEngine(), () => remote, () => true, ExpectedBuild);
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(HelperEngine).GetField("_remote", fields)!.SetValue(engine, remote);
        typeof(HelperEngine).GetField("_usingRemote", fields)!.SetValue(engine, true);
        typeof(HelperEngine).GetField("_state", fields)!.SetValue(engine, EngineState.Running);
        return engine;
    }

    [Fact]
    public async Task Protocol_RoundTripsProfileSecretsOnlyInMemory()
    {
        using var stream = new MemoryStream();
        var request = StartRequest();
        await HelperProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        var decoded = await HelperProtocol.ReadAsync<HelperRequest>(stream, CancellationToken.None);
        Assert.Equal("secret", decoded.Start!.Secrets[Keys.Password]);
        Assert.Equal(EngineKind.SingBox, decoded.Engine);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(HelperProtocol.MaxMessageBytes + 1)]
    public async Task Protocol_RejectsOversizedAndInvalidFramesBeforeBodyAllocation(int size)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, size);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => HelperProtocol.ReadAsync<HelperRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Protocol_RejectsTruncatedFrames()
    {
        using var stream = new MemoryStream(new byte[] { 4, 0, 0, 0, 1 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => HelperProtocol.ReadAsync<HelperRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public void Policy_RejectsVersionUnknownOperationsAndArbitraryPrograms()
    {
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(StartRequest() with { Version = 999 }));
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(StartRequest() with { Operation = (HelperOperation)999 }));
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(StartRequest() with { Engine = EngineKind.Xray }));
        var request = StartRequest();
        request.Settings.EnginePaths.Add("SingBox", "evil.exe");
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(request));
    }

    [Fact]
    public void Policy_RejectsSettingsSmuggledInsideStartAndExternalPaths()
    {
        var request = StartRequest();
        request.Start!.Settings.Zapret.InstallPath = "untrusted";
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(request));
        request = StartRequest();
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(request with { Start = request.Start! with { RuleSetDirectory = "foreign" } }));
        request.Settings.Zapret.Strategy = "../evil.bat";
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(request));
    }

    [Fact]
    public void Policy_RejectsRawJsonAndExecutablePlugins()
    {
        var request = StartRequest();
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(request with
        { Start = request.Start! with { Profile = new Profile { Protocol = ProtocolKind.SingBoxConfig } } }));
        request.Start!.Profile!.Options[Keys.Plugin] = "evil.exe";
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(request));
    }

    [Theory]
    [InlineData("up evil")]
    [InlineData("plugin evil.dll")]
    [InlineData("config foreign.ovpn")]
    [InlineData("log foreign.txt")]
    [InlineData("auth-user-pass foreign.txt")]
    [InlineData("script-security 2")]
    [InlineData("management 127.0.0.1 1234")]
    [InlineData("ca /root/private")]
    [InlineData("<key>\n</key> trailing\nup evil\n</key>")]
    public void OpenVpn_RejectsCommandsExternalFilesAndInlineBlockEscapes(string config) =>
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.ValidateOpenVpn(config));

    [Fact]
    public void OpenVpn_AcceptsInlineCredentialsAndRemoteTunnelData() =>
        HelperRequestPolicy.ValidateOpenVpn("client\ndev tun\nremote example.org 443\nauth-user-pass\n<ca>\n-----BEGIN CERTIFICATE-----\nYWJj\n-----END CERTIFICATE-----\n</ca>");

    [Fact]
    public void Bundle_RejectsModifiedMissingAdditionalAndEscapingFiles()
    {
        var root = TestDirs.New("helper-bundle");
        var engines = Path.Combine(root, "engines");
        Directory.CreateDirectory(engines);
        var binary = Path.Combine(engines, "engine");
        File.WriteAllText(binary, "bundled");
        var hashes = new Dictionary<string, string> { [Path.Combine("engines", "engine")] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))) };
        HelperBundleIntegrity.Verify(root, hashes);
        File.WriteAllText(binary, "changed");
        Assert.Throws<InvalidDataException>(() => HelperBundleIntegrity.Verify(root, hashes));
        File.WriteAllText(binary, "bundled");
        File.WriteAllText(Path.Combine(engines, "evil.dll"), "extra");
        Assert.Throws<InvalidDataException>(() => HelperBundleIntegrity.Verify(root, hashes));
        File.Delete(Path.Combine(engines, "evil.dll"));
        Assert.Throws<InvalidDataException>(() => HelperBundleIntegrity.Verify(root, new Dictionary<string, string> { ["../escape"] = hashes.Values.First() }));
        File.Delete(binary);
        Assert.Throws<FileNotFoundException>(() => HelperBundleIntegrity.Verify(root, hashes));
    }

    [Fact]
    public async Task Session_OwnsItsWorkDirectoryAndStopsItsFakeEngineOnDisconnect()
    {
        var root = TestDirs.New("helper-session");
        var engine = new FakeHelperEngine();
        await using (var session = new HelperSession(_ => engine, root, new HelperLeasePool()))
        {
            await session.DispatchAsync(StartRequest(), CancellationToken.None);
            Assert.StartsWith(root + Path.DirectorySeparatorChar, engine.Request!.WorkDirectory, StringComparison.Ordinal);
            Assert.NotEqual("untrusted-client-path", engine.Request.WorkDirectory);
            await Assert.ThrowsAsync<InvalidDataException>(() => session.DispatchAsync(StartRequest() with { Engine = EngineKind.Zapret }, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => session.DispatchAsync(StartRequest(), CancellationToken.None));
        }
        Assert.True(engine.Stopped);
        Assert.Empty(Directory.EnumerateDirectories(root));
    }

    [Fact]
    public void Leases_PreventConflictingTunnelsAndAllowAnIndependentDpiTool()
    {
        var pool = new HelperLeasePool();
        var first = pool.Acquire(EngineKind.SingBox);
        using var tool = pool.Acquire(EngineKind.Zapret);
        Assert.Throws<InvalidDataException>(() => pool.Acquire(EngineKind.AmneziaWG));
        first.Dispose();
        using var second = pool.Acquire(EngineKind.OpenVpn);
        first.Dispose(); // An old lease cannot release the new owner's resource.
        Assert.Throws<InvalidDataException>(() => pool.Acquire(EngineKind.SingBox));
    }

    [Fact]
    public async Task AStoppedSession_CannotReconnectWithoutOwningALease()
    {
        var root = TestDirs.New("helper-reconnect");
        var pool = new HelperLeasePool();
        await using var first = new HelperSession(_ => new FakeHelperEngine(), root, pool);
        await first.DispatchAsync(StartRequest(), CancellationToken.None);
        await first.DispatchAsync(new HelperRequest { Operation = HelperOperation.Stop, Engine = EngineKind.SingBox }, CancellationToken.None);
        using var nextOwner = pool.Acquire(EngineKind.AmneziaWG);
        await Assert.ThrowsAsync<InvalidDataException>(() => first.DispatchAsync(StartRequest() with { Operation = HelperOperation.Reconnect }, CancellationToken.None));
    }

    [Fact]
    public async Task SafetyLock_BlocksHelperClientBeforeAnyIpc()
    {
        Assert.True(NetworkSafetyGuard.IsLocked);
        await using IPrivilegedOperations connection = OperatingSystem.IsWindows()
            ? new Bydlo.Platform.HelperConnection() : new Bydlo.Platform.UnixHelperConnection();
        await Assert.ThrowsAsync<NetworkSafetyException>(() => connection.SendAsync(StartRequest()));
        Assert.False(await Bydlo.Platform.HelperManagement.ChangeAsync(true, userConsent: false));
        await Assert.ThrowsAsync<NetworkSafetyException>(() => Bydlo.Platform.HelperManagement.ChangeAsync(true, userConsent: true));
    }

    private static HelperRequest StartRequest() => new()
    {
        Engine = EngineKind.SingBox, Operation = HelperOperation.Start,
        Start = new EngineStartRequest
        {
            Profile = new Profile { Protocol = ProtocolKind.Trojan, Host = "example.org", Port = 443 },
            Secrets = new ProfileSecrets { Values = new() { [Keys.Password] = "secret" } },
            Settings = new AppSettings { Mode = ConnectionMode.Tun }, WorkDirectory = "untrusted-client-path",
        },
    };

    private sealed class FakeHelperEngine : IEngine
    {
        public EngineStartRequest? Request { get; private set; }
        public bool Stopped { get; private set; }
        public EngineDescriptor Descriptor => Bydlo.Engines.SingBox.SingBoxEngine.Info;
        public EngineState State { get; private set; }
        public event EventHandler<EngineStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<string>? Log { add { } remove { } }
        public event EventHandler<TrafficSample>? Traffic { add { } remove { } }
        public bool CanHandle(Profile profile) => true;
        public Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) => Task.FromResult(EngineAvailability.Ready("fake"));
        public Task StartAsync(EngineStartRequest request, CancellationToken ct = default) { Request = request; State = EngineState.Running; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { Stopped = true; State = EngineState.Stopped; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOperations : IPrivilegedOperations
    {
        public Func<CancellationToken, Task<HelperReply>>? OnList { get; init; }
        public HelperBuild? Build { get; init; } = ExpectedBuild;
        public HelperReply StopReply { get; set; } = new() { State = EngineState.Stopped };
        public bool Disposed { get; private set; }
        public List<HelperRequest> Requests { get; } = [];
        public Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (request.Operation == HelperOperation.ListConnections && OnList is not null) { return OnList(ct); }
            if (request.Operation == HelperOperation.Stop) { return Task.FromResult(StopReply); }
            return Task.FromResult(new HelperReply { Build = Build, Availability = EngineAvailability.Ready("bundled") });
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
