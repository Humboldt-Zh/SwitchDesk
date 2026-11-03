using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchDesk.Core;

public enum InstallOutcome { Upgraded, Installed, NoChange }
public sealed record ManagedInstallPlan(string OperationId, string DeviceId, string Environment, string Storage,
    ContentEntry Entry, string PackagePath, long RequiredBytes, TitleComponent[] Before);
public sealed record ManagedInstallResult(InstallOutcome Outcome, uint ActualVersion, string[] RecoverySnapshots);
public static class ManagedUpdates
{
    public static void DemandCapabilities(DeviceInfo info)
    {
        if (!info.ApplicationMode || !new[] { "install.plan.v1", "package.dependencies.v1", "operations.status", "saves" }.All(info.Capabilities.Contains))
            throw new DeviceException("CapabilityMissing", "管理更新需要支持组件约束和操作状态的新版完整应用模式配套程序");
    }
    public static uint FirmwareNumber(string firmware)
    {
        var fields = firmware.Split('.');
        if (fields.Length < 3 || !uint.TryParse(fields[0], out var major) || !uint.TryParse(fields[1], out var minor) || !uint.TryParse(fields[2], out var micro) || major > 63 || minor > 63 || micro > 15) throw new InvalidDataException("无法核对主机系统数字版本");
        return major << 26 | minor << 20 | micro << 16;
    }
    public static ManagedInstallPlan Prepare(DeviceInfo device, ContentEntry entry, PackageInfo package, string packagePath, string storage, StorageInfo volume, GameTitle[] titles)
    {
        DemandCapabilities(device); ContentSources.ValidateInspected(entry, package);
        if (entry.Kind == "Base") throw new InvalidOperationException("管理更新入口处理更新与 DLC；本体请使用现有安装清单");
        if (storage is not ("SdCard" or "BuiltInUser") || volume.Id != storage || package.RequiredBytes <= 0 || package.RequiredBytes > volume.FreeBytes) throw new IOException("目标存储或可用空间不满足安装计划");
        var game = titles.SingleOrDefault(g => g.TitleId.Equals(entry.ApplicationId, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("缺少对应地区的游戏本体");
        if (!game.Components.Any(c => c.Kind == "Base")) throw new IOException("本体未安装或卡带不可用");
        var component = package.Components[0]; var requiredSystem = component.RequiredSystemVersion!.Value;
        if (requiredSystem > FirmwareNumber(device.Firmware)) throw new IOException("内容要求更高的系统版本；不会自动更新主机或降低内容要求");
        var appVersion = game.Components.Where(c => c.Kind is "Base" or "Update").Max(c => c.Version);
        if (component.RequiredApplicationVersion > appVersion) throw new IOException("DLC 需要先安装匹配的游戏更新");
        if (component.Kind == "DataPatch" && (component.RequiredContentId == null || !game.Components.Any(c => c.Kind == "DLC" && c.TitleId.Equals(component.RequiredContentId, StringComparison.OrdinalIgnoreCase)))) throw new IOException("DLC 更新缺少对应 DLC 本体或其依赖信息");
        var installed = game.Components.Where(c => c.TitleId.Equals(entry.ComponentId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (installed.Any(c => c.Version > entry.Version)) throw new IOException("拒绝自动降级组件");
        return new(Guid.NewGuid().ToString("N"), device.DeviceId, device.Environment, storage, entry, Path.GetFullPath(packagePath), package.RequiredBytes, game.Components);
    }
    public static string InventoryFingerprint(IEnumerable<TitleComponent> components)
        => Fingerprints.Of(JsonSerializer.SerializeToUtf8Bytes(components.OrderBy(c => c.TitleId, StringComparer.Ordinal).ThenBy(c => c.Version).ThenBy(c => c.Storage, StringComparer.Ordinal).Select(c => new { c.TitleId, c.Kind, c.Version, c.Storage, c.Size }), Wire.Json));
    /// <summary>Caller holds the shared host write gate for the entire preflight/protection/install/verification sequence.</summary>
    public static async Task<ManagedInstallResult> ExecuteAsync(DeviceClient client, ManagedInstallPlan plan, SnapshotStore snapshots, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var device = await client.QueryAsync<DeviceInfo>("device.info", ct: ct); DemandCapabilities(device);
        if (device.DeviceId != plan.DeviceId || device.Environment != plan.Environment) throw new IOException("确认后主机或系统环境已变化");
        await ContentDownloads.VerifyAsync(plan.PackagePath, plan.Entry, ct);
        var package = await Transfers.InspectAsync(client, plan.PackagePath, ct); ContentSources.ValidateInspected(plan.Entry, package);
        var titles = await client.QueryAsync<GameTitle[]>("titles.list", ct: ct); var volume = (await client.QueryAsync<StorageInfo[]>("storages.list", ct: ct)).Single(v => v.Id == plan.Storage);
        var refreshed = Prepare(device, plan.Entry, package, plan.PackagePath, plan.Storage, volume, titles);
        if (!Fingerprints.Equal(InventoryFingerprint(plan.Before), InventoryFingerprint(refreshed.Before))) throw new IOException("确认后游戏内容已变化，请重新生成安装计划");
        var saved = await client.QueryAsync<SaveInfo[]>("saves.list", ct: ct); var recovery = new List<string>();
        foreach (var save in saved.Where(s => s.TitleId.Equals(plan.Entry.ApplicationId, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report(new(0, 0, "安装前保护游戏存档")); var copy = await snapshots.BackupAsync(client, device, save, progress, ct, recovery: true); await SnapshotStore.VerifyAsync(copy.Path, ct); recovery.Add(copy.Path);
        }
        // Re-read after backup; neither a second client nor a reconnect may silently change the expected game.
        var latest = await client.QueryAsync<GameTitle[]>("titles.list", ct: ct); var actualBefore = latest.Single(g => g.TitleId.Equals(plan.Entry.ApplicationId, StringComparison.OrdinalIgnoreCase)).Components;
        if (!Fingerprints.Equal(InventoryFingerprint(plan.Before), InventoryFingerprint(actualBefore))) throw new IOException("保护存档期间游戏内容已变化，未安装");
        await using var source = new FileStream(plan.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize, true);
        if (source.Length != plan.Entry.Size || !Fingerprints.Equal(Convert.ToHexString(await SHA256.HashDataAsync(source, ct)), plan.Entry.Sha256)) throw new InvalidDataException("安装包在预检后发生变化");
        source.Position = 0;
        await client.CallAsync("titles.install.plan", new { plan.OperationId, expectedDeviceId = plan.DeviceId, expectedEnvironment = plan.Environment, plan.Storage,
            name = Path.GetFileName(plan.PackagePath), size = source.Length, expected = plan.Entry, before = plan.Before }, ct, source: source, progress: progress);
        var after = await client.QueryAsync<GameTitle[]>("titles.list", ct: ct);
        var version = after.SelectMany(g => g.Components).Where(c => c.TitleId.Equals(plan.Entry.ComponentId, StringComparison.OrdinalIgnoreCase)).Select(c => (uint?)c.Version).Max();
        if (version != plan.Entry.Version) throw new IOException("安装调用已返回，但读回版本未达到计划版本；请查看诊断与保护快照");
        var oldVersion = plan.Before.Where(c => c.TitleId.Equals(plan.Entry.ComponentId, StringComparison.OrdinalIgnoreCase)).Select(c => (uint?)c.Version).Max();
        return new(oldVersion == version ? InstallOutcome.NoChange : oldVersion == null ? InstallOutcome.Installed : InstallOutcome.Upgraded, version.Value, recovery.ToArray());
    }
}
public sealed record ManagedBatchItem(ContentEntry Entry, PackageInfo Package, string Path, string OperationId);
public sealed record ManagedBatchPlan(string Id, string DeviceId, string Environment, string ApplicationId, string Storage, long RequiredBytes, TitleComponent[] Before, ManagedBatchItem[] Items);
public sealed record ManagedBatchResult(string OperationId, ManagedInstallResult Result);
public sealed class ManagedBatchException(string message, IReadOnlyList<ManagedBatchResult> completed, Exception cause) : IOException(message, cause)
{ public IReadOnlyList<ManagedBatchResult> Completed { get; } = completed; }
public sealed class ManagedBatchCanceledException(string message, IReadOnlyList<ManagedBatchResult> completed, Exception cause, CancellationToken ct) : OperationCanceledException(message, cause, ct)
{ public IReadOnlyList<ManagedBatchResult> Completed { get; } = completed; }
public static class ManagedUpdateBatches
{
    public static ManagedBatchPlan Prepare(DeviceInfo device, string storage, StorageInfo volume, GameTitle[] titles, ManagedBatchItem[] items)
    {
        if (items.Length == 0 || items.Select(x => x.Entry.ComponentId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length || items.Select(x => x.Entry.ApplicationId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) throw new InvalidDataException("批量更新必须属于同一游戏且每个组件只选一个版本");
        var ordered = items.OrderBy(x => x.Entry.Kind == "Update" ? 0 : x.Entry.Kind == "DLC" ? 1 : 2).ThenBy(x => x.Entry.ComponentId, StringComparer.Ordinal).ToArray();
        var app = ordered[0].Entry.ApplicationId; var game = titles.Single(g => g.TitleId.Equals(app, StringComparison.OrdinalIgnoreCase)); var before = game.Components;
        var projected = titles.ToArray(); long bytes = 0;
        foreach (var item in ordered)
        {
            var plan = ManagedUpdates.Prepare(device, item.Entry, item.Package, item.Path, storage, volume, projected); bytes = checked(bytes + plan.RequiredBytes);
            game = game with { Components = game.Components.Where(c => !c.TitleId.Equals(item.Entry.ComponentId, StringComparison.OrdinalIgnoreCase)).Append(item.Package.Components[0] with { Storage = storage }).ToArray() };
            projected = projected.Select(g => g.TitleId.Equals(app, StringComparison.OrdinalIgnoreCase) ? game : g).ToArray();
        }
        if (bytes > volume.FreeBytes) throw new IOException("批量计划合计空间不足");
        return new(Guid.NewGuid().ToString("N"), device.DeviceId, device.Environment, app, storage, bytes, before, ordered);
    }
    public static async Task<ManagedBatchResult[]> ExecuteAsync(DeviceClient client, ManagedBatchPlan batch, SnapshotStore snapshots, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var completed = new List<ManagedBatchResult>(); var expected = batch.Before;
        try
        {
            foreach (var item in batch.Items)
            {
                ct.ThrowIfCancellationRequested(); var device = await client.QueryAsync<DeviceInfo>("device.info", ct: ct);
                if (device.DeviceId != batch.DeviceId || device.Environment != batch.Environment) throw new IOException("批量计划的主机环境已变化");
                var titles = await client.QueryAsync<GameTitle[]>("titles.list", ct: ct); var game = titles.Single(g => g.TitleId.Equals(batch.ApplicationId, StringComparison.OrdinalIgnoreCase));
                if (!Fingerprints.Equal(ManagedUpdates.InventoryFingerprint(expected), ManagedUpdates.InventoryFingerprint(game.Components))) throw new IOException("批量安装之间发生了未预期的内容变化，请重新预检");
                var volume = (await client.QueryAsync<StorageInfo[]>("storages.list", ct: ct)).Single(v => v.Id == batch.Storage);
                var plan = ManagedUpdates.Prepare(device, item.Entry, item.Package, item.Path, batch.Storage, volume, titles) with { OperationId = item.OperationId };
                progress?.Report(new(0, 0, $"更新计划 {completed.Count + 1}/{batch.Items.Length}"));
                var result = await ManagedUpdates.ExecuteAsync(client, plan, snapshots, progress, ct); completed.Add(new(plan.OperationId, result));
                // Reconstruct expected state from the previously observed inventory, allowing only this selected component to change.
                var after = (await client.QueryAsync<GameTitle[]>("titles.list", ct: ct)).Single(g => g.TitleId.Equals(batch.ApplicationId, StringComparison.OrdinalIgnoreCase)).Components;
                var unchangedBefore = expected.Where(c => !c.TitleId.Equals(item.Entry.ComponentId, StringComparison.OrdinalIgnoreCase));
                var unchangedAfter = after.Where(c => !c.TitleId.Equals(item.Entry.ComponentId, StringComparison.OrdinalIgnoreCase));
                if (!Fingerprints.Equal(ManagedUpdates.InventoryFingerprint(unchangedBefore), ManagedUpdates.InventoryFingerprint(unchangedAfter))) throw new IOException("安装后发现其他组件变化，后续项目已停止");
                expected = after;
            }
            return completed.ToArray();
        }
        catch (OperationCanceledException ex) { throw new ManagedBatchCanceledException($"更新已取消，{completed.Count} 项已完成结果保留", completed, ex, ct); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        { throw new ManagedBatchException($"批量更新已停止，{completed.Count} 项结果保留；请复核后重试或跳过", completed, ex); }
    }
}
public static class ContentIndexBuilder
{
    public static async Task<ContentIndex> BuildAsync(string directory, Func<string, CancellationToken, Task<PackageInfo>> inspect, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory); LocalSafety.DemandDirectory(root); var entries = new List<ContentEntry>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (Path.GetExtension(path).ToLowerInvariant() is not (".nsp" or ".xci" or ".nsz" or ".xcz")) continue;
            ct.ThrowIfCancellationRequested(); LocalSafety.DemandRegularFile(path);
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize, true);
            var package = await inspect(path, ct);
            if (!package.Recognized || package.Components.Length != 1 || !ContentCatalog.ValidId(package.Components[0].ApplicationId ?? package.TitleId)) throw new InvalidDataException("索引生成拒绝未识别或混合包，请先拆分组件");
            var component = package.Components[0]; var key = component.TitleId + ":" + component.Version;
            if (!seen.Add(key)) throw new InvalidDataException("目录包含相同组件版本的多个文件，请先整理");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
            entries.Add(new(component.ApplicationId ?? package.TitleId!, component.TitleId, component.Kind, component.Version, source.Length, hash, Path.GetRelativePath(root, path).Replace('\\', '/')));
        }
        var index = new ContentIndex(1, entries.ToArray()); ContentSources.Parse(JsonSerializer.SerializeToUtf8Bytes(index, Wire.Json), new("builder", "builder", ContentSourceKind.Local, root)); return index;
    }
}
