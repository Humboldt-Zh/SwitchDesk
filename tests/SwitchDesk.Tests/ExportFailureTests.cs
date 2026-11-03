using System.IO;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;

internal static partial class Program
{
    private static async Task ExportFailureTestsAsync()
    {
        await Test("export read failure closes handle and publishes no incomplete content", async () =>
        {
            var directory = Path.Combine(root, "export-failure");
            await using var peer = new Peer { FailRead = true };
            await using var client = await peer.ConnectAsync();
            await Throws<DeviceException>(() => Transfers.ExportGameAsync(client, Peer.Info,
                new(Peer.Save.TitleId, "Failed export", "", "1", "已安装", []),
                new(Peer.Save.TitleId, "Base", 0, "SdCard", 5), directory, null, default));
            Assert(peer.ExportOpens == 1 && peer.CloseOperations == 1 && peer.ActiveHandleCount == 0);
            Assert(!Directory.EnumerateFiles(directory).Any(), "failed export left a file or manifest");
        });
        await Test("cancelled export between chunks cleans only its own partials and closes handle", async () =>
        {
            var directory = Path.Combine(root, "export-cancel"); Directory.CreateDirectory(directory);
            var unrelated = Path.Combine(directory, "other-task.partial"); await File.WriteAllTextAsync(unrelated, "keep");
            await using var peer = new Peer(); peer.Files["main.dat"] = new byte[Wire.ChunkSize + 19];
            await using var client = await peer.ConnectAsync(); using var cancellation = new CancellationTokenSource();
            await Throws<OperationCanceledException>(() => Transfers.ExportGameAsync(client, Peer.Info,
                new(Peer.Save.TitleId, "Cancelled export", "", "1", "已安装", []),
                new(Peer.Save.TitleId, "Base", 0, "SdCard", peer.Files["main.dat"].Length), directory,
                new CancelExportProgress(cancellation), cancellation.Token));
            Assert(peer.CloseOperations == 1 && peer.ActiveHandleCount == 0);
            Assert(Directory.EnumerateFiles(directory).SequenceEqual(new[] { unrelated }));
            Assert(await File.ReadAllTextAsync(unrelated) == "keep");
        });
    }

    private sealed class CancelExportProgress(CancellationTokenSource cancellation) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) { if (value.Completed > 0) cancellation.Cancel(); }
    }
}
