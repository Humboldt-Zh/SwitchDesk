using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    private static async Task ManagedUpdateTests()
    {
        var directory = Path.Combine(root, "managed-updates"); Directory.CreateDirectory(directory); var input = Path.Combine(directory, "fixture.nsp"); await File.WriteAllBytesAsync(input, [1, 2, 3]);
        var entry = new ContentEntry(Peer.Save.TitleId, "0100000000000800", "Update", 65536, 3, Fingerprints.Of([1, 2, 3]), "fixture.nsp");
        var component = new TitleComponent(entry.ComponentId, entry.Kind, entry.Version, "Package", 3) { ApplicationId = entry.ApplicationId, RequiredSystemVersion = 0, RequiredApplicationVersion = 0 };
        var package = new PackageInfo("fixture.nsp", 1024, [component], entry.ApplicationId, "fixture", true);
        var game = new GameTitle(entry.ApplicationId, "fixture", "", "irrelevant display version", "已安装", [new(entry.ApplicationId, "Base", 0, "SdCard", 3), new(entry.ComponentId, "Update", 1, "SdCard", 3)]);
        var info = Peer.Info with { Firmware = "20.4.0", Capabilities = Peer.Info.Capabilities.Concat(new[] { "install.plan.v1", "package.dependencies.v1", "operations.status" }).ToArray() };
        var volume = new StorageInfo("SdCard", "fixture", 1L << 36, 1L << 35);
        await Test("managed preflight rejects missing base, downgrade, firmware and space", async () =>
        {
            await Throws<IOException>(() => Sync(() => ManagedUpdates.Prepare(info, entry, package, input, "SdCard", volume, [])));
            await Throws<IOException>(() => Sync(() => ManagedUpdates.Prepare(info, entry, package, input, "SdCard", volume, [game with { Components = [new(entry.ApplicationId, "Base", 0, "SdCard", 3), component with { Version = entry.Version + 1, Storage = "SdCard" }] }])));
            await Throws<IOException>(() => Sync(() => ManagedUpdates.Prepare(info, entry, package with { Components = [component with { RequiredSystemVersion = uint.MaxValue }] }, input, "SdCard", volume, [game])));
            await Throws<IOException>(() => Sync(() => ManagedUpdates.Prepare(info, entry, package, input, "SdCard", volume with { FreeBytes = 0 }, [game])));
        });
        await Test("managed install protects all matching saves and verifies the real installed version", async () =>
        {
            await using var peer = new Peer { DeviceOverride = info, Titles = [game], PackageOverride = package }; await using var client = await peer.ConnectAsync();
            var plan = ManagedUpdates.Prepare(info, entry, package, input, "SdCard", volume, [game]);
            var result = await ManagedUpdates.ExecuteAsync(client, plan, new(Path.Combine(directory, "protected")), null, default);
            Assert(result.Outcome == InstallOutcome.Upgraded && result.ActualVersion == 65536 && result.RecoverySnapshots.Length == 1 && peer.ManagedInstallRequests == 1 && peer.RestoreCommits == 0);
            Assert((await SnapshotStore.VerifyAsync(result.RecoverySnapshots[0])).Manifest.UserId == Peer.Save.UserId);
        });
        await Test("successful install call without changed metadata never becomes an upgrade", async () =>
        {
            await using var peer = new Peer { DeviceOverride = info, Titles = [game], PackageOverride = package, SkipManagedInstall = true }; await using var client = await peer.ConnectAsync();
            var plan = ManagedUpdates.Prepare(info, entry, package, input, "SdCard", volume, [game]);
            await Throws<IOException>(() => ManagedUpdates.ExecuteAsync(client, plan, new(Path.Combine(directory, "no-change")), null, default)); Assert(peer.ManagedInstallRequests == 1);
        });
        await Test("managed source corruption or wrong environment causes zero install requests", async () =>
        {
            await using var peer = new Peer { DeviceOverride = info, Titles = [game], PackageOverride = package }; await using var client = await peer.ConnectAsync();
            var plan = ManagedUpdates.Prepare(info, entry, package, input, "SdCard", volume, [game]);
            await Throws<IOException>(() => ManagedUpdates.ExecuteAsync(client, plan with { Environment = "other" }, new(directory), null, default));
            await File.WriteAllBytesAsync(input, [3, 2, 1]); await Throws<InvalidDataException>(() => ManagedUpdates.ExecuteAsync(client, plan, new(directory), null, default)); Assert(peer.ManagedInstallRequests == 0 && peer.ReadOpens == 0);
            await File.WriteAllBytesAsync(input, [1, 2, 3]);
        });
        await Test("reconnect reconciliation reads versions and native status without retrying installation", async () =>
        {
            var batch = ManagedUpdateBatches.Prepare(info, "SdCard", volume, [game], [new(entry, package, input, Guid.NewGuid().ToString("N"))]);
            await using var peer = new Peer { DeviceOverride = info, Titles = [game with { Components = [game.Components[0], component with { Storage = "SdCard" }] }] };
            peer.OperationStates[batch.Items[0].OperationId] = "verify-required"; await using var client = await peer.ConnectAsync();
            var verified = await OperationReconciliation.InspectAsync(client, batch, default); Assert(verified.State == "verified" && peer.ManagedInstallRequests == 0);
            peer.OperationStates[batch.Items[0].OperationId] = "installing"; Assert((await OperationReconciliation.InspectAsync(client, batch, default)).State == "needs-inspection");
            peer.OperationStates.Clear(); peer.Titles = [game]; Assert((await OperationReconciliation.InspectAsync(client, batch, default)).State == "stopped" && peer.ManagedInstallRequests == 0);
        });
        await Test("content index is generated from inspected IDs and rejects mixed containers", async () =>
        {
            var source = Path.Combine(directory, "builder"); Directory.CreateDirectory(source); await File.WriteAllBytesAsync(Path.Combine(source, "arbitrary-name.nsp"), [1, 2, 3]);
            var index = await ContentIndexBuilder.BuildAsync(source, (_, _) => Task.FromResult(package), default);
            Assert(index.Entries.Single().ComponentId == entry.ComponentId && index.Entries[0].Sha256 == entry.Sha256);
            await Throws<InvalidDataException>(() => ContentIndexBuilder.BuildAsync(source, (_, _) => Task.FromResult(package with { Components = [component, component] }), default));
        });
        await Test("batch preflight orders update before dependent DLC and writes under one reviewed plan", async () =>
        {
            var dlcPath = Path.Combine(directory, "fixture-dlc.nsp"); await File.WriteAllBytesAsync(dlcPath, [1, 2, 3]);
            var dlc = entry with { ComponentId = "0100000000001001", Kind = "DLC", Version = 0, Location = "fixture-dlc.nsp" };
            var dlcPackage = package with { Name = "fixture-dlc.nsp", Components = [component with { TitleId = dlc.ComponentId, Kind = "DLC", Version = 0, RequiredApplicationVersion = 65536 }] };
            var items = new[] { new ManagedBatchItem(dlc, dlcPackage, dlcPath, Guid.NewGuid().ToString("N")), new(entry, package, input, Guid.NewGuid().ToString("N")) };
            var batch = ManagedUpdateBatches.Prepare(info, "SdCard", volume, [game], items); Assert(batch.Items[0].Entry.Kind == "Update" && batch.RequiredBytes == package.RequiredBytes + dlcPackage.RequiredBytes);
            await using var peer = new Peer { DeviceOverride = info, Titles = [game] }; peer.Packages["fixture.nsp"] = package; peer.Packages["fixture-dlc.nsp"] = dlcPackage;
            await using var client = await peer.ConnectAsync(); var results = await ManagedUpdateBatches.ExecuteAsync(client, batch, new(Path.Combine(directory, "batch-protection")), null, default);
            Assert(results.Length == 2 && peer.ManagedInstallRequests == 2 && peer.Titles[0].Components.Any(c => c.TitleId == dlc.ComponentId));
            await Throws<IOException>(() => Sync(() => ManagedUpdateBatches.Prepare(info, "SdCard", volume, [game], [items[0]])));
        });
        await Test("real loopback HTTP source publishes only the verified fixture", async () =>
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(); await using var stream = socket.GetStream(); using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                Assert((await reader.ReadLineAsync())?.StartsWith("GET /library/fixture.nsp ", StringComparison.Ordinal) == true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nETag: \"loopback-fixture\"\r\nConnection: close\r\n\r\n")); await stream.WriteAsync(new byte[] { 1, 2, 3 });
            });
            using var downloads = new ContentDownloads(); var cache = Path.Combine(directory, "loopback-cache");
            var path = await downloads.FetchAsync(new("fixture", "loopback-only", ContentSourceKind.Http, $"http://127.0.0.1:{port}/library/"), entry, cache, null, default);
            await serve.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop(); Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[] { 1, 2, 3 }));
        });
        await Test("stalled stream becomes a retryable timeout while user cancellation stays cancellation", async () =>
        {
            using var stalled = new StalledStream(); await Throws<IOException>(async () => await StreamReads.WithInactivityTimeoutAsync(stalled, new byte[1], default, TimeSpan.FromMilliseconds(30)));
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Throws<OperationCanceledException>(async () => await StreamReads.WithInactivityTimeoutAsync(stalled, new byte[1], cancel.Token, TimeSpan.FromMilliseconds(30)));
        });
        await Test("one downloader limits concurrent HTTP bodies to two", async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var active = 0; var peak = 0;
            using var http = new HttpClient(new FixtureHttp(request =>
            {
                var marker = byte.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath), System.Globalization.CultureInfo.InvariantCulture);
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(new HeldBody(marker, async ct =>
                {
                    var count = Interlocked.Increment(ref active); lock (release) peak = Math.Max(peak, count); if (count == 2) twoStarted.TrySetResult();
                    try { await release.Task.WaitAsync(ct); } finally { Interlocked.Decrement(ref active); }
                })) };
            }));
            using var downloads = new ContentDownloads(http); var source = new ContentSource("fixture", "fixture", ContentSourceKind.Http, "https://source.invalid/library/");
            var tasks = Enumerable.Range(0, 3).Select(i => downloads.FetchAsync(source, entry with { Size = 1, Sha256 = Fingerprints.Of([(byte)i]), Location = i + ".nsp" }, Path.Combine(directory, "parallel-cache"), null, default)).ToArray();
            try { await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(30); Assert(peak == 2); }
            finally { release.TrySetResult(); }
            await Task.WhenAll(tasks); Assert(peak == 2);
        });
        await Test("credentials are scoped to HTTPS source and never appear in serialized configuration", async () =>
        {
            var source = new ContentSource("fixture", "fixture", ContentSourceKind.Http, "https://source.invalid/library/", Guid.NewGuid().ToString("N"));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://source.invalid/library/a.nsp"); ContentAuthentication.Apply(request, source, new FixtureCredentials()); Assert(request.Headers.Authorization?.Scheme == "Basic");
            Assert(!JsonSerializer.Serialize(source, Wire.Json).Contains("fixture-password", StringComparison.Ordinal));
            Assert(!new SourceCredential("fixture-user", "fixture-password").ToString().Contains("fixture-password", StringComparison.Ordinal));
            await Throws<InvalidDataException>(() => Sync(() => { using var wrong = new HttpRequestMessage(HttpMethod.Get, "https://other.invalid/a.nsp"); ContentAuthentication.Apply(wrong, source, new FixtureCredentials()); }));
            await Throws<InvalidDataException>(() => Sync(() => { using var insecure = new HttpRequestMessage(HttpMethod.Get, "http://source.invalid/library/a.nsp"); ContentAuthentication.Apply(insecure, source with { Root = "http://source.invalid/library/" }, new FixtureCredentials()); }));
        });
        await Test("changed resume ETag clears the uncommitted partial instead of appending", async () =>
        {
            var cache = Path.Combine(directory, "changed-resume"); Directory.CreateDirectory(cache); var partial = Path.Combine(cache, entry.Sha256 + ".nsp.part"); await File.WriteAllBytesAsync(partial, [1]);
            await File.WriteAllTextAsync(partial + ".resume.json", JsonSerializer.Serialize(new DownloadResume("https://source.invalid/library/a.nsp", 3, entry.Sha256, "\"old\""), Wire.Json));
            using var downloads = new ContentDownloads(new HttpClient(new FixtureHttp(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request, Content = new ByteArrayContent([2, 3]) }; response.Headers.ETag = new EntityTagHeaderValue("\"new\""); response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 2, 3); return response;
            })));
            await Throws<InvalidDataException>(() => downloads.FetchAsync(new("fixture", "fixture", ContentSourceKind.Http, "https://source.invalid/library/"), entry with { Location = "a.nsp" }, cache, null, default)); Assert(!File.Exists(partial) && !Directory.GetFiles(cache, "*.nsp").Any());
        });
        await Test("TitleDB uses its fixed API raw fallback and retains the actual endpoint", async () =>
        {
            var calls = 0; using var http = new HttpClient(new FixtureHttp(request =>
            {
                calls++; Assert(request.Headers.UserAgent.ToString().StartsWith("SwitchDesk/", StringComparison.Ordinal));
                return calls == 1 ? new(HttpStatusCode.BadGateway) { RequestMessage = request } : new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("id|rightsId|version\n0100000000000800|00000000000000000000000000000000|65536\n") };
            }));
            var catalog = await TitleDbProvider.FetchAsync(http, default); Assert(calls == 2 && catalog.Endpoint!.Contains("api.github.com", StringComparison.Ordinal) && catalog.Components.Single().Version == 65536);
        });
    }
    private sealed class FixtureCredentials : IContentCredentials
    { public SourceCredential? Read(string reference) => new("fixture-user", "fixture-password"); }
    private sealed class StalledStream : MemoryStream
    { public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; } }
    private sealed class HeldBody(byte marker, Func<CancellationToken, Task> wait) : MemoryStream(new byte[] { marker })
    {
        private bool entered;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { if (!entered) { entered = true; await wait(ct); } return await base.ReadAsync(buffer, ct); }
    }
}
