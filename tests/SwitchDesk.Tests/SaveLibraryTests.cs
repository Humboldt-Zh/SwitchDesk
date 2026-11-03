using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task RunSaveLibraryTestsAsync()
    {
        await Test("save library scans existing v1 ZIP and Recovery manifests without reading payloads", async () =>
        {
            var directory = Path.Combine(root, "save-library-scan");
            var first = Manifest("0100000000000011", "first") with { Directories = null };
            var second = Manifest("0100000000000022", "second") with { CreatedAt = first.CreatedAt.AddMinutes(1) };
            WriteManifest(Path.Combine(directory, "Snapshots", "old-format1.zip"), first);
            WriteManifest(Path.Combine(directory, "Recovery", "protected.zip"), second);
            WriteManifest(Path.Combine(directory, "unsupported.zip"), first with { FormatVersion = 2 });
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "damaged.zip"), "not a ZIP");
            using (var zip = ZipFile.Open(Path.Combine(directory, "missing-manifest.zip"), ZipArchiveMode.Create)) zip.CreateEntry("unrelated.txt");
            await File.WriteAllTextAsync(Path.Combine(directory, "ignored.zip.partial"), "in progress");
            var store = new SnapshotStore(directory); var scan = store.Scan();
            Assert(scan.Snapshots.Length == 2 && scan.Snapshots[0].Manifest.TitleId == second.TitleId);
            Assert(scan.Issues.Length == 3 && scan.Issues.Any(i => i.Reason.Contains("版本", StringComparison.Ordinal)));
            Assert(scan.RecoveryPaths is { Length: 1 } && store.List().Count == 2);
            // These fixtures deliberately contain no payload: discovery must not
            // imply that restoration or full snapshot verification has passed.
            await Throws<InvalidDataException>(() => SnapshotStore.VerifyAsync(scan.Snapshots[0].Path));
            Assert(Directory.GetFiles(directory, "*.zip", SearchOption.AllDirectories).Length == 5);
        });
        await Test("save library shows five backup-only games without installation files or a connected console", () => Sync(() =>
        {
            var vm = new SaveLibraryViewModel();
            var history = Enumerable.Range(1, 5).Select(i => new Snapshot($"backup-{i}.zip", Manifest($"01000000000000{i:X2}", $"游戏 {i}"))).ToArray();
            vm.SetData([], new(history, []), null, false, null);
            Assert(vm.Games.Count == 5 && vm.Games.All(g => g.BackupCount == 1 && g.Status == "仅电脑备份"));
            vm.SourceFilter = "电脑备份"; Assert(vm.Games.Count == 5);
            vm.Search = "游戏 3"; Assert(vm.Games.Count == 1);
            vm.ClearFiltersCommand.Execute(null); Assert(vm.Games.Count == 5);
            vm.OpenGameCommand.Execute(vm.Games[0]); Assert(vm.TimelineOpen && vm.Detail.History.Count == 1 && !vm.Detail.HasLive);
            vm.SelectedGame = null; Assert(!vm.TimelineOpen && vm.SelectedSource == null && vm.Detail.History.Count == 0);
        }));
        await Test("save library isolates device, environment, user and save type and defaults to current source", () => Sync(() =>
        {
            var vm = new SaveLibraryViewModel(); var target = Peer.Save;
            var manifest = Manifest(target.TitleId, target.GameName) with { DeviceId = Peer.Info.DeviceId, Environment = Peer.Info.Environment, UserId = target.UserId, UserName = target.UserName, SaveType = target.Type };
            var current = new Snapshot("current.zip", manifest);
            var otherDevice = new Snapshot("other.zip", manifest with { DeviceId = "OTHER", CreatedAt = manifest.CreatedAt.AddDays(1) });
            var otherEnvironment = new Snapshot("sysmmc.zip", manifest with { Environment = "different-environment" });
            var otherUser = new Snapshot("user.zip", manifest with { UserId = "USER-B" });
            var shared = new Snapshot("shared.zip", manifest with { UserId = "", SaveType = "Device" });
            var scan = new SnapshotScanResult([current, otherDevice, otherEnvironment, otherUser, shared], [], ["current.zip"]);
            vm.SetData([target], scan, Peer.Info, true, DateTimeOffset.UtcNow);
            Assert(vm.Games.Count == 1 && vm.Games[0].BackupCount == 5 && vm.Games[0].Sources.Length == 5);
            vm.OpenGameCommand.Execute(vm.Games[0]);
            Assert(vm.SelectedSource?.Identity.DeviceId == Peer.Info.DeviceId && vm.Detail.HasLive);
            Assert(vm.Detail.History.Count == 1 && vm.Detail.History[0] == current && vm.Versions[0].Recovery);
            vm.SelectedSnapshot = current;
            vm.SelectedSource = vm.Sources.Single(s => s.Identity.DeviceId == "OTHER");
            Assert(vm.Detail.History.Count == 1 && vm.Detail.History[0] == otherDevice && !vm.Detail.HasLive && vm.SelectedSnapshot == null);
            var selectedIdentity = vm.SelectedSource.Identity;
            vm.SetData([target], scan, Peer.Info, false, DateTimeOffset.UtcNow);
            Assert(vm.SelectedSource?.Identity == selectedIdentity && !vm.Detail.HasLive);
            foreach (var source in vm.Sources) { vm.SelectedSource = source; Assert(vm.Detail.History.Count == 1); }
            vm.SelectedSource = vm.Sources.Single(s => s.Record != null);
            Assert(vm.SourceStatus.Contains("离线记录", StringComparison.Ordinal) && vm.SourceStatus.Contains("上次读取", StringComparison.Ordinal));
            vm.SetData([target], scan, Peer.Info with { Capabilities = ["titles"] }, true, DateTimeOffset.UtcNow);
            Assert(!vm.Detail.HasLive && vm.SourceStatus.Contains("当前模式不可读取", StringComparison.Ordinal));
        }));
        await Test("save library picks latest historical source, refreshes directory changes and reports unreadable backups", () => Sync(() =>
        {
            var vm = new SaveLibraryViewModel(); var first = new Snapshot("a.zip", Manifest("0100000000000011", "A"));
            var latest = new Snapshot("b.zip", first.Manifest with { DeviceId = "NEW", CreatedAt = first.Manifest.CreatedAt.AddDays(1) });
            vm.SetData([], new([first, latest], [new("broken.zip", "测试损坏")]), null, false, null);
            vm.OpenGameCommand.Execute(vm.Games[0]); Assert(vm.SelectedSource?.Identity.DeviceId == "NEW");
            Assert(vm.HasIssues && vm.IssueSummary.Contains('1') && vm.IssueDetails.Contains("测试损坏", StringComparison.Ordinal));
            vm.SelectedSnapshot = latest;
            var replacement = new Snapshot("replacement.zip", Manifest("0100000000000022", "B"));
            vm.SetData([], new([replacement], []), null, false, null);
            Assert(vm.Games.Count == 1 && vm.Games[0].Name == "B" && !vm.TimelineOpen && vm.SelectedSnapshot == null && !vm.HasIssues);
            vm.SetData([], new([], []), null, false, null); Assert(vm.Empty && vm.EmptyHeading == "还没有存档记录");
        }));
        await Test("save library keeps cached-only records visible but does not expose them as live saves", () => Sync(() =>
        {
            var vm = new SaveLibraryViewModel();
            vm.SetData([Peer.Save], new([], []), Peer.Info, false, DateTimeOffset.UtcNow.AddDays(-1));
            vm.OpenGameCommand.Execute(vm.Games.Single());
            Assert(vm.SelectedGame?.Status == "主机离线记录" && vm.SelectedSource?.Record != null && !vm.Detail.HasLive && vm.HistoryEmpty);
            vm.SetData([Peer.Save], new([], []), Peer.Info, true, DateTimeOffset.UtcNow);
            Assert(vm.SelectedGame?.Status == "当前主机存档" && vm.Detail.HasLive && vm.Detail.Selected?.Live?.Key == Peer.Save.Key);
        }));
        await Test("save library refresh preserves selected version and refreshes equal source records", () => Sync(() =>
        {
            var vm = new SaveLibraryViewModel(); var snapshot = new Snapshot("refresh.zip", Manifest(Peer.Save.TitleId, Peer.Save.GameName));
            var scan = new SnapshotScanResult([snapshot], []);
            vm.SetData([], scan, null, false, null); vm.SelectedGame = vm.Games.Single(); vm.SelectedSnapshot = snapshot;
            // Simulate the transient null written by a bound ComboBox while its
            // source collection is rebuilt during refresh.
            vm.Sources.CollectionChanged += (_, _) => { if (vm.Sources.Count == 0) vm.SelectedSource = null; };
            vm.SetData([], scan, null, false, null);
            Assert(vm.SelectedSnapshot?.Path == snapshot.Path && vm.Detail.History.Count == 1 && vm.Versions.Count == 1);
            var newer = new Snapshot("newer.zip", snapshot.Manifest with { CreatedAt = snapshot.Manifest.CreatedAt.AddMinutes(1) });
            vm.SetData([], new([snapshot, newer], [], [snapshot.Path]), null, false, null);
            Assert(vm.SelectedGame?.BackupCount == 2 && vm.Detail.History.Count == 2 && vm.Versions.Any(v => v.Recovery));
            Assert(vm.SelectedSnapshot?.Path == snapshot.Path);
            vm.SelectedGame = null; Assert(!vm.TimelineOpen && vm.Detail.History.Count == 0 && vm.SelectedSnapshot == null);
        }));
    }

    private static SaveManifest Manifest(string titleId, string gameName) => new(1, "FIXTURE-DEVICE", "fixture-environment", titleId,
        gameName, "FIXTURE-USER", "Fixture User", "Account", DateTimeOffset.UtcNow, [], new string('A', 64), []);

    private static void WriteManifest(string path, SaveManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(stream, manifest, Wire.Json);
    }
}
