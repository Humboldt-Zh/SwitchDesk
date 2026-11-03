namespace SwitchDesk.Core;

public sealed record VisibilityAdmission(string Firmware, string Environment, bool Home, bool AllSoftware, bool Groups,
    int HideRestoreRounds, int RestartChecks, bool ContentAndSavesUnchanged, bool OfflineRecovery, string Evidence)
{
    public string DeviceId { get; init; } = "";
    public bool Allowed => Home && AllSoftware && Groups && HideRestoreRounds >= 10 && RestartChecks >= 3 && ContentAndSavesUnchanged && OfflineRecovery && !string.IsNullOrWhiteSpace(Evidence);
}
public sealed record TitleVisibility(string TitleId, bool Visible, bool SupportsApplicationMode, bool ProtectedEntry);
public sealed record VisibilityChange(string TitleId, bool Before, bool After);
public sealed record VisibilityPlan(string OperationId, string DeviceId, string Environment, VisibilityChange[] Changes);
public interface ITitleVisibility
{
    Task<TitleVisibility[]> ListAsync(CancellationToken ct);
    // Must persist on SD before the first native display-state mutation.
    Task WriteRecoveryJournalAsync(VisibilityPlan plan, CancellationToken ct);
    Task SetVisibleAsync(string titleId, bool visible, CancellationToken ct);
}
public static class VisibilityPlans
{
    public static VisibilityPlan Prepare(DeviceInfo device, TitleVisibility[] current, IEnumerable<string> keepVisible, VisibilityAdmission? admission)
    {
        if (admission?.Allowed != true || admission.DeviceId != device.DeviceId || admission.Firmware != device.Firmware || admission.Environment != device.Environment)
            throw new DeviceException("VisibilityNotAdmitted", "原生 HOME 隐藏／恢复尚未通过当前环境准入，保留原生显示");
        if (!device.Capabilities.Contains("titles.visibility.v1")) throw new DeviceException("CapabilityMissing", "当前配套程序未提供已验证的原生显示控制");
        if (current.Select(x => x.TitleId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Length || current.Any(x => !ContentCatalog.ValidId(x.TitleId))) throw new InvalidDataException("主机显示状态清单无效");
        var selected = keepVisible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Any(x => !current.Any(c => c.TitleId.Equals(x, StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("精选方案包含已失效的游戏");
        foreach (var entry in current.Where(x => x.ProtectedEntry)) selected.Add(entry.TitleId);
        if (!current.Any(x => selected.Contains(x.TitleId) && x.SupportsApplicationMode)) throw new InvalidOperationException("至少保留一个完整应用模式恢复入口");
        var changes = current.Where(x => x.Visible != selected.Contains(x.TitleId)).Select(x => new VisibilityChange(x.TitleId, x.Visible, selected.Contains(x.TitleId))).ToArray();
        return new(Guid.NewGuid().ToString("N"), device.DeviceId, device.Environment, changes);
    }
    public static async Task ApplyAsync(VisibilityPlan plan, ITitleVisibility backend, Phase2State state, DeviceInfo device, VisibilityAdmission? admission, CancellationToken ct)
    {
        if (plan.DeviceId != device.DeviceId || plan.Environment != device.Environment || admission?.Allowed != true || admission.DeviceId != device.DeviceId || admission.Firmware != device.Firmware || admission.Environment != device.Environment || !device.Capabilities.Contains("titles.visibility.v1"))
            throw new DeviceException("VisibilityNotAdmitted", "原生显示控制未通过当前环境准入");
        var current = await backend.ListAsync(ct);
        if (plan.Changes.Select(x => x.TitleId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Changes.Length ||
            plan.Changes.Any(x => !x.After && current.Any(t => t.TitleId == x.TitleId && t.ProtectedEntry))) throw new InvalidDataException("精选方案包含重复项目或受保护入口");
        if (!current.Any(t => t.SupportsApplicationMode && (plan.Changes.SingleOrDefault(c => c.TitleId == t.TitleId)?.After ?? t.Visible))) throw new InvalidDataException("精选方案没有保留完整应用模式恢复入口");
        foreach (var change in plan.Changes)
            if (current.SingleOrDefault(x => x.TitleId == change.TitleId)?.Visible != change.Before) throw new IOException("显示状态已变化，请重新预览精选方案");
        var revision = state.Put("visibility-journals", plan.OperationId, new VisibilityJournal(plan, "prepared", []), 0);
        await backend.WriteRecoveryJournalAsync(plan, ct);
        var applied = new List<string>();
        try
        {
            foreach (var change in plan.Changes)
            {
                ct.ThrowIfCancellationRequested(); await backend.SetVisibleAsync(change.TitleId, change.After, ct); applied.Add(change.TitleId);
                revision = state.Put("visibility-journals", plan.OperationId, new VisibilityJournal(plan, "applying", applied.ToArray()), revision);
            }
            var after = await backend.ListAsync(ct);
            if (plan.Changes.Any(c => after.SingleOrDefault(x => x.TitleId == c.TitleId)?.Visible != c.After)) throw new IOException("主机显示状态读回不同，请恢复本次设置");
            state.Put("visibility-journals", plan.OperationId, new VisibilityJournal(plan, "complete", applied.ToArray()), revision);
        }
        catch
        {
            // The SD journal covers even an IPC that succeeded before the connection was lost.
            var status = "needs-recovery";
            try
            {
                using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var actual = await backend.ListAsync(recoveryTimeout.Token);
                var recovery = RecoveryPlan(new(plan, "needs-recovery", applied.ToArray()), device, actual);
                await backend.WriteRecoveryJournalAsync(recovery, recoveryTimeout.Token);
                foreach (var change in recovery.Changes) await backend.SetVisibleAsync(change.TitleId, change.After, recoveryTimeout.Token);
                var restored = await backend.ListAsync(recoveryTimeout.Token);
                if (recovery.Changes.All(c => restored.SingleOrDefault(t => t.TitleId == c.TitleId)?.Visible == c.After)) status = "rolled-back";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { }
            state.Put("visibility-journals", plan.OperationId, new VisibilityJournal(plan, status, applied.ToArray()), revision); throw;
        }
    }
    public static VisibilityPlan Undo(VisibilityJournal journal, DeviceInfo current)
    {
        if (journal.Plan.DeviceId != current.DeviceId || journal.Plan.Environment != current.Environment) throw new IOException("精选日志属于其他主机或系统环境");
        return new(Guid.NewGuid().ToString("N"), current.DeviceId, current.Environment, journal.Plan.Changes.Select(c => new VisibilityChange(c.TitleId, c.After, c.Before)).ToArray());
    }
    public static VisibilityPlan RecoveryPlan(VisibilityJournal journal, DeviceInfo device, TitleVisibility[] current, bool onlyToolHidden = false)
    {
        if (journal.Plan.DeviceId != device.DeviceId || journal.Plan.Environment != device.Environment) throw new IOException("精选恢复日志属于其他环境");
        var changes = journal.Plan.Changes.Where(c => !onlyToolHidden || c.Before && !c.After).Select(c =>
        {
            var actual = current.SingleOrDefault(t => t.TitleId == c.TitleId) ?? throw new IOException("恢复清单中的应用已不在当前主机记录中，请先核对");
            if (actual.ProtectedEntry && !c.Before) throw new IOException("请先更换完整应用模式入口，不能隐藏当前恢复入口");
            return new VisibilityChange(c.TitleId, actual.Visible, c.Before);
        }).Where(c => c.Before != c.After).ToArray();
        if (!current.Any(t => t.SupportsApplicationMode && (changes.SingleOrDefault(c => c.TitleId == t.TitleId)?.After ?? t.Visible))) throw new IOException("恢复后没有完整应用模式入口");
        return new(Guid.NewGuid().ToString("N"), device.DeviceId, device.Environment, changes);
    }
}
public sealed record VisibilityJournal(VisibilityPlan Plan, string State, string[] Applied);
