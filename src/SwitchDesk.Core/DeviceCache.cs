using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed record CachedConsole(DeviceInfo Device, StorageInfo[] Storages, GameTitle[] Games, SaveInfo[] Saves, DateTimeOffset SyncedAt);

// A console and its system environment are a single cache identity. Old firmware data
// is useful offline, but is never evidence that a current USB operation is available.
public sealed class DeviceCache(string root)
{
    public static string Identity(string device, string environment) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device + "\0" + environment)));
    public async Task SaveAsync(CachedConsole value, CancellationToken ct = default)
    {
        var directory = Path.Combine(root, Identity(value.Device.DeviceId, value.Device.Environment)); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "inventory.json"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Wire.Json), ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public CachedConsole? Latest()
    {
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "inventory.json", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).Select(Read).Where(x => x != null).OrderByDescending(x => x!.SyncedAt).FirstOrDefault();
    }
    private static CachedConsole? Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 64 * 1024 * 1024) return null;
            var item = JsonSerializer.Deserialize<CachedConsole>(File.ReadAllText(path), Wire.Json);
            return item?.Device is { DeviceId: not null, Environment: not null, Capabilities: not null, Model: not null, Firmware: not null, Atmosphere: not null, Companion: not null, UsbSpeed: not null } &&
                item.Games != null && item.Games.All(g => g is { Name: not null, TitleId: not null, State: not null, Components: not null } && g.Components.All(c => c is { TitleId: not null, Storage: not null, Kind: not null, Size: >= 0 })) &&
                item.Storages != null && item.Storages.All(s => s is { Id: not null, Name: not null, TotalBytes: >= 0, FreeBytes: >= 0 } && s.FreeBytes <= s.TotalBytes) &&
                item.Saves != null && item.Saves.All(s => s is { Key: not null, TitleId: not null, UserId: not null, UserName: not null, Type: not null }) ? item : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public string IconPath(DeviceInfo device, string titleId) => Path.Combine(root, Identity(device.DeviceId, device.Environment), PathSafety.SafeName(titleId) + ".jpg");
}
