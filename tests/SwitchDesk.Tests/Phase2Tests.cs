using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    private static byte[] KnightJson(int progress, string setting = "target") => Encoding.UTF8.GetBytes($$"""{"playerData":{"version":"fixture-1","progress":{{progress}}},"sceneData":{"room":"fixture"},"deviceSetting":"{{setting}}"}""");
    private static readonly GameVersionContext FixtureVersions = new("fixture-build", "public", "0100000000000000", 65536);
    private static CompatibilityCatalog KnightCompatibility() => new([new("hollow-knight", "knight-string-aes256-v1", "fixture-1", "knight-json-v1", "fixture-1", MigrationFidelity.Full, [], [], "synthetic-codec-fixture-only")
    { SteamBuildId = FixtureVersions.SteamBuildId, SteamBranch = FixtureVersions.SteamBranch, SwitchApplicationId = FixtureVersions.SwitchApplicationId, SwitchContentVersion = FixtureVersions.SwitchContentVersion }]);
    private static SavePair FixturePair(string path) => new(Guid.NewGuid().ToString("N"), "hollow-knight", "Windows fixture", path, "1", "fixture-console", "fixture-env", FixtureVersions.SwitchApplicationId, "fixture-user", "fixture-save", "user1.dat", "1");
    private static SaveReading Reading(byte value, DateTimeOffset? at = null) => new(Fingerprints.Of([value]), DateTimeOffset.UtcNow, at, at == null ? SaveTimeSource.ObservedOnly : SaveTimeSource.FileModified, "fixture", "1", "1", "fixture");
    private static byte[] DeadCellsFixture(byte permanent, byte run, uint dlc = 1)
    {
        var header = new byte[59]; new byte[] { 0xDE, 0xAD, 0xCE, 0x11, 1 }.CopyTo(header, 0); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(55), 0x1c3);
        using var output = new MemoryStream(); output.Write(header);
        using (var zip = new ZLibStream(output, CompressionLevel.SmallestSize, true))
        {
            using var writer = new BinaryWriter(zip, Encoding.UTF8, true); writer.Write(1); writer.Write(permanent); writer.Write(1); writer.Write(run);
            writer.Write(4); writer.Write(34f); writer.Write(4); writer.Write(dlc);
        }
        var result = output.ToArray(); SHA1.HashData(result).CopyTo(result, 5); return result;
    }
    private static async Task Phase2Tests()
    {
        var phase = Path.Combine(root, "phase2"); Directory.CreateDirectory(phase);
        await Test("cross-save first pairing never selects a source by clock", () => Sync(() => Assert(SaveComparison.Compare(null, Reading(1, DateTimeOffset.UtcNow.AddDays(10)), Reading(2)).RecommendedSource == null)));
        await Test("cross-save fingerprints distinguish all four baseline states", () => Sync(() =>
        {
            var baseline = new SaveBaseline(Reading(1).Fingerprint, Reading(2).Fingerprint, DateTimeOffset.UtcNow);
            Assert(SaveComparison.Compare(baseline, Reading(1), Reading(2)).State == ComparisonState.Unchanged);
            Assert(SaveComparison.Compare(baseline, Reading(3), Reading(2)).State == ComparisonState.SteamChanged);
            Assert(SaveComparison.Compare(baseline, Reading(1), Reading(3)).State == ComparisonState.SwitchChanged);
            Assert(SaveComparison.Compare(baseline, Reading(3), Reading(4)).State == ComparisonState.Conflict);
        }));
        await Test("observed time is never described as game save time", () => Sync(() => Assert(Reading(1).TimeLabel.Contains("保存时间不可用", StringComparison.Ordinal))));
        await Test("new companion restore carries operation ID and expected target fingerprint", async () =>
        {
            var info = Peer.Info with { Capabilities = Peer.Info.Capabilities.Concat(new[] { "saves.inspect", "restore.atomic.v1" }).ToArray() };
            await using var peer = new Peer { DeviceOverride = info }; await using var client = await peer.ConnectAsync(); var store = new SnapshotStore(Path.Combine(phase, "atomic-backups"));
            var snapshot = await store.BackupAsync(client, info, Peer.Save, null, default); var before = await client.QueryAsync<SaveDeviceInspection>("saves.inspect");
            await store.RestoreAsync(client, info, Peer.Save, snapshot.Path, null, default, before.Fingerprint, "0123456789abcdef0123456789abcdef");
            Assert(peer.RestoreFingerprint == before.Fingerprint && peer.RestoreOperation == "0123456789abcdef0123456789abcdef" && peer.RestoreCommits == 1);
            var writes = peer.WriteOperations;
            await Throws<IOException>(() => store.RestoreAsync(client, info, Peer.Save, snapshot.Path, null, default, Reading(55).Fingerprint)); Assert(peer.WriteOperations == writes);
        });
        await Test("phase-two database CAS survives reopen without touching library", async () =>
        {
            var path = Path.Combine(phase, "state.db");
            using (var db = new Phase2State(path))
            { var revision = db.Put("pair", "中文", new { size = 5L << 30 }, 0); Assert(revision == 1); await Throws<InvalidOperationException>(() => Sync(() => db.Put("pair", "中文", new { size = 0 }, 0))); }
            using var reopened = new Phase2State(path); Assert(reopened.Get<JsonElement>("pair", "中文")!.Value.Value.GetProperty("size").GetInt64() == 5L << 30);
        });
        await Test("Steam discovery ignores backups and keeps accounts and slots separate", () => Sync(() =>
        {
            var steam = Path.Combine(phase, "Steam"); var profile = Path.Combine(phase, "profile"); var documents = Path.Combine(profile, "Documents");
            foreach (var account in new[] { "111", "222" }) { var remote = Path.Combine(steam, "userdata", account, "1794680", "remote"); Directory.CreateDirectory(remote); File.WriteAllBytes(Path.Combine(remote, "SaveData"), [1]); File.WriteAllBytes(Path.Combine(remote, "LastRunBackup.sav"), [2]); }
            var shared = Path.Combine(documents, "Saved Games", "Hades"); Directory.CreateDirectory(shared); File.WriteAllBytes(Path.Combine(shared, "Profile1.sav"), [1]);
            var found = SteamSaves.Discover(steam, profile, documents); Assert(found.Length == 3 && found.Where(x => x.AdapterId == "vampire-survivors").Select(x => x.Owner).Distinct().Count() == 2 && found.Single(x => x.AdapterId == "hades").Owner.StartsWith("Windows ", StringComparison.Ordinal));
        }));
        await Test("Vampire exact checksum preserves numeric dialect and rejects corruption", async () =>
        {
            var empty = "{\"Version\":0,\"Coins\":946.0,\"checksum\":\"\"}"; var valid = Encoding.UTF8.GetBytes(empty.Replace("\"checksum\":\"\"", "\"checksum\":\"" + Fingerprints.Of(Encoding.UTF8.GetBytes(empty)) + "\"", StringComparison.Ordinal));
            var adapter = new VampireSaveAdapter(); Assert(adapter.Inspect(valid, SavePlatform.Steam).Version == "0");
            valid = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("946.0", "947.0", StringComparison.Ordinal)); await Throws<InvalidDataException>(() => Sync(() => adapter.Inspect(valid, SavePlatform.Steam)));
        });
        await Test("duplicate JSON properties cannot bypass save validation", () => Throws<InvalidDataException>(() => Sync(() => LocalSafety.DemandJsonObject("{\"checksum\":\"a\",\"checksum\":\"b\"}"u8))));
        await Test("Knight bounded envelope roundtrip and target settings survive conversion", () => Sync(() =>
        {
            var adapter = new KnightSaveAdapter(false); var pc = KnightSaveAdapter.EncodePc(KnightJson(7, "source"));
            Assert(KnightSaveAdapter.DecodePc(pc).SequenceEqual(KnightJson(7, "source")));
            var converted = adapter.Convert(pc, SavePlatform.Steam, KnightJson(1, "target")); using var doc = JsonDocument.Parse(converted.Bytes);
            Assert(doc.RootElement.GetProperty("deviceSetting").GetString() == "target" && doc.RootElement.GetProperty("playerData").GetProperty("progress").GetInt32() == 7);
            var reverse = adapter.Convert(converted.Bytes, SavePlatform.Switch, pc); Assert(adapter.Inspect(reverse.Bytes, SavePlatform.Steam).Version == "fixture-1");
        }));
        await Test("Knight malformed length, trailing data and padding are rejected", async () =>
        {
            var bytes = KnightSaveAdapter.EncodePc(KnightJson(1)); var bad = bytes.ToArray(); bad[22] ^= 1;
            await Throws<InvalidDataException>(() => Sync(() => KnightSaveAdapter.DecodePc(bad)));
            await Throws<InvalidDataException>(() => Sync(() => KnightSaveAdapter.DecodePc(bytes.Concat(new byte[] { 0 }).ToArray())));
            bad = bytes.ToArray(); bad[^2] = (byte)'!'; await Throws<InvalidDataException>(() => Sync(() => KnightSaveAdapter.DecodePc(bad)));
        });
        await Test("Dead Cells verifies checksum and retains only declared permanent progress", async () =>
        {
            var adapter = new DeadCellsSaveAdapter(); var source = DeadCellsFixture(42, 8); var target = DeadCellsFixture(1, 9);
            Assert(adapter.Inspect(source, SavePlatform.Steam).Version == "34"); var converted = adapter.Convert(source, SavePlatform.Steam, target);
            Assert(converted.Fidelity == MigrationFidelity.PermanentProgressOnly && (BinaryPrimitives.ReadUInt32LittleEndian(converted.Bytes.AsSpan(55)) & 6) == 0);
            using var input = new MemoryStream(converted.Bytes[59..]); using var zip = new ZLibStream(input, CompressionMode.Decompress); using var reader = new BinaryReader(zip);
            Assert(reader.ReadInt32() == 1 && reader.ReadByte() == 42);
            source[^1] ^= 1; await Throws<InvalidDataException>(() => Sync(() => adapter.Inspect(source, SavePlatform.Steam)));
            await Throws<InvalidDataException>(() => Sync(() => adapter.Convert(target, SavePlatform.Steam, DeadCellsFixture(1, 1, 2))));
        });
        await Test("content index requires an explicit numeric version even for zero", () => Throws<InvalidDataException>(() => Sync(() =>
            ContentSources.Parse(Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"entries\":[{\"applicationId\":\"0100000000000000\",\"componentId\":\"0100000000000800\",\"kind\":\"Update\",\"size\":1,\"sha256\":\"" + Reading(1).Fingerprint + "\",\"location\":\"a.nsp\"}]}"), new("fixture", "fixture", ContentSourceKind.Http, "https://source.invalid/games/")))));
        await Test("format recognition alone never grants migration eligibility", async () =>
        {
            var adapter = new KnightSaveAdapter(false); var pc = adapter.Inspect(KnightSaveAdapter.EncodePc(KnightJson(1)), SavePlatform.Steam); var nx = adapter.Inspect(KnightJson(1), SavePlatform.Switch);
            Assert(CompatibilityCatalog.Released.Find(adapter.Id, pc, nx, [], [], FixtureVersions) == null);
            Assert(KnightCompatibility().Find(adapter.Id, pc, nx, [], []) == null);
            Assert(KnightCompatibility().Find(adapter.Id, pc, nx, [], [], FixtureVersions with { SteamBuildId = "new-build" }) == null);
            Assert(KnightCompatibility().Find(adapter.Id, pc, nx, ["unmatched-dlc"], [], FixtureVersions) == null);
            using var db = new Phase2State(Path.Combine(phase, "blocked.db"));
            await Throws<NotSupportedException>(() => new MigrationWorkspace(db, phase, CompatibilityCatalog.Released).PrepareAsync(FixturePair("unused"), adapter, SavePlatform.Switch, KnightSaveAdapter.EncodePc(KnightJson(1)), KnightJson(2), [], [], default, FixtureVersions));
        });
        await Test("prepared migration preserves both images and commits only one PC slot", async () =>
        {
            var directory = Path.Combine(phase, "migrate"); Directory.CreateDirectory(directory); var target = Path.Combine(directory, "user1.dat"); var other = Path.Combine(directory, "user2.dat");
            var original = KnightSaveAdapter.EncodePc(KnightJson(1)); var source = KnightJson(42); await File.WriteAllBytesAsync(target, original); await File.WriteAllBytesAsync(other, [9]);
            using var db = new Phase2State(Path.Combine(directory, "state.db")); var workspace = new MigrationWorkspace(db, directory, KnightCompatibility());
            var operation = await workspace.PrepareAsync(FixturePair(target), new KnightSaveAdapter(false), SavePlatform.Switch, original, source, [], [], default, FixtureVersions);
            Assert((await File.ReadAllBytesAsync(Path.Combine(operation.RecoveryDirectory, "source.bin"))).SequenceEqual(source)); Assert((await File.ReadAllBytesAsync(target)).SequenceEqual(original));
            await workspace.CommitPcAsync(operation.Id, () => Task.CompletedTask, default);
            Assert((await File.ReadAllBytesAsync(other)).SequenceEqual(new byte[] { 9 })); Assert(db.Get<MigrationOperation>("migrations", operation.Id)!.Value.Value.State == MigrationState.Complete);
            await Throws<InvalidOperationException>(() => workspace.CommitPcAsync(operation.Id, () => Task.CompletedTask, default));
            Assert((await File.ReadAllBytesAsync(Path.Combine(operation.RecoveryDirectory, "target.bin"))).SequenceEqual(original));
            await File.WriteAllBytesAsync(target, original); Assert(!await workspace.VerifyPcAfterCloudAsync(operation.Id, default));
            Assert(db.Get<MigrationOperation>("migrations", operation.Id)!.Value.Value.State == MigrationState.NeedsRecovery);
        });
        await Test("changed target or missing protection blocks PC overwrite", async () =>
        {
            var directory = Path.Combine(phase, "stale"); Directory.CreateDirectory(directory); var target = Path.Combine(directory, "user1.dat"); var pc = KnightSaveAdapter.EncodePc(KnightJson(1)); await File.WriteAllBytesAsync(target, pc);
            using var db = new Phase2State(Path.Combine(directory, "state.db")); var workspace = new MigrationWorkspace(db, directory, KnightCompatibility());
            var operation = await workspace.PrepareAsync(FixturePair(target), new KnightSaveAdapter(false), SavePlatform.Switch, pc, KnightJson(3), [], [], default, FixtureVersions);
            await File.WriteAllBytesAsync(target, [9]); await Throws<IOException>(() => workspace.CommitPcAsync(operation.Id, () => Task.CompletedTask, default)); Assert((await File.ReadAllBytesAsync(target))[0] == 9);
            await File.WriteAllBytesAsync(target, pc); await File.WriteAllBytesAsync(Path.Combine(operation.RecoveryDirectory, "target.bin"), [0]);
            await Throws<InvalidDataException>(() => workspace.CommitPcAsync(operation.Id, () => Task.CompletedTask, default)); Assert((await File.ReadAllBytesAsync(target)).SequenceEqual(pc));
        });
        await Test("Steam to Switch uses a complete target snapshot and preserves unrelated files and identity", async () =>
        {
            var directory = Path.Combine(phase, "switch-migrate"); Directory.CreateDirectory(directory); var pcPath = Path.Combine(directory, "user1.dat");
            var pc = KnightSaveAdapter.EncodePc(KnightJson(42, "computer")); await File.WriteAllBytesAsync(pcPath, pc);
            var nx = KnightJson(1, "console"); var info = Peer.Info with { Capabilities = Peer.Info.Capabilities.Concat(new[] { "saves.inspect", "restore.atomic.v1" }).ToArray() };
            await using var peer = new Peer { DeviceOverride = info }; peer.Files["main.dat"] = nx; var settings = peer.Files["nested/config.bin"].ToArray();
            await using var client = await peer.ConnectAsync(); var store = new SnapshotStore(Path.Combine(directory, "backups")); var full = await store.BackupAsync(client, info, Peer.Save, null, default);
            var inspection = await client.QueryAsync<SaveDeviceInspection>("saves.inspect");
            using var db = new Phase2State(Path.Combine(directory, "state.db")); var workspace = new MigrationWorkspace(db, directory, KnightCompatibility());
            var pair = FixturePair(pcPath) with { DeviceId = info.DeviceId, Environment = info.Environment, TitleId = Peer.Save.TitleId, UserId = Peer.Save.UserId, SaveKey = Peer.Save.Key, SwitchPath = "main.dat" };
            var operation = await workspace.PrepareAsync(pair, new KnightSaveAdapter(false), SavePlatform.Steam, pc, nx, [], [], default, FixtureVersions);
            await workspace.CommitSwitchAsync(operation.Id, pair, client, info, Peer.Save, full.Path, inspection.Fingerprint, store, () => Task.CompletedTask, null, default);
            using var result = JsonDocument.Parse(peer.Files["main.dat"]); Assert(result.RootElement.GetProperty("playerData").GetProperty("progress").GetInt32() == 42 && result.RootElement.GetProperty("deviceSetting").GetString() == "console");
            Assert(peer.Files["nested/config.bin"].SequenceEqual(settings) && (await File.ReadAllBytesAsync(pcPath)).SequenceEqual(pc));
            var converted = await SnapshotStore.VerifyAsync(Path.Combine(operation.RecoveryDirectory, "converted-target.zip")); Assert(SnapshotStore.SameTarget(converted.Manifest, full.Manifest));
            Assert(db.Get<SavePair>("pairs", pair.Id)!.Value.Value.Baseline != null && peer.RestoreCommits == 1);
        });
        await Test("interrupted migration is retained for explicit recovery", () => Sync(() =>
        {
            using var db = new Phase2State(Path.Combine(phase, "interrupted.db")); var operation = new MigrationOperation("fixture", "pair", "hades", SavePlatform.Switch, MigrationState.Writing, Reading(1).Fingerprint, Reading(2).Fingerprint, Reading(3).Fingerprint, phase, "unused", [], MigrationFidelity.Full, DateTimeOffset.UtcNow);
            db.Put("migrations", operation.Id, operation); new MigrationWorkspace(db, phase, CompatibilityCatalog.Released).RecoverInterruptedOperations(); Assert(db.Get<MigrationOperation>("migrations", operation.Id)!.Value.Value.State == MigrationState.NeedsRecovery);
        }));
        await Test("native hiding admission rejects missing HOME proof or capability", () => Throws<DeviceException>(() => Sync(() => VisibilityPlans.Prepare(Peer.Info, [new(Peer.Save.TitleId, true, true, true)], [Peer.Save.TitleId], null))));
        await Test("visibility model journals before mutation and rolls back a partial failure", async () =>
        {
            var info = Peer.Info with { Capabilities = ["titles.visibility.v1"] }; var admission = new VisibilityAdmission(info.Firmware, info.Environment, true, true, true, 10, 3, true, true, "synthetic-model-only") { DeviceId = info.DeviceId };
            var current = new[] { new TitleVisibility("0100000000000000", true, true, true), new("0100000000002000", true, true, false), new("0100000000004000", true, true, false), new("0100000000006000", false, true, false) };
            var backend = new FixtureVisibility(current); var plan = VisibilityPlans.Prepare(info, current, [current[0].TitleId, current[3].TitleId], admission);
            using var db = new Phase2State(Path.Combine(phase, "visibility.db")); await Throws<IOException>(() => VisibilityPlans.ApplyAsync(plan, backend, db, info, admission, default));
            Assert(backend.Rows.SequenceEqual(current) && backend.JournalCount == 2 && db.Get<VisibilityJournal>("visibility-journals", plan.OperationId)!.Value.Value.State == "rolled-back");
            await Throws<DeviceException>(() => Sync(() => VisibilityPlans.Prepare(info with { DeviceId = "another" }, current, [current[0].TitleId], admission)));
        });
        await Test("PC snapshots deduplicate per owner and prune only ordinary history", async () =>
        {
            var directory = Path.Combine(phase, "pc-retention"); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "user1.dat"); var store = new PcSnapshotStore(Path.Combine(directory, "history"));
            var source = new PcSaveCandidate("hades", path, "Windows fixture", "1", "fixture"); PcSnapshot? last = null;
            for (var i = 1; i <= 3; i++) { await File.WriteAllBytesAsync(path, [(byte)i]); var file = await StableSaveFile.ReadAsync(path, default); last = await store.BackupAsync(source, file, Reading((byte)i), 2, default); }
            Assert(Directory.GetFiles(Path.Combine(directory, "history"), "snapshot.json", SearchOption.AllDirectories).Length == 2);
            var original = await StableSaveFile.ReadAsync(path, default); var duplicate = await store.BackupAsync(source, original, Reading(3), 2, default); Assert(duplicate.Directory == last!.Directory);
            await store.BackupAsync(source with { Owner = "different-account" }, original, Reading(3), 2, default); Assert(Directory.GetFiles(Path.Combine(directory, "history"), "snapshot.json", SearchOption.AllDirectories).Length == 3);
        });
        await Test("migration lineage preserves original save time instead of import mtime", () => Sync(() =>
        {
            var originalTime = DateTimeOffset.UtcNow.AddDays(-5); var origin = Reading(1, originalTime); var imported = Reading(2, DateTimeOffset.UtcNow);
            var operation = new MigrationOperation("fixture", "pair", "hollow-knight", SavePlatform.Switch, MigrationState.Complete, origin.Fingerprint, Reading(9).Fingerprint, imported.Fingerprint, "fixture", "fixture", [], MigrationFidelity.Full, DateTimeOffset.UtcNow) { SourceReading = origin, CompletedAt = DateTimeOffset.UtcNow };
            var reading = MigrationLineage.CarryOriginalTime(imported, "pair", SavePlatform.Steam, [operation]); Assert(reading.SavedAt == originalTime && reading.TimeSource == SaveTimeSource.MigratedSource && reading.MigratedAt != null);
            Assert(MigrationLineage.CarryOriginalTime(Reading(3), "pair", SavePlatform.Steam, [operation]).TimeSource == SaveTimeSource.ObservedOnly);
        }));
        await Test("Steam restart guard flags pre-game file replacement without treating ordinary play as cloud sync", () => Sync(() =>
        {
            var guard = new SteamCloudGuard(); guard.Observe(false, new Dictionary<string, bool> { ["hades"] = false }); guard.Observe(true, new Dictionary<string, bool> { ["hades"] = false });
            Assert(guard.NeedsReview("hades", false, Reading(1).Fingerprint, Reading(2).Fingerprint)); Assert(!guard.NeedsReview("hades", false, Reading(1).Fingerprint, Reading(1).Fingerprint));
            guard.Observe(true, new Dictionary<string, bool> { ["hades"] = true }); guard.Observe(true, new Dictionary<string, bool> { ["hades"] = false });
            Assert(!guard.NeedsReview("hades", false, Reading(1).Fingerprint, Reading(2).Fingerprint));
            var unknownStart = new SteamCloudGuard(); unknownStart.Observe(true, new Dictionary<string, bool>()); Assert(!unknownStart.NeedsReview("hades", false, Reading(1).Fingerprint, Reading(2).Fingerprint));
        }));
        await Test("TitleDB numeric comparison handles missing, new DLC and catalog staleness", async () =>
        {
            using var stream = new MemoryStream("id|rightsId|version\n0100000000000800|00000000000000000000000000000000|65536\n0100000000001001|00000000000000000000000000000000|0\n0100000000002800|00000000000000000000000000000000|\n"u8.ToArray());
            var catalog = await ContentCatalog.ReadTitleDbAsync(stream, DateTimeOffset.UtcNow.AddDays(-2), null, default);
            var game = new GameTitle("0100000000000000", "fixture", "", "9.99", "已安装", [new("0100000000000000", "Base", 0, "SdCard", 1), new("0100000000000800", "Update", 1, "SdCard", 1)]);
            var offers = ContentCatalog.Compare(game, catalog); Assert(offers.Single(x => x.Component.Kind == "Update").Status == UpdateStatus.UpdateAvailable && offers.Single(x => x.Component.Kind == "DLC").Status == UpdateStatus.NewDlc && catalog.NeedsRefresh(DateTimeOffset.UtcNow));
            Assert(ContentCatalog.Compare(game with { TitleId = "0100000000002000" }, catalog).Single().Status == UpdateStatus.Unknown);
        });
        foreach (var location in new[] { "../other/game.nsp", "%2e%2e/other/game.nsp", "%252e%252e/game.nsp", "https://other.invalid/games/game.nsp", "https://source.invalid/games2/game.nsp", "//other.invalid/game.nsp", "https://user:secret@source.invalid/games/x.nsp" })
            await Test("content source containment " + location, () => Throws<InvalidDataException>(() => Sync(() => ContentSources.Resolve(new("fixture", "fixture", ContentSourceKind.Http, "https://source.invalid/games/"), location))));
        await Test("mixed or mismatching packages never enter managed installation", async () =>
        {
            var entry = new ContentEntry("0100000000000000", "0100000000000800", "Update", 65536, 5L << 30, Reading(1).Fingerprint, "update.nsp");
            var component = new TitleComponent(entry.ComponentId, entry.Kind, entry.Version, "Package", entry.Size) { ApplicationId = entry.ApplicationId, RequiredApplicationVersion = 0, RequiredSystemVersion = 0 };
            ContentSources.ValidateInspected(entry, new("fixture", entry.Size, [component], entry.ApplicationId, "fixture", true));
            await Throws<InvalidDataException>(() => Sync(() => ContentSources.ValidateInspected(entry, new("fixture", entry.Size, [component, component], entry.ApplicationId, "fixture", true))));
            await Throws<InvalidDataException>(() => Sync(() => ContentSources.ValidateInspected(entry, new("fixture", entry.Size, [component with { Version = 1 }], entry.ApplicationId, "fixture", true))));
        });
        await Test("own local content source copies and verifies without modifying input", async () =>
        {
            var directory = Path.Combine(phase, "source"); Directory.CreateDirectory(directory); var data = new byte[Wire.ChunkSize + 13]; Random.Shared.NextBytes(data); await File.WriteAllBytesAsync(Path.Combine(directory, "fixture.nsp"), data);
            var entry = new ContentEntry("0100000000000000", "0100000000000800", "Update", 65536, data.Length, Fingerprints.Of(data), "fixture.nsp");
            using var downloader = new ContentDownloads(); var result = await downloader.FetchAsync(new("fixture", "fixture", ContentSourceKind.Local, directory), entry, Path.Combine(phase, "cache"), null, default);
            Assert((await File.ReadAllBytesAsync(result)).SequenceEqual(data)); Assert((await File.ReadAllBytesAsync(Path.Combine(directory, "fixture.nsp"))).SequenceEqual(data));
        });
        await Test("HTTP interrupted download resumes only with the same strong ETag", async () =>
        {
            var data = "fixture-download-content"u8.ToArray(); var cache = Path.Combine(phase, "resume"); Directory.CreateDirectory(cache); var sha = Fingerprints.Of(data); var partial = Path.Combine(cache, sha + ".nsp.part");
            await File.WriteAllBytesAsync(partial, data[..5]); await File.WriteAllTextAsync(partial + ".resume.json", JsonSerializer.Serialize(new DownloadResume("https://source.invalid/games/a.nsp", data.Length, sha, "\"fixture-1\""), Wire.Json));
            using var http = new HttpClient(new FixtureHttp(request =>
            {
                Assert(request.Headers.Range?.Ranges.Single().From == 5 && request.Headers.IfRange?.EntityTag?.Tag == "\"fixture-1\"");
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[5..]), RequestMessage = request };
                response.Headers.ETag = new EntityTagHeaderValue("\"fixture-1\""); response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, data.Length - 1, data.Length); return response;
            }));
            using var downloader = new ContentDownloads(http); var path = await downloader.FetchAsync(new("fixture", "fixture", ContentSourceKind.Http, "https://source.invalid/games/"), new("0100000000000000", "0100000000000800", "Update", 1, data.Length, sha, "a.nsp"), cache, null, default);
            Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(data) && !File.Exists(partial));
        });
        await Test("HTTP redirects and checksum failures publish no installable cache file", async () =>
        {
            var source = new ContentSource("fixture", "fixture", ContentSourceKind.Http, "https://source.invalid/games/"); var entry = new ContentEntry("0100000000000000", "0100000000000800", "Update", 1, 3, Fingerprints.Of([1, 2, 3]), "a.nsp");
            var cache = Path.Combine(phase, "invalid-download");
            using (var downloads = new ContentDownloads(new HttpClient(new FixtureHttp(request => new(HttpStatusCode.Found) { RequestMessage = request, Headers = { Location = new Uri("https://other.invalid/a.nsp") } })))) await Throws<IOException>(() => downloads.FetchAsync(source, entry, cache, null, default));
            using (var downloads = new ContentDownloads(new HttpClient(new FixtureHttp(request => new(HttpStatusCode.OK) { Content = new ByteArrayContent([3, 2, 1]), RequestMessage = request })))) await Throws<InvalidDataException>(() => downloads.FetchAsync(source, entry, cache, null, default));
            Assert(!Directory.GetFiles(cache, "*.nsp").Any());
        });
    }
    private sealed class FixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
    private sealed class FixtureVisibility(TitleVisibility[] initial) : ITitleVisibility
    {
        public TitleVisibility[] Rows { get; private set; } = initial.ToArray();
        public int JournalCount { get; private set; } private int calls;
        public Task<TitleVisibility[]> ListAsync(CancellationToken ct) => Task.FromResult(Rows.ToArray());
        public Task WriteRecoveryJournalAsync(VisibilityPlan plan, CancellationToken ct) { JournalCount++; return Task.CompletedTask; }
        public Task SetVisibleAsync(string titleId, bool visible, CancellationToken ct)
        {
            Assert(JournalCount > 0); if (++calls == 2) throw new IOException("synthetic partial visibility failure");
            Rows = Rows.Select(r => r.TitleId == titleId ? r with { Visible = visible } : r).ToArray(); return Task.CompletedTask;
        }
    }
}
