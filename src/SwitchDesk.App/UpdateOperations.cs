using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwitchDesk.Core;

namespace SwitchDesk.App;

public sealed class UpdateChoice(UpdateOffer offer, ContentSource? source, ContentEntry? entry) : Observable
{
    public UpdateOffer Offer { get; } = offer;
    public ContentSource? Source { get; } = source;
    public ContentEntry? Entry { get; } = entry;
    public bool HasContent => Entry != null;
    private bool selected = entry != null && offer.Status == UpdateStatus.UpdateAvailable;
    public bool Selected { get => selected; set { Set(ref selected, value); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public string Label => $"{Offer.Label} · v{Offer.Component.Version}";
    public string Detail => $"目录：{Offer.Component.MetadataSource}" + (Offer.Component.RetrievedAt is {} time ? $" · {time.LocalDateTime:MM-dd HH:mm}" : "") + (Entry != null ? $"\n{Source!.Name} · {Display.Size(Entry.Size)}" : "\n尚无对应安装包 · 可添加自有来源");
}
public partial class MainWindow
{
    private void ReloadContentSources() { vm.ContentSources = phase2.List<ContentSource>("content-sources"); vm.SelectedSource = vm.ContentSources.FirstOrDefault(); }
    private void RemoveContentSource()
    {
        var source = vm.SelectedSource ?? throw new IOException("请选择来源"); var entry = phase2.Get<ContentSource>("content-sources", source.Id) ?? throw new IOException("来源已移除");
        phase2.Remove("content-sources", source.Id, entry.Revision); ReloadContentSources(); vm.SelectedOffer = null; vm.UpdateOffers = []; vm.Notify("来源已移除，原始文件与已下载内容保留");
        if (source.CredentialReference != null) new WindowsContentCredentials().Delete(source.CredentialReference);
    }
    private string? updateTitle;
    private void UpdatePhase2Details()
    {
        var id = vm.Library.Selected?.TitleId;
        if (id == updateTitle)
        {
            if (vm.Library.Selected?.Console is {} game && vm.UpdateOffers.Length > 0)
            {
                var selected = vm.SelectedOffer?.Offer.Component.ComponentId;
                var catalog = new CatalogSnapshot("已读取的目录", DateTimeOffset.MinValue, null, vm.UpdateOffers.Where(c => c.Offer.Status != UpdateStatus.Unknown).Select(c => c.Offer.Component).ToArray());
                var fresh = ContentCatalog.Compare(game, catalog);
                vm.UpdateOffers = vm.UpdateOffers.Select(old =>
                {
                    var offer = old.Offer.Status == UpdateStatus.Unknown ? old.Offer : fresh.Single(o => o.Component.ComponentId.Equals(old.Offer.Component.ComponentId, StringComparison.OrdinalIgnoreCase) && o.Component.Kind == old.Offer.Component.Kind);
                    return new UpdateChoice(offer, old.Source, old.Entry) { Selected = old.Selected && (offer.Status is UpdateStatus.UpdateAvailable or UpdateStatus.NewDlc or UpdateStatus.DlcUpdate) };
                }).ToArray();
                vm.SelectedOffer = vm.UpdateOffers.FirstOrDefault(o => o.Offer.Component.ComponentId == selected) ?? vm.UpdateOffers.FirstOrDefault();
            }
            return;
        }
        updateTitle = id;
        vm.UpdateOffers = []; vm.SelectedOffer = null; vm.UpdateSummary = "检查更新可查询目录；安装包来自你配置的来源";
        vm.CrossSave.Open = false; vm.CrossSave.SwitchFiles = []; vm.CrossSave.SelectedSwitchFile = null; vm.CrossSave.Invalidate();
    }
    private async Task CheckUpdatesAsync()
    {
        var game = vm.Library.Selected?.Console ?? throw new IOException("请选择已读取的主机游戏");
        var result = await RunJob("检查更新 · " + game.Name, async (_, ct) =>
        {
            var catalog = phase2.Get<CatalogSnapshot>("catalogs", "titledb")?.Value;
            // Explicit button is a manual refresh. A failed request retains the original timestamp.
            try
            {
                catalog = await FetchCatalogAsync(ct);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
            { ct.ThrowIfCancellationRequested(); Log("目录刷新失败，沿用原时间的缓存：" + e.Message); }
            catalog ??= new("TitleDB（当前不可用）", DateTimeOffset.MinValue, null, []);
            var entries = new List<(ContentSource Source, ContentEntry Entry)>();
            foreach (var source in phase2.List<ContentSource>("content-sources"))
            {
                try
                {
                    byte[] bytes;
                    if (source.Kind == ContentSourceKind.Local)
                    {
                        var path = ContentSources.Resolve(source, "index.json"); LocalSafety.DemandRegularFile(path);
                        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("索引超过解析预算"); bytes = await File.ReadAllBytesAsync(path, ct);
                    }
                    else
                    {
                        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
                        using var request = new HttpRequestMessage(HttpMethod.Get, ContentSources.Resolve(source, "index.json")); ContentAuthentication.Apply(request, source, new WindowsContentCredentials());
                        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync(ct); bytes = await ReadIndexBoundedAsync(stream, ct);
                    }
                    var index = ContentSources.Parse(bytes, source); phase2.Put("source-catalogs", source.Id, new SourceCatalog(source.Id, source.Name, DateTimeOffset.UtcNow, index));
                    entries.AddRange(index.Entries.Select(e => (source, e)));
                }
                catch (Exception e) when (e is IOException or JsonException or HttpRequestException)
                {
                    if (phase2.Get<SourceCatalog>("source-catalogs", source.Id)?.Value is {} cached) entries.AddRange(cached.Index.Entries.Select(entry => (source, entry)));
                    Log("内容来源不可用，缓存保留原获取时间：" + source.Name + " · " + e.Message);
                }
            }
            var activeSources = phase2.List<ContentSource>("content-sources").Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var combined = ContentCatalog.Combine(catalog, phase2.List<SourceCatalog>("source-catalogs").Where(c => activeSources.Contains(c.SourceId)));
            var choices = ContentCatalog.Compare(game, combined).Select(offer =>
            {
                var match = entries.FirstOrDefault(x => x.Entry.ApplicationId.Equals(game.TitleId, StringComparison.OrdinalIgnoreCase) && x.Entry.ComponentId.Equals(offer.Component.ComponentId, StringComparison.OrdinalIgnoreCase) && x.Entry.Version == offer.Component.Version && x.Entry.Kind == offer.Component.Kind);
                return new UpdateChoice(offer, match.Source, match.Entry);
            }).ToArray();
            return (Catalog: catalog, Choices: choices);
        });
        if (vm.Library.Selected?.TitleId != game.TitleId) return;
        ApplyCatalogLabels();
        vm.UpdateSummary = result.Catalog.FetchedAt == DateTimeOffset.MinValue ? "公共目录暂不可用 · 仍可使用自有索引；各条目标明来源" : $"{result.Catalog.Source} · 获取于 {result.Catalog.FetchedAt.LocalDateTime:MM-dd HH:mm} · 目录记录不代表可下载内容";
        vm.UpdateOffers = result.Choices; vm.SelectedOffer = result.Choices.FirstOrDefault();
    }
    private static async Task<byte[]> ReadIndexBoundedAsync(Stream input, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await StreamReads.WithInactivityTimeoutAsync(input, buffer, ct)) != 0) { if (output.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("索引超过 16 MiB"); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private void AddContentSource()
    {
        var dialog = new Window { Owner = this, Title = "添加自有内容来源", Width = 560, MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 64), SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)FindResource(typeof(Window)) };
        var panel = new StackPanel { Margin = new(24) }; dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "内容来源", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "选择包含 index.json 的目录，或填写自有 HTTP 目录地址。目录只提供元数据，安装包需由你准备。", Margin = new(0, 12, 0, 16) });
        var kind = new ComboBox { ItemsSource = new[] { "本地 / Windows 已连接的 NAS", "HTTP / HTTPS" }, SelectedIndex = 0 }; panel.Children.Add(kind);
        var input = new TextBox { Margin = new(0, 12, 0, 8) }; panel.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, "内容来源目录或 HTTPS 地址");
        var browse = new Button { Content = "选择目录", HorizontalAlignment = HorizontalAlignment.Left }; panel.Children.Add(browse);
        kind.SelectionChanged += (_, _) => browse.IsEnabled = kind.SelectedIndex == 0;
        browse.Click += (_, _) => { var pick = new OpenFolderDialog { Title = "选择包含 index.json 的来源目录" }; if (pick.ShowDialog(dialog) == true) input.Text = pick.FolderName; };
        var authentication = new Expander { Header = "可选：HTTPS 用户名与密码", Margin = new(0, 12, 0, 0) }; var authPanel = new StackPanel();
        var user = new TextBox { Margin = new(0, 8, 0, 8), ToolTip = "用户名", MaxLength = 200 }; var password = new PasswordBox { ToolTip = "密码", MaxLength = 1000 };
        System.Windows.Automation.AutomationProperties.SetName(user, "HTTPS 来源用户名"); System.Windows.Automation.AutomationProperties.SetName(password, "HTTPS 来源密码");
        authPanel.Children.Add(user); authPanel.Children.Add(password); authPanel.Children.Add(new TextBlock { Text = "保存在 Windows 凭据管理器，配置只保存引用；NAS 请使用 Windows 已建立的连接。", Margin = new(0, 8, 0, 0) }); authentication.Content = authPanel; panel.Children.Add(authentication);
        var error = new TextBlock { Margin = new(0, 12, 0, 0) }; error.SetResourceReference(ForegroundProperty, "Error"); panel.Children.Add(error);
        var save = new Button { Content = "添加来源", Margin = new(0, 16, 0, 0), Style = (Style)FindResource("Primary") }; panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            try
            {
                var root = input.Text.Trim(); if (root.Length == 0) throw new InvalidDataException("请填写来源目录");
                var source = new ContentSource(Guid.NewGuid().ToString("N"), kind.SelectedIndex == 0 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) : new Uri(root).Host, kind.SelectedIndex == 0 ? ContentSourceKind.Local : ContentSourceKind.Http, kind.SelectedIndex == 0 ? Path.GetFullPath(root) : root);
                var index = ContentSources.Resolve(source, "index.json"); if (source.Kind == ContentSourceKind.Local && !File.Exists(index)) throw new InvalidDataException("目录内缺少 index.json；格式说明见二期开发文档");
                if (user.Text.Length > 0 || password.Password.Length > 0)
                {
                    if (source.Kind != ContentSourceKind.Http || new Uri(source.Root).Scheme != "https") throw new InvalidDataException("带用户名密码的来源必须使用 HTTPS");
                    var reference = Guid.NewGuid().ToString("N"); new WindowsContentCredentials().Write(reference, new(user.Text, password.Password)); source = source with { CredentialReference = reference };
                }
                try { phase2.Put("content-sources", source.Id, source, 0); }
                catch { if (source.CredentialReference != null) new WindowsContentCredentials().Delete(source.CredentialReference); throw; }
                dialog.DialogResult = true;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UriFormatException or System.ComponentModel.Win32Exception) { error.Text = e.Message; }
        };
        var accepted = dialog.ShowDialog() == true; password.Clear();
        if (accepted) { ReloadContentSources(); vm.Notify("来源已添加，检查更新后可匹配安装包"); }
    }
    private async Task DownloadOfferAsync()
    {
        var selected = vm.SelectedOffer ?? throw new IOException("请选择目录条目"); var entry = selected.Entry ?? throw new IOException("请先添加包含此组件的自有来源"); var source = selected.Source!;
        var path = await RunJob("下载 · " + selected.Label, async (job, ct) =>
        {
            using var downloads = new ContentDownloads(new WindowsContentCredentials()); var file = await downloads.FetchAsync(source, entry, Path.Combine(settingsStore.Root, "Phase2", "Downloads"), Progress(job), ct);
            if (vm.Online && device?.Capabilities.Contains("package.dependencies.v1") == true)
            {
                var (peer, _) = RequireDevice("package.dependencies.v1"); var inspected = await Transfers.InspectAsync(peer, file, ct); ContentSources.ValidateInspected(entry, inspected);
                library.Upsert(new(file, inspected.GameName ?? selected.Label, entry.ApplicationId, entry.Kind, entry.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Size, entry.Sha256, "已校验并核对包内组件"));
            }
            else library.Upsert(new(file, selected.Label + " · 待核对包内信息", null, "Package", "待识别", entry.Size, entry.Sha256, "SHA-256 已校验，待新版主机解析"));
            phase2.Put("downloads", entry.Sha256, new { sourceId = source.Id, entry, path = file, state = "download-verified", time = DateTimeOffset.UtcNow }); return file;
        });
        LoadLocalLists(); vm.Notify("下载校验完成 · 安装前仍需主机核对包内信息、依赖与存档保护", Path.GetDirectoryName(path)!);
    }
    private async Task BuildContentIndexAsync()
    {
        var pick = new OpenFolderDialog { Title = "选择要生成 index.json 的安装包目录" }; if (pick.ShowDialog(this) != true) return;
        var (_, identity) = RequireDevice("package.dependencies.v1"); var directory = pick.FolderName;
        var index = await RunJob("生成内容索引", async (_, ct) =>
        {
            var (peer, current) = RequireDevice("package.dependencies.v1"); if (current.DeviceId != identity.DeviceId || current.Environment != identity.Environment) throw new IOException("主机环境已变化");
            return await ContentIndexBuilder.BuildAsync(directory, (path, token) => Transfers.InspectAsync(peer, path, token), ct);
        });
        var path = Path.Combine(directory, "index.json");
        if (File.Exists(path) && !Dialogs.Confirm(this, "替换来源索引", $"已解析 {index.Entries.Length} 个独立组件。将保留旧索引副本并替换 index.json；安装包不会修改。", "保留副本并替换")) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try { await DurableFile.WriteNewAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(index, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }), lifetime.Token);
            if (File.Exists(path)) File.Replace(temporary, path, path + "." + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak"); else File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        vm.Notify($"内容索引已生成 · {index.Entries.Length} 项", directory);
    }
    private async Task InstallOfferAsync()
    {
        var choices = vm.UpdateOffers.Where(x => x.Selected && x.Entry != null).ToArray(); if (choices.Length == 0) throw new IOException("请勾选要安装的更新与 DLC");
        var (_, identity) = RequireDevice("install.plan.v1"); var storage = UpdateStorage.SelectedValue?.ToString() ?? "SdCard";
        var plan = await RunJob($"预检更新 · {choices.Length} 项", async (job, ct) =>
        {
            var (peer, info) = RequireDevice("install.plan.v1"); if (info.DeviceId != identity.DeviceId || info.Environment != identity.Environment) throw new IOException("主机环境已变化");
            using var downloader = new ContentDownloads(new WindowsContentCredentials()); var total = choices.Sum(c => c.Entry!.Size); var completed = new long[choices.Length]; var aggregateLock = new object(); var progress = Progress(job);
            var paths = await Task.WhenAll(choices.Select(async (choice, i) =>
            {
                var source = choice.Source!; if (phase2.Get<ContentSource>("content-sources", source.Id) == null) throw new IOException("所选内容来源已移除，请重新检查更新");
                void Report(TransferProgress value) { lock (aggregateLock) { completed[i] = value.Completed; progress.Report(new(completed.Sum(), total, $"下载更新清单 · {choices.Length} 项")); } }
                var path = await downloader.FetchAsync(source, choice.Entry!, Path.Combine(settingsStore.Root, "Phase2", "Downloads"), new DirectProgress(Report), ct); Report(new(choice.Entry!.Size, choice.Entry.Size, "下载完成")); return path;
            }));
            var items = new List<ManagedBatchItem>();
            for (var i = 0; i < choices.Length; i++) { var package = await Transfers.InspectAsync(peer, paths[i], ct); items.Add(new(choices[i].Entry!, package, paths[i], Guid.NewGuid().ToString("N"))); }
            var current = await peer.QueryAsync<DeviceInfo>("device.info", ct: ct); var games = await peer.QueryAsync<GameTitle[]>("titles.list", ct: ct); var volumes = await peer.QueryAsync<StorageInfo[]>("storages.list", ct: ct);
            if (current.DeviceId != identity.DeviceId || current.Environment != identity.Environment) throw new IOException("目标主机已变化");
            return ManagedUpdateBatches.Prepare(current, storage, volumes.Single(v => v.Id == storage), games, items.ToArray());
        });
        if (!Dialogs.Confirm(this, "安装更新与 DLC", $"清单  {string.Join("、", choices.Select(c => c.Label))}\n目标  {identity.Model} · {identity.Environment} · {(storage == "SdCard" ? "SD 卡" : "用户分区")}\n空间  {Display.Size(plan.RequiredBytes)}\n\n先保护所有用户存档，按游戏更新、DLC、DLC 更新的依赖顺序安装；逐项复读真实版本。", $"备份存档并开始安装 {choices.Length} 项")) return;
        var results = await RunJob($"批量更新 · {choices.Length} 项", async (job, ct) =>
        {
            var (peer, _) = RequireDevice("install.plan.v1"); phase2.Put("install-batches", plan.Id, new { plan, state = "running", time = DateTimeOffset.UtcNow }, 0);
            try { var installed = await ManagedUpdateBatches.ExecuteAsync(peer, plan, new SnapshotStore(settings.BackupDirectory), Progress(job), ct);
                phase2.Put("install-batches", plan.Id, new { plan, state = "complete", installed, time = DateTimeOffset.UtcNow });
                await Dispatcher.InvokeAsync(() => job.Result = $"{installed.Count(r => r.Result.Outcome != InstallOutcome.NoChange)} 项内容变化 · {installed.Count(r => r.Result.Outcome == InstallOutcome.NoChange)} 项版本未变");
                await RefreshDeviceAsync(ct); return installed; }
            catch (Exception ex) { var completed = ex is ManagedBatchException batchError ? batchError.Completed : ex is ManagedBatchCanceledException canceled ? canceled.Completed : []; phase2.Put("install-batches", plan.Id, new { plan, state = "needs-inspection", completed, error = ex.Message, time = DateTimeOffset.UtcNow }); throw; }
        }, InstallOfferAsync);
        vm.Notify($"{results.Length} 项已核对 · {results.Count(r => r.Result.Outcome != InstallOutcome.NoChange)} 项版本变化", Path.Combine(settings.BackupDirectory, "Recovery"));
    }
}
