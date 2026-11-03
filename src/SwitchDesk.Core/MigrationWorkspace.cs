using System.Text.Json;

namespace SwitchDesk.Core;

public enum MigrationState { Preparing, Prepared, Writing, Complete, NeedsRecovery, Abandoned }
public sealed record MigrationOperation(string Id, string PairId, string AdapterId, SavePlatform Source, MigrationState State,
    string SourceFingerprint, string TargetFingerprint, string OutputFingerprint, string RecoveryDirectory,
    string TargetPath, string[] Effects, MigrationFidelity Fidelity, DateTimeOffset CreatedAt, string? Error = null)
{
    public SaveReading? SourceReading { get; init; }
    public SaveReading? TargetReading { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? AttentionReason { get; init; }
}

/// <summary>Produces immutable local before-images and an output before confirmation. No implicit copy on connection.</summary>
public sealed class MigrationWorkspace(Phase2State state, string root, CompatibilityCatalog compatibility)
{
    private static async Task<byte[]> ReadImageAsync(string directory, string name, string expected, CancellationToken ct)
    {
        var file = await StableSaveFile.ReadAsync(Path.Combine(directory, name), ct);
        if (!Fingerprints.Equal(file.Fingerprint, expected)) throw new InvalidDataException("迁移保护数据校验失败：" + name);
        return file.Bytes;
    }
    public async Task<MigrationOperation> PrepareAsync(SavePair pair, ISaveAdapter adapter, SavePlatform source,
        ReadOnlyMemory<byte> pc, ReadOnlyMemory<byte> nx, string[] pcDlc, string[] nxDlc, CancellationToken ct, GameVersionContext? versions = null, SaveReading? pcReading = null, SaveReading? nxReading = null)
    {
        if (pair.AdapterId != adapter.Id || pair.PcSlot.Length == 0 || pair.SwitchSlot.Length == 0) throw new InvalidDataException("配对游戏或槽位无效");
        if ((pcReading != null && !Fingerprints.Equal(pcReading.Fingerprint, Fingerprints.Of(pc.Span))) || (nxReading != null && !Fingerprints.Equal(nxReading.Fingerprint, Fingerprints.Of(nx.Span)))) throw new InvalidDataException("存档时间记录与内容指纹不对应");
        var paired = state.Get<SavePair>("pairs", pair.Id);
        if (paired != null && paired.Value.Value != pair) throw new InvalidOperationException("配对或基线已变化，请重新比较");
        if (paired == null) state.Put("pairs", pair.Id, pair, 0);
        var pcInfo = adapter.Inspect(pc.Span, SavePlatform.Steam); var nxInfo = adapter.Inspect(nx.Span, SavePlatform.Switch);
        var rule = compatibility.Find(adapter.Id, pcInfo, nxInfo, pcDlc, nxDlc, versions) ?? throw new NotSupportedException("当前游戏、版本或 DLC 组合尚未准入；可以分别备份，不能迁移");
        if (pcInfo.Fidelity == MigrationFidelity.Unsupported || nxInfo.Fidelity == MigrationFidelity.Unsupported) throw new NotSupportedException("当前适配器只支持检查与备份");
        var input = source == SavePlatform.Steam ? pc : nx; var target = source == SavePlatform.Steam ? nx : pc;
        var converted = adapter.Convert(input.Span, source, target.Span);
        if (converted.Fidelity == MigrationFidelity.Unsupported || converted.Fidelity != rule.Fidelity) throw new InvalidDataException("转换结果完整度与准入规则不同");
        adapter.Inspect(converted.Bytes, source == SavePlatform.Steam ? SavePlatform.Switch : SavePlatform.Steam);
        var id = Guid.NewGuid().ToString("N"); var directory = Path.Combine(root, "Recovery", id); Directory.CreateDirectory(directory);
        var operation = new MigrationOperation(id, pair.Id, adapter.Id, source, MigrationState.Preparing, Fingerprints.Of(input.Span), Fingerprints.Of(target.Span),
            Fingerprints.Of(converted.Bytes), directory, source == SavePlatform.Switch ? Path.GetFullPath(pair.PcPath) : pair.SwitchPath,
            converted.Effects, converted.Fidelity, DateTimeOffset.UtcNow) { SourceReading = source == SavePlatform.Steam ? pcReading : nxReading, TargetReading = source == SavePlatform.Steam ? nxReading : pcReading };
        var revision = state.Put("migrations", id, operation, 0);
        try
        {
            await DurableFile.WriteNewAsync(Path.Combine(directory, "source.bin"), input, ct);
            await DurableFile.WriteNewAsync(Path.Combine(directory, "target.bin"), target, ct);
            await DurableFile.WriteNewAsync(Path.Combine(directory, "output.bin"), converted.Bytes, ct);
            await DurableFile.WriteNewAsync(Path.Combine(directory, "operation.json"), JsonSerializer.SerializeToUtf8Bytes(operation, Wire.Json), ct);
            operation = operation with { State = MigrationState.Prepared }; state.Put("migrations", id, operation, revision); return operation;
        }
        catch (Exception ex) { state.Put("migrations", id, operation with { State = MigrationState.Abandoned, Error = ex.Message }, revision); throw; }
    }
    public async Task CommitPcAsync(string operationId, Func<Task> demandClosed, CancellationToken ct, Func<Task>? validateSource = null)
    {
        var saved = state.Get<MigrationOperation>("migrations", operationId) ?? throw new InvalidDataException("迁移操作不存在");
        var operation = saved.Value; var revision = saved.Revision;
        if (operation.State != MigrationState.Prepared || operation.Source != SavePlatform.Switch) throw new InvalidOperationException("此迁移不能再次写入电脑");
        await demandClosed(); ct.ThrowIfCancellationRequested();
        if (validateSource != null) await validateSource();
        var current = await StableSaveFile.ReadAsync(operation.TargetPath, ct);
        if (!Fingerprints.Equal(current.Fingerprint, operation.TargetFingerprint)) throw new IOException("目标存档已变化，请重新比较；保护快照仍保留");
        var output = await ReadImageAsync(operation.RecoveryDirectory, "output.bin", operation.OutputFingerprint, ct);
        await ReadImageAsync(operation.RecoveryDirectory, "target.bin", operation.TargetFingerprint, ct);
        var temporary = operation.TargetPath + ".switchdesk-" + operation.Id + ".tmp";
        await DurableFile.WriteNewAsync(temporary, output, ct);
        try
        {
            await demandClosed(); if (validateSource != null) await validateSource(); var latest = await StableSaveFile.ReadAsync(operation.TargetPath, ct);
            if (!Fingerprints.Equal(latest.Fingerprint, operation.TargetFingerprint)) throw new IOException("确认后目标存档又发生变化，请重新比较");
            ct.ThrowIfCancellationRequested();
            revision = state.Put("migrations", operation.Id, operation with { State = MigrationState.Writing }, revision);
            // One selected slot, same-volume atomic replacement. Never delete the destination as fallback.
            File.Replace(temporary, operation.TargetPath, null);
            var readback = await StableSaveFile.ReadAsync(operation.TargetPath, CancellationToken.None);
            if (!Fingerprints.Equal(readback.Fingerprint, operation.OutputFingerprint)) throw new IOException("写回校验失败，可能受到云存档或其他程序覆盖");
            var complete = operation with { State = MigrationState.Complete, CompletedAt = DateTimeOffset.UtcNow };
            state.Put("migrations", operation.Id, complete, revision); RecordBaseline(complete);
        }
        catch (Exception ex)
        {
            var latest = state.Get<MigrationOperation>("migrations", operation.Id);
            if (latest is {} entry && entry.Value.State == MigrationState.Writing)
                state.Put("migrations", operation.Id, operation with { State = MigrationState.NeedsRecovery, Error = ex.Message }, entry.Revision);
            ex.Data["SwitchDesk.RecoveryPath"] = Path.Combine(operation.RecoveryDirectory, "target.bin"); throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task CommitSwitchAsync(string operationId, SavePair pair, DeviceClient client, DeviceInfo device, SaveInfo save,
        string completeTargetSnapshot, string expectedWholeSaveFingerprint, SnapshotStore snapshots, Func<Task> demandSourceClosed, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var saved = state.Get<MigrationOperation>("migrations", operationId) ?? throw new InvalidDataException("迁移操作不存在"); var operation = saved.Value; var revision = saved.Revision;
        if (operation.State != MigrationState.Prepared || operation.Source != SavePlatform.Steam || operation.PairId != pair.Id || operation.TargetPath != pair.SwitchPath) throw new InvalidOperationException("此迁移不能再次写入 Switch");
        if (!device.Capabilities.Contains("restore.atomic.v1") || !device.Capabilities.Contains("saves.inspect")) throw new DeviceException("CapabilityMissing", "Switch 写入需要新版事务恢复能力");
        if (pair.DeviceId != device.DeviceId || pair.Environment != device.Environment || pair.TitleId != save.TitleId || pair.UserId != save.UserId || pair.SaveKey != save.Key) throw new IOException("迁移目标的主机、环境、用户或存档记录已变化");
        var target = await SnapshotStore.VerifyAsync(completeTargetSnapshot, ct);
        if (target.Manifest.DeviceId != pair.DeviceId || target.Manifest.Environment != pair.Environment || target.Manifest.TitleId != pair.TitleId || target.Manifest.UserId != pair.UserId || target.Manifest.SaveType != save.Type) throw new InvalidDataException("目标快照身份不匹配，不能重写身份后恢复");
        var slot = target.Manifest.Files.SingleOrDefault(f => f.Path == pair.SwitchPath);
        if (slot == null || !Fingerprints.Equal(slot.Sha256, operation.TargetFingerprint)) throw new IOException("目标槽位模板与比较结果不同");
        await demandSourceClosed(); var pc = await StableSaveFile.ReadAsync(pair.PcPath, ct);
        if (!Fingerprints.Equal(pc.Fingerprint, operation.SourceFingerprint)) throw new IOException("电脑来源存档已变化，请重新比较");
        var output = await ReadImageAsync(operation.RecoveryDirectory, "output.bin", operation.OutputFingerprint, ct);
        await ReadImageAsync(operation.RecoveryDirectory, "target.bin", operation.TargetFingerprint, ct);
        var convertedPath = Path.Combine(operation.RecoveryDirectory, "converted-target.zip");
        var converted = await SnapshotStore.CreateConvertedTargetAsync(completeTargetSnapshot, pair.SwitchPath, output, convertedPath, ct);
        try
        {
            await demandSourceClosed(); var freshPc = await StableSaveFile.ReadAsync(pair.PcPath, ct);
            if (!Fingerprints.Equal(freshPc.Fingerprint, operation.SourceFingerprint)) throw new IOException("确认后电脑来源存档已变化");
            ct.ThrowIfCancellationRequested(); revision = state.Put("migrations", operation.Id, operation with { State = MigrationState.Writing }, revision);
            await snapshots.RestoreAsync(client, device, save, converted.Path, progress, ct, expectedWholeSaveFingerprint, operation.Id);
            var readback = await snapshots.BackupAsync(client, device, save, progress, CancellationToken.None);
            if (!Fingerprints.Equal(readback.Manifest.ContentHash, converted.Manifest.ContentHash)) throw new IOException("Switch 写回后的完整快照校验不同，请保留 Recovery 并处理");
            var complete = operation with { State = MigrationState.Complete, CompletedAt = DateTimeOffset.UtcNow };
            state.Put("migrations", operation.Id, complete, revision); RecordBaseline(complete);
        }
        catch (Exception ex)
        {
            var current = state.Get<MigrationOperation>("migrations", operation.Id)!.Value;
            if (current.Value.State == MigrationState.Writing) state.Put("migrations", operation.Id, operation with { State = MigrationState.NeedsRecovery, Error = ex.Message }, current.Revision);
            ex.Data["SwitchDesk.RecoveryPath"] = completeTargetSnapshot; throw;
        }
    }
    private void RecordBaseline(MigrationOperation operation)
    {
        var entry = state.Get<SavePair>("pairs", operation.PairId) ?? throw new InvalidDataException("配对记录缺失，迁移结果已保留");
        var time = operation.CompletedAt ?? DateTimeOffset.UtcNow;
        var baseline = operation.Source == SavePlatform.Steam ? new SaveBaseline(operation.SourceFingerprint, operation.OutputFingerprint, time) : new SaveBaseline(operation.OutputFingerprint, operation.SourceFingerprint, time);
        state.Put("pairs", operation.PairId, entry.Value with { Baseline = baseline }, entry.Revision);
    }
    public async Task<bool> VerifyPcAfterCloudAsync(string operationId, CancellationToken ct)
    {
        var entry = state.Get<MigrationOperation>("migrations", operationId) ?? throw new InvalidDataException("迁移记录不存在");
        if (entry.Value.Source != SavePlatform.Switch || entry.Value.State != MigrationState.Complete) return false;
        var current = await StableSaveFile.ReadAsync(entry.Value.TargetPath, ct);
        if (Fingerprints.Equal(current.Fingerprint, entry.Value.OutputFingerprint)) return true;
        state.Put("migrations", operationId, entry.Value with { State = MigrationState.NeedsRecovery, Error = "迁移后电脑文件已变化，请确认是继续游玩的进度还是 Steam Cloud 替换；不会自动覆盖" }, entry.Revision); return false;
    }
    public async Task<MigrationState> ReconcilePcAsync(string operationId, CancellationToken ct)
    {
        var entry = state.Get<MigrationOperation>("migrations", operationId) ?? throw new InvalidDataException("迁移记录不存在"); var operation = entry.Value;
        if (operation.Source != SavePlatform.Switch || operation.State != MigrationState.NeedsRecovery) return operation.State;
        SteamSaves.DemandClosed(operation.AdapterId, false);
        foreach (var (file, fingerprint) in new[] { ("source.bin", operation.SourceFingerprint), ("target.bin", operation.TargetFingerprint), ("output.bin", operation.OutputFingerprint) })
            await ReadImageAsync(operation.RecoveryDirectory, file, fingerprint, ct);
        var current = await StableSaveFile.ReadAsync(operation.TargetPath, ct);
        if (Fingerprints.Equal(current.Fingerprint, operation.OutputFingerprint))
        {
            var complete = operation with { State = MigrationState.Complete, CompletedAt = operation.CompletedAt ?? DateTimeOffset.UtcNow, Error = null };
            state.Put("migrations", operationId, complete, entry.Revision); RecordBaseline(complete); return complete.State;
        }
        if (Fingerprints.Equal(current.Fingerprint, operation.TargetFingerprint))
        { state.Put("migrations", operationId, operation with { State = MigrationState.Abandoned, Error = "目标仍是覆盖前版本，未发现迁移结果；保护副本保留" }, entry.Revision); return MigrationState.Abandoned; }
        return MigrationState.NeedsRecovery;
    }
    public async Task<MigrationState> ReconcileSwitchAsync(string operationId, DeviceClient client, SnapshotStore snapshots, CancellationToken ct)
    {
        var entry = state.Get<MigrationOperation>("migrations", operationId) ?? throw new InvalidDataException("迁移记录不存在"); var operation = entry.Value;
        if (operation.Source != SavePlatform.Steam || operation.State != MigrationState.NeedsRecovery) return operation.State;
        var pair = state.Get<SavePair>("pairs", operation.PairId)?.Value ?? throw new InvalidDataException("配对记录缺失");
        var device = await client.QueryAsync<DeviceInfo>("device.info", ct: ct);
        if (device.DeviceId != pair.DeviceId || device.Environment != pair.Environment || !device.Capabilities.Contains("operations.status")) throw new IOException("请连接原主机与原系统环境，并使用支持操作状态的配套程序");
        var status = await client.QueryAsync<JsonElement>("operations.status", new { operationId }, ct);
        if (!status.TryGetProperty("state", out var native) || native.GetString() != "complete") return MigrationState.NeedsRecovery;
        var save = (await client.QueryAsync<SaveInfo[]>("saves.list", ct: ct)).SingleOrDefault(s => s.Key == pair.SaveKey && s.TitleId == pair.TitleId && s.UserId == pair.UserId && s.Environment == pair.Environment) ?? throw new IOException("原存档记录已变化，不能自动核对为完成");
        var converted = await SnapshotStore.VerifyAsync(Path.Combine(operation.RecoveryDirectory, "converted-target.zip"), ct);
        var actual = await snapshots.BackupAsync(client, device, save, null, ct);
        if (!SnapshotStore.SameTarget(actual.Manifest, converted.Manifest) || !Fingerprints.Equal(actual.Manifest.ContentHash, converted.Manifest.ContentHash)) return MigrationState.NeedsRecovery;
        var complete = operation with { State = MigrationState.Complete, CompletedAt = operation.CompletedAt ?? DateTimeOffset.UtcNow, Error = null };
        state.Put("migrations", operationId, complete, entry.Revision); RecordBaseline(complete); return MigrationState.Complete;
    }
    public void RecoverInterruptedOperations()
    {
        foreach (var operation in state.List<MigrationOperation>("migrations").Where(x => x.State is MigrationState.Preparing or MigrationState.Writing))
        {
            var current = state.Get<MigrationOperation>("migrations", operation.Id)!.Value;
            state.Put("migrations", operation.Id, operation with { State = MigrationState.NeedsRecovery, Error = "上次任务被中断，请检查保护快照与目标实际内容后处理" }, current.Revision);
        }
    }
}
public static class DurableFile
{
    public static async Task WriteNewAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(true);
    }
}
