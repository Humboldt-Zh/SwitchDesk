using System.Net.Http;
using SwitchDesk.Core;

namespace SwitchDesk.App;
public partial class MainWindow
{
    private void ApplyCatalogLabels()
    {
        var catalog = phase2.Get<CatalogSnapshot>("catalogs", "titledb")?.Value ?? new CatalogSnapshot("无公共目录缓存", DateTimeOffset.MinValue, null, []);
        var active = phase2.List<ContentSource>("content-sources").Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        catalog = ContentCatalog.Combine(catalog, phase2.List<SourceCatalog>("source-catalogs").Where(c => active.Contains(c.SourceId)));
        var byApplication = catalog.Components.ToLookup(c => c.ApplicationId, StringComparer.OrdinalIgnoreCase);
        var updates = titles.Where(g => ContentCatalog.Compare(g, catalog with { Components = byApplication[g.TitleId].ToArray() }).Any(o => o.Status is UpdateStatus.UpdateAvailable or UpdateStatus.DlcUpdate or UpdateStatus.NewDlc)).Select(g => g.TitleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only observed native states may label a title hidden. A PC preference is not proof of HOME behavior.
        vm.Library.ApplyStates(updates, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
    private async Task<CatalogSnapshot> FetchCatalogAsync(CancellationToken ct)
    {
        var catalog = await TitleDbProvider.FetchAsync(ct);
        phase2.Put("catalogs", "titledb", catalog); return catalog;
    }
    private async Task RefreshCatalogInBackgroundAsync()
    {
        if (fixtureMode || lifetime.IsCancellationRequested) return;
        var now = DateTimeOffset.UtcNow; var attempt = phase2.Get<DateTimeOffset>("catalog-policy", "last-auto-attempt")?.Value;
        if (attempt is {} last && now >= last && now - last < TimeSpan.FromHours(24)) return;
        var catalog = phase2.Get<CatalogSnapshot>("catalogs", "titledb")?.Value;
        if (catalog?.NeedsRefresh(now) == false) return;
        phase2.Put("catalog-policy", "last-auto-attempt", now);
        try { await RunJob("自动检查更新目录", async (_, ct) => { await FetchCatalogAsync(ct); await Dispatcher.InvokeAsync(ApplyCatalogLabels); return true; }); }
        catch (Exception ex) when (ex is System.IO.IOException or HttpRequestException or OperationCanceledException) { Log("自动目录检查未完成，保留原目录时间：" + ex.Message); }
    }
}
