using System.IO;
using System.Text.Json;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    private static async Task UiV2Tests()
    {
        await Test("WPF commands: batch stop/skip, direct backup, offline/reconnect", RunUiIntegrationAsync);
        await Test("UI library merges known content; unknown names never inferred", () => Sync(() =>
        {
            var vm = new LibraryViewModel(); var game = new GameTitle("0100000000000000", "Known", "Test", "1", "已安装", [new("0100000000000800", "Update", 1, "SdCard", 1024)]);
            LibraryItem Local(string name, string? id, string file) => new(Path.Combine(root, file), name, id, "", "", 4096, null, "待识别");
            vm.Merge([game], [Local("filename cannot identify this", null, "a.nsp"), Local("Known", null, "b.nsp"), Local("Update", "0100000000000800", "c.nsp")]);
            Assert(vm.All.Count == 3 && vm.All.Count(x => x.Unknown) == 2); Assert(vm.All.Single(x => x.OnConsole).Local.Length == 1);
            vm.Selected = vm.All.Single(x => x.OnConsole); vm.Selected.Components[0].Selected = false; vm.Search = "nothing"; Assert(vm.Filtered.Count == 0 && vm.Selected != null); vm.Search = "";
            vm.Source = "主机"; Assert(vm.Filtered.Count == 1); vm.Source = "电脑"; Assert(vm.Filtered.Count == 3); vm.Source = "全部";
            vm.SaveTitles = [game.TitleId]; vm.SavesOnly = true; Assert(vm.Filtered.Count == 1);
            vm.SavesOnly = false; vm.Merge([game with { DisplayVersion = "2" }], []); Assert(vm.Selected?.Key == "title:" + game.TitleId && !vm.Selected.Components[0].Selected);
        }));
        await Test("UI 500 items rebuild and resize retain stable selection", () => Sync(() =>
        {
            var vm = new LibraryViewModel(); var items = Enumerable.Range(0, 500).Select(i => new GameTitle($"01000000{i:X8}", $"Item {i}", "", "1", "已安装", [])).ToArray();
            vm.Merge(items, []); vm.Selected = vm.All[250]; var key = vm.Selected.Key;
            foreach (var width in new[] { 450, 650, 1500, 450 }) { vm.Resize(width); Assert(vm.Rows.Sum(x => x.Cards.Length) == 500); Assert(vm.Rows.All(x => x.Cards.All(c => c.Width is >= 180 and <= 208))); }
            vm.Merge(items, []); Assert(vm.Selected?.Key == key);
        }));
        await Test("UI save timeline isolates console, environment, title, user and save type", () => Sync(() =>
        {
            var vm = new SaveViewModel(); var target = Peer.Save;
            Snapshot Snapshot(string device, string environment, string user, string title, string type) => new("fixture.zip", new(1, device, environment, title, "test", user, "User", type, DateTimeOffset.UtcNow, [], "hash"));
            var exact = Snapshot(Peer.Info.DeviceId, Peer.Info.Environment, target.UserId, target.TitleId, target.Type);
            vm.SetData([target, target with { Key = "shared", UserId = "", UserName = "Shared", Type = "Device" }], [exact, Snapshot("OTHER", Peer.Info.Environment, target.UserId, target.TitleId, target.Type), Snapshot(Peer.Info.DeviceId, "sysMMC", target.UserId, target.TitleId, target.Type), Snapshot(Peer.Info.DeviceId, Peer.Info.Environment, "USER-B", target.TitleId, target.Type), Snapshot(Peer.Info.DeviceId, Peer.Info.Environment, target.UserId, "0100000000000001", target.Type)]);
            vm.SetContext(target.TitleId, Peer.Info); Assert(vm.History.Count == 1 && vm.History[0] == exact); Assert(vm.Selected!.ToString() == vm.Selected.Label);
            vm.Selected = vm.Users.Single(x => x.Type == "Device"); Assert(vm.History.Count == 0 && vm.Selected.Label.Contains("设备共享"));
            var changed = 0; vm.AutoChanged += _ => changed++; vm.SetAutomatic(true); Assert(changed == 0 && vm.AutoBackup); vm.AutoBackup = false; Assert(changed == 1);
            vm.SetContext("0100000000000001", Peer.Info); Assert(vm.Users.Count == 1 && !vm.HasLive && vm.History.Count == 1);
        }));
        await Test("UI truthful progress and non-cancellable save commit", () => Sync(() =>
        {
            var job = new Job { Name = "restore" }; Assert(job.CanCancel && job.Indeterminate);
            job.Report(new(1, 4, "传输")); Assert(job.Percent == 25 && !job.Indeterminate && job.State == "传输");
            job.Report(new(0, 0, "正在写回存档，请保持连接")); Assert(!job.CanCancel && job.Indeterminate && job.State == "主机处理" && !job.ProgressText.Contains('%'));
            var tasks = new TasksViewModel(); tasks.Add(job); job.State = "完成"; Assert(!job.CanCancel && tasks.CountText == "1 项历史");
        }));
        await Test("UI cache survives restart and separates system identities", async () =>
        {
            var cacheRoot = Path.Combine(root, "cache-v2"); var cache = new DeviceCache(cacheRoot);
            var first = new CachedConsole(Peer.Info, [], [new("0100000000000000", "offline", "", "1", "已安装", [])], [Peer.Save], DateTimeOffset.UtcNow.AddDays(-1));
            await cache.SaveAsync(first); var second = first with { Device = Peer.Info with { Environment = "sysMMC" }, SyncedAt = DateTimeOffset.UtcNow };
            await cache.SaveAsync(second); var read = new DeviceCache(cacheRoot).Latest(); Assert(read!.Device.Environment == "sysMMC" && read.Games[0].Name == "offline");
            Assert(cache.IconPath(first.Device, "title") != cache.IconPath(second.Device, "title"));
            var corrupt = Path.Combine(cacheRoot, "corrupt"); Directory.CreateDirectory(corrupt); await File.WriteAllTextAsync(Path.Combine(corrupt, "inventory.json"), "{broken"); Assert(cache.Latest()!.Device.Environment == "sysMMC");
        });
        await Test("UI settings remain compatible with v1 files", () => Sync(() =>
        {
            var old = "{\"backupDirectory\":\"C:\\\\Backup\",\"libraryDirectory\":\"C:\\\\Library\",\"exportDirectory\":\"C:\\\\Exports\",\"sysDvrClient\":\"\",\"retention\":10,\"autoBackupKeys\":[]}";
            var settings = JsonSerializer.Deserialize<AppSettings>(old, Wire.Json)!; Assert(settings.Theme == "System" && !settings.ListView);
        }));
        await Test("batch revalidation checks total decompressed space and system identity", async () =>
        {
            var path = Path.Combine(root, "install-v2.nsz"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var source = InstallSource.Capture(path, new("test", 5L << 30, [], null, "test", true));
            var target = new StorageInfo("SdCard", "SD", 20L << 30, 9L << 30);
            Assert(InstallBatch.Validate([source], Peer.Info, Peer.Info, target) == 5L << 30);
            await Throws<IOException>(() => Sync(() => InstallBatch.Validate([source, source], Peer.Info, Peer.Info, target)));
            await Throws<IOException>(() => Sync(() => InstallBatch.Validate([source], Peer.Info, Peer.Info with { Environment = "sysMMC" }, target)));
            await Throws<IOException>(() => Sync(() => InstallBatch.Validate([source], Peer.Info, Peer.Info with { DeviceId = "OTHER" }, target)));
            await Throws<IOException>(() => Sync(() => InstallBatch.Validate([source], Peer.Info, Peer.Info with { ApplicationMode = false }, target)));
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]); await Throws<IOException>(() => Sync(source.Validate));
        });
    }
}
