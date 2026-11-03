using System.Text.Json;

namespace SwitchDesk.Core;

public sealed record InstallItemInspection(string OperationId, string ComponentId, string NativeState, uint? ActualVersion, string State);
public sealed record InstallBatchInspection(string BatchId, string State, InstallItemInspection[] Items, DateTimeOffset ObservedAt);
public static class OperationReconciliation
{
    /// <summary>Read-only after reconnect. Never retries a write merely because a response was lost.</summary>
    public static async Task<InstallBatchInspection> InspectAsync(DeviceClient client, ManagedBatchPlan plan, CancellationToken ct)
    {
        var device = await client.QueryAsync<DeviceInfo>("device.info", ct: ct);
        if (device.DeviceId != plan.DeviceId || device.Environment != plan.Environment) throw new IOException("上次安装计划属于其他主机或系统环境");
        if (!device.Capabilities.Contains("operations.status")) throw new DeviceException("CapabilityMissing", "当前配套程序无法查询持久操作状态");
        var game = (await client.QueryAsync<GameTitle[]>("titles.list", ct: ct)).SingleOrDefault(g => g.TitleId.Equals(plan.ApplicationId, StringComparison.OrdinalIgnoreCase));
        var selected = plan.Items.Select(i => i.Entry.ComponentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unselectedIntact = game != null && Fingerprints.Equal(ManagedUpdates.InventoryFingerprint(game.Components.Where(c => !selected.Contains(c.TitleId))), ManagedUpdates.InventoryFingerprint(plan.Before.Where(c => !selected.Contains(c.TitleId))));
        var items = new List<InstallItemInspection>();
        foreach (var item in plan.Items)
        {
            var response = await client.QueryAsync<JsonElement>("operations.status", new { operationId = item.OperationId }, ct);
            var native = response.TryGetProperty("state", out var state) ? state.GetString() ?? "unknown" : "unknown";
            var version = game?.Components.Where(c => c.TitleId.Equals(item.Entry.ComponentId, StringComparison.OrdinalIgnoreCase)).Select(c => (uint?)c.Version).Max();
            var oldVersion = plan.Before.Where(c => c.TitleId.Equals(item.Entry.ComponentId, StringComparison.OrdinalIgnoreCase)).Select(c => (uint?)c.Version).Max();
            var result = !unselectedIntact ? "needs-inspection" : version == item.Entry.Version && (native is "verify-required" or "complete") ? "verified" : native == "unknown" && oldVersion == version ? version == item.Entry.Version ? "not-executed-already-current" : "not-executed" : "needs-inspection";
            items.Add(new(item.OperationId, item.Entry.ComponentId, native, version, result));
        }
        var overall = items.All(i => i.State is "verified" or "not-executed-already-current") ? "verified" : items.Any(i => i.State == "needs-inspection") ? "needs-inspection" : "stopped";
        return new(plan.Id, overall, items.ToArray(), DateTimeOffset.UtcNow);
    }
}
