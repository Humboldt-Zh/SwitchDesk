using System.IO;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public partial class MainWindow
{
    private volatile bool steamWatchesDirty = true;
    private sealed record PcWatch(MigrationOperation Operation, DateTime Modified, long Length, string Fingerprint);
    private async Task ObserveSteamChangesAsync()
    {
        var guard = new SteamCloudGuard(); var watches = new Dictionary<string, PcWatch>(StringComparer.Ordinal);
        var errors = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (watches.Count == 0 && !steamWatchesDirty) continue;
                var activeGames = SaveAdapters.All.ToDictionary(a => a.Id, a => SteamSaves.GameIsRunning(a.Id)); guard.Observe(SteamSaves.SteamIsRunning(), activeGames);
                if (Volatile.Read(ref activeJobs) != 0 || !await jobsGate.WaitAsync(0, lifetime.Token)) continue;
                Interlocked.Increment(ref activeJobs);
                try
                {
                    if (steamWatchesDirty)
                    {
                        var imports = phase2.List<MigrationOperation>("migrations").GroupBy(o => o.PairId).Select(g => g.OrderByDescending(o => o.CompletedAt ?? o.CreatedAt).First()).Where(o => o.State == MigrationState.Complete && o.Source == SavePlatform.Switch).ToArray();
                        var ids = imports.Select(o => o.Id).ToHashSet(StringComparer.Ordinal); foreach (var old in watches.Keys.Where(k => !ids.Contains(k)).ToArray()) watches.Remove(old);
                        foreach (var operation in imports) if (!watches.ContainsKey(operation.Id)) watches[operation.Id] = new(operation, DateTime.MinValue, -1, operation.OutputFingerprint);
                        steamWatchesDirty = false;
                    }
                    foreach (var (id, watch) in watches.ToArray())
                    {
                        if (activeGames.GetValueOrDefault(watch.Operation.AdapterId)) continue;
                        var info = new FileInfo(watch.Operation.TargetPath); if (!info.Exists || info.Length == watch.Length && info.LastWriteTimeUtc == watch.Modified) continue;
                        try
                        {
                            var current = await StableSaveFile.ReadAsync(info.FullName, lifetime.Token);
                            if (guard.NeedsReview(watch.Operation.AdapterId, false, watch.Fingerprint, current.Fingerprint))
                            {
                                var directory = Path.Combine(watch.Operation.RecoveryDirectory, "CloudObserved", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                                await DurableFile.WriteNewAsync(Path.Combine(directory, "observed.bin"), current.Bytes, lifetime.Token);
                                await DurableFile.WriteNewAsync(Path.Combine(directory, "observation.json"), JsonSerializer.SerializeToUtf8Bytes(new { current.Fingerprint, current.ModifiedAt, observedAt = DateTimeOffset.UtcNow, reason = "SteamSessionChange" }, Wire.Json), lifetime.Token);
                                var record = phase2.Get<MigrationOperation>("migrations", id)!.Value;
                                if (record.Value.State != MigrationState.Complete) { steamWatchesDirty = true; continue; }
                                phase2.Put("migrations", id, record.Value with { State = MigrationState.NeedsRecovery, AttentionReason = "SteamSessionChange", Error = "Steam 重开后、未观察到游戏会话时电脑存档发生变化；可能是云端替换。已保留新内容和两端保护副本，请比较后手动选择" }, record.Revision);
                                steamWatchesDirty = true; await Dispatcher.InvokeAsync(() => { vm.CrossSave.Invalidate(); LoadPendingDiagnostics(); });
                            }
                            watches[id] = new(watch.Operation, current.ModifiedAt.UtcDateTime, current.Bytes.LongLength, current.Fingerprint);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                        { steamWatchesDirty = true; if (!errors.TryGetValue(id, out var last) || DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(30)) { errors[id] = DateTimeOffset.UtcNow; Log("Steam 存档观察暂不可用：" + ex.Message); } }
                    }
                }
                finally { Interlocked.Decrement(ref activeJobs); jobsGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
    }
    private void CompleteReviewedCloudChanges(string pairId, string currentOperationId)
    {
        foreach (var old in phase2.List<MigrationOperation>("migrations").Where(o => o.PairId == pairId && o.Id != currentOperationId && o.State == MigrationState.NeedsRecovery && o.AttentionReason == "SteamSessionChange"))
        {
            var entry = phase2.Get<MigrationOperation>("migrations", old.Id)!.Value;
            phase2.Put("migrations", old.Id, old with { State = MigrationState.Abandoned, AttentionReason = "ReviewedByManualMigration", Error = "用户已通过后续手动迁移处理；原始保护副本继续保留" }, entry.Revision);
            ResolvePending("migration:" + old.Id, "已通过后续手动迁移处理 · 副本保留");
        }
        steamWatchesDirty = true;
    }
}
