using System.Text.Json;
using Microsoft.Win32;
using SwitchDesk.Core;

if (args.Length != 2 || args[0] is not ("inspect-local" or "inspect-console" or "capture-console" or "catalog-check" or "credential-check")) { Console.Error.WriteLine("SwitchDesk.Tools inspect-local|inspect-console|capture-console|catalog-check|credential-check <private-output-directory>"); return 2; }
var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
if (args[0] == "credential-check")
{
    var vault = new WindowsContentCredentials(); var reference = Guid.NewGuid().ToString("N"); var dummy = new SourceCredential("SwitchDesk temporary self-test", Guid.NewGuid().ToString("N"));
    try
    {
        vault.Write(reference, dummy); if (vault.Read(reference) != dummy) throw new IOException("安全存储读回不同"); vault.Delete(reference);
        if (vault.Read(reference) != null) throw new IOException("临时凭据未删除");
        await File.WriteAllTextAsync(Path.Combine(output, "credential-check.json"), JsonSerializer.Serialize(new { build = BuildInfo.ClientId, observedAt = DateTimeOffset.UtcNow, temporaryGenericCredentialRoundtrip = true, removed = true, userCredentialsRead = false }, Wire.Json));
        Console.WriteLine("Windows credential roundtrip passed; temporary test entry removed; no user credentials inspected."); return 0;
    }
    finally { vault.Delete(reference); }
}
if (args[0] == "catalog-check")
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var catalog = await TitleDbProvider.FetchAsync(timeout.Token);
        var cached = new DeviceCache(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchDesk", "ConsoleCache")).Latest();
        var games = cached?.Games ?? []; var comparisons = games.Select(g => new { g.Name, g.TitleId, offers = ContentCatalog.Compare(g, catalog) }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(output, "catalog-check.json"), JsonSerializer.Serialize(new { privacy = "private-local-only", evidence = "public-catalog-and-prior-console-cache; no USB session or install", build = BuildInfo.ClientId, catalog.Source, catalog.FetchedAt, catalog.SourceModifiedAt, catalogCount = catalog.Components.Length, comparisons }, Wire.Json));
        Console.WriteLine($"Parsed {catalog.Components.Length} catalog components; compared {games.Length} cached games; no console connection or writes."); return 0;
    }
    catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or OperationCanceledException) { Console.Error.WriteLine("Catalog check failed: " + e.Message); return 5; }
}
if (args[0] is "inspect-console" or "capture-console")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var ct = timeout.Token;
    var candidates = UsbDiscovery.Enumerate();
    if (candidates.Count != 1) { Console.Error.WriteLine($"Expected one SwitchDesk USB interface, found {candidates.Count}; no console changes."); return 3; }
    try
    {
        await using var client = new DeviceClient(new WinUsbStream(candidates[0].Path)); var device = await client.HandshakeAsync(ct);
        var titles = await client.QueryAsync<GameTitle[]>("titles.list", ct: ct); var savesOnConsole = device.Capabilities.Contains("saves") ? await client.QueryAsync<SaveInfo[]>("saves.list", ct: ct) : [];
        await File.WriteAllTextAsync(Path.Combine(output, "console-inspection.json"), JsonSerializer.Serialize(new { privacy = "private-local-only", device, titles, saves = savesOnConsole, observedAt = DateTimeOffset.UtcNow }, Wire.Json), ct);
        var selected = titles.Where(g => new[] { "吸血鬼幸存者", "Vampire Survivors", "死亡细胞", "Dead Cells", "Hades", "哈迪斯" }.Any(name => g.Name.Contains(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var game in selected)
        {
            Console.WriteLine($"{game.Name}: display {game.DisplayVersion}, {game.Components.Length} components, {savesOnConsole.Count(s => s.TitleId == game.TitleId)} save records; read-only");
            if (args[0] != "capture-console") continue;
            var store = new SnapshotStore(Path.Combine(output, "ConsoleSamples"));
            foreach (var save in savesOnConsole.Where(s => s.TitleId == game.TitleId))
            { var snapshot = await store.BackupAsync(client, device, save, null, ct); await SnapshotStore.VerifyAsync(snapshot.Path, ct); Console.WriteLine($"Captured private {game.Name} sample: {snapshot.Manifest.Files.Length} files; no live writes"); }
        }
        Console.WriteLine($"Console inventory: {titles.Length} games, {savesOnConsole.Length} saves. Device/account details remain in the private report."); return 0;
    }
    catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidDataException or OperationCanceledException)
    { Console.Error.WriteLine($"Console read failed: {e.GetType().Name}: {e.Message}; no console writes requested."); return 4; }
}
using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"); var steam = steamKey?.GetValue("SteamPath") as string;
var saves = SteamSaves.Discover(steam, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
var builds = steam == null ? [] : SteamSaves.InstalledBuilds(steam);
var records = new List<object>();
foreach (var save in saves)
{
    var adapter = SaveAdapters.Get(save.AdapterId);
    try
    {
        SteamSaves.DemandClosed(adapter.Id, false); var file = await StableSaveFile.ReadAsync(save.Path, CancellationToken.None);
        var inspection = adapter.Inspect(file.Bytes, SavePlatform.Steam);
        records.Add(new { save, inspection, size = file.Bytes.Length, file.ModifiedAt, file.Fingerprint, observedAt = DateTimeOffset.UtcNow });
        var matches = builds.Where(x => x.AppId == adapter.SteamAppId).ToArray(); var build = matches.Length == 1 ? matches[0] : null;
        Console.WriteLine($"{adapter.Name} / slot {save.Slot}: {inspection.Format}, save field {inspection.Version}, Steam build {build?.BuildId ?? "unknown"}, {file.Bytes.Length} bytes; read-only");
    }
    catch (Exception e) when (e is IOException or InvalidDataException or JsonException or NotSupportedException)
    { records.Add(new { save, error = e.Message, observedAt = DateTimeOffset.UtcNow }); Console.WriteLine($"{adapter.Name} / slot {save.Slot}: {e.GetType().Name}; original preserved"); }
}
await File.WriteAllTextAsync(Path.Combine(output, "local-save-inspection.json"), JsonSerializer.Serialize(new { privacy = "private-local-only", build = BuildInfo.ClientId, records, installations = builds }, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
Console.WriteLine($"Inspected {saves.Length} candidates; no game data modified."); return 0;
