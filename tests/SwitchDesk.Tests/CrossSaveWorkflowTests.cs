using System.IO;
using SwitchDesk.App;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task RunCrossSaveWorkflowTestsAsync()
    {
        await Test("cross-save registry lists six games and only confirmed Switch identities", () => Sync(() =>
        {
            Assert(CrossSaveGames.All.Count == 6 && CrossSaveGames.All.Select(x => x.AdapterId).Distinct().Count() == 6);
            Assert(CrossSaveGames.ForTitle("010089a0197e4000")?.AdapterId == "vampire-survivors");
            Assert(CrossSaveGames.ForTitle("0100646009FBE000")?.AdapterId == "dead-cells");
            Assert(CrossSaveGames.ForTitle(Peer.Save.TitleId) == null && CrossSaveGames.ForTitle(null) == null, "unrelated game was matched to an adapter");
            Assert(CrossSaveGames.All.Where(x => x.AdapterId is not ("vampire-survivors" or "dead-cells")).All(x => !x.HasSwitchIdentity));
        }));
        await Test("cross-save file selection has an explicit first-read and multi-file state", () => Sync(() =>
        {
            var vm = new CrossSaveViewModel(); Assert(!vm.FilesReady && !vm.CanChooseFile && vm.SelectedSwitchFile == null);
            vm.SetFiles([new("slot-1.dat", 100), new("settings/config.dat", 20)]);
            Assert(vm.FilesReady && vm.CanChooseFile && vm.SelectedSwitchFile == null, "multiple files silently chose an arbitrary slot");
            vm.SelectedSwitchFile = "slot-1.dat"; Assert(vm.SelectedSwitchFile == "slot-1.dat");
            vm.SelectedSwitchFile = "unlisted.dat"; Assert(vm.SelectedSwitchFile == null, "unlisted file became selectable");
            vm.SetFiles([new("only.dat", 10)]); Assert(vm.SelectedSwitchFile == "only.dat");
            vm.CanSelect = false; Assert(!vm.CanChooseFile); vm.CanSelect = true; vm.FilesLoading = true; Assert(!vm.CanChooseFile);
        }));
        await Test("cross-save excludes empty and oversized files without losing their explanation", () => Sync(() =>
        {
            var vm = new CrossSaveViewModel(); vm.SetFiles([new("empty", 0), new("large", StableSaveFile.MaximumBytes + 1L)]);
            Assert(vm.SwitchFileOptions.Length == 2 && vm.SwitchFiles.Length == 0 && vm.SelectedSwitchFile == null && !vm.CanChooseFile);
            Assert(vm.SwitchFileOptions[0].Label.Contains("空文件", StringComparison.Ordinal) && vm.SwitchFileOptions[1].Label.Contains("上限", StringComparison.Ordinal));
        }));
        await Test("cross-save user and target changes clear old files and migration preview", () => Sync(() =>
        {
            var vm = new CrossSaveViewModel { Adapter = SaveAdapters.Get("vampire-survivors") };
            var save = Peer.Save with { TitleId = "010089A0197E4000" };
            vm.SelectedTargetGame = new(save.TitleId, "测试吸血鬼");
            vm.Users = [new(save.Key, save.UserName, save.Type, save), new("second", "第二用户", save.Type, save with { Key = "second", UserId = "USER-B", UserName = "第二用户" })];
            vm.SelectedUser = vm.Users[0]; vm.SetFiles([new("user-a.dat", 1)]); vm.CanMigrate = true;
            vm.SelectedUser = vm.Users[1];
            Assert(vm.SwitchFiles.Length == 0 && vm.SelectedSwitchFile == null && !vm.FilesReady && !vm.CanMigrate, "previous user's slot survived selection");
            vm.SetFiles([new("user-b.dat", 1)]); vm.SelectedTargetGame = new("0100646009FBE000", "测试死亡细胞");
            Assert(vm.Users.Length == 0 && vm.SelectedUser == null && vm.SwitchFiles.Length == 0);
            vm.Adapter = SaveAdapters.Get("hades"); Assert(!vm.SelectedGame!.Definition.HasSwitchIdentity && vm.SelectedTargetGame == null);
        }));
        await Test("cross-save discovery reads metadata only and closes the legacy save handle", async () =>
        {
            await using var peer = new Peer { FailRead = true }; await using var client = await peer.ConnectAsync();
            var files = await CrossSaveDiscovery.ListFilesAsync(client, Peer.Info, Peer.Save, default);
            Assert(files.Length == 2 && files.All(x => x.CanCompare));
            Assert(peer.ReadOpens == 1 && peer.CloseOperations == 1 && peer.ActiveHandleCount == 0 && peer.WriteOperations == 0 && peer.RestoreCommits == 0);
            Assert(!client.IsBroken && (await client.HandshakeAsync()).DeviceId == Peer.Info.DeviceId, "metadata discovery broke the connection");
        });
        await Test("cross-save empty directory remains a valid discovery result", async () =>
        {
            await using var peer = new Peer(); peer.BeforeHandleTree = () => { peer.Files.Clear(); return Task.CompletedTask; };
            await using var client = await peer.ConnectAsync();
            var files = await CrossSaveDiscovery.ListFilesAsync(client, Peer.Info, Peer.Save, default);
            Assert(files.Length == 0 && peer.ActiveHandleCount == 0 && peer.CloseOperations == 1);
        });
        await Test("cross-save discovery closes the handle after invalid paths and remains synchronized", async () =>
        {
            await using var peer = new Peer(); peer.Files["../escape.dat"] = [1]; await using var client = await peer.ConnectAsync();
            await Throws<InvalidDataException>(async () => await CrossSaveDiscovery.ListFilesAsync(client, Peer.Info, Peer.Save, default));
            Assert(peer.CloseOperations == 1 && peer.ActiveHandleCount == 0 && !client.IsBroken);
            Assert((await client.HandshakeAsync()).Environment == Peer.Info.Environment);
        });
        await Test("cross-save discovery rejects file-directory collisions and releases its handle", async () =>
        {
            await using var peer = new Peer { DirectoryConflict = true }; await using var client = await peer.ConnectAsync();
            await Throws<InvalidDataException>(async () => await CrossSaveDiscovery.ListFilesAsync(client, Peer.Info, Peer.Save, default));
            Assert(peer.CloseOperations == 1 && peer.ActiveHandleCount == 0 && peer.WriteOperations == 0);
        });
        await Test("cross-save discovery refuses Applet, wrong environment, and precancelled requests before mounting", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await Throws<DeviceException>(async () => await CrossSaveDiscovery.ListFilesAsync(client, Peer.Info with { ApplicationMode = false }, Peer.Save, default));
            await Throws<DeviceException>(async () => await CrossSaveDiscovery.ListFilesAsync(client, Peer.Info with { Capabilities = ["files"] }, Peer.Save, default));
            await Throws<DeviceException>(async () => await CrossSaveDiscovery.ListFilesAsync(client, Peer.Info, Peer.Save with { Environment = "sysMMC" }, default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws<OperationCanceledException>(async () => await CrossSaveDiscovery.ListFilesAsync(client, Peer.Info, Peer.Save, cancelled.Token));
            Assert(peer.ReadOpens == 0 && peer.CloseOperations == 0 && !client.IsBroken);
        });
    }
}
