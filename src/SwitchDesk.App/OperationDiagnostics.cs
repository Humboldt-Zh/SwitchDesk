using System.IO;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public partial class MainWindow
{
    private readonly Dictionary<string, Job> pendingOperations = new(StringComparer.Ordinal);
    private void LoadPendingDiagnostics()
    {
        foreach (var record in phase2.List<JsonElement>("install-batches"))
        {
            if (!record.TryGetProperty("state", out var status) || status.GetString() is not ("running" or "needs-inspection")) continue;
            var plan = record.GetProperty("plan").Deserialize<ManagedBatchPlan>(Wire.Json) ?? throw new InvalidDataException("上次更新清单损坏，保留日志后检查");
            AddPending("batch:" + plan.Id, $"上次更新 · {plan.Items.Length} 项", "需要核对主机实际版本；不会自动重新安装");
        }
        foreach (var operation in phase2.List<MigrationOperation>("migrations").Where(m => m.State == MigrationState.NeedsRecovery))
            AddPending("migration:" + operation.Id, "上次存档迁移", operation.Error ?? "上次任务中断或目标已变化，保护副本保留；请核对后手动处理");
        void AddPending(string key, string title, string reason)
        {
            if (pendingOperations.ContainsKey(key)) return;
            var job = new Job { Name = title, State = "需要处理", CanCancel = false, Detail = reason }; pendingOperations[key] = job; vm.Tasks.Add(job);
        }
    }
    private async Task InspectPendingOperationsAsync()
    {
        await RunJob("核对上次操作 · 只读", async (_, ct) =>
        {
            var workspace = new MigrationWorkspace(phase2, Path.Combine(settingsStore.Root, "Phase2"), CompatibilityCatalog.Released);
            foreach (var operation in phase2.List<MigrationOperation>("migrations").Where(m => m.State == MigrationState.NeedsRecovery))
            {
                MigrationState outcome;
                if (operation.Source == SavePlatform.Switch) outcome = await workspace.ReconcilePcAsync(operation.Id, ct);
                else
                {
                    if (!vm.Online || device?.Capabilities.Contains("operations.status") != true) continue;
                    var pair = phase2.Get<SavePair>("pairs", operation.PairId)?.Value;
                    if (pair == null || pair.DeviceId != device.DeviceId || pair.Environment != device.Environment) continue;
                    var (peer, _) = RequireDevice("operations.status"); outcome = await workspace.ReconcileSwitchAsync(operation.Id, peer, new(settings.BackupDirectory), ct);
                }
                if (outcome is MigrationState.Complete or MigrationState.Abandoned) await Dispatcher.InvokeAsync(() => ResolvePending("migration:" + operation.Id, outcome == MigrationState.Complete ? "已核对迁移结果" : "目标未改变 · 保护副本保留"));
            }
            if (vm.Online && device?.Capabilities.Contains("operations.status") == true)
            {
                var (peer, info) = RequireDevice("operations.status");
                foreach (var record in phase2.List<JsonElement>("install-batches"))
                {
                    if (!record.TryGetProperty("state", out var status) || status.GetString() is not ("running" or "needs-inspection")) continue;
                    var plan = record.GetProperty("plan").Deserialize<ManagedBatchPlan>(Wire.Json)!;
                    if (plan.DeviceId != info.DeviceId || plan.Environment != info.Environment) continue;
                    var inspection = await OperationReconciliation.InspectAsync(peer, plan, ct);
                    phase2.Put("install-batches", plan.Id, new { plan, state = inspection.State, inspection, time = DateTimeOffset.UtcNow });
                    if (inspection.State is "verified" or "stopped") await Dispatcher.InvokeAsync(() => ResolvePending("batch:" + plan.Id, inspection.State == "verified" ? "实际版本已核对" : "未执行的项目保留，重新预检后可继续"));
                }
            }
            return true;
        });
        LoadPendingDiagnostics(); vm.Notify("只读核对完成 · 未自动重试任何写入");
    }
    private void ResolvePending(string key, string result)
    {
        if (pendingOperations.TryGetValue(key, out var job)) { job.Result = result; job.State = "完成"; job.Detail = result; job.Indeterminate = false; job.Percent = 100; }
    }
}
