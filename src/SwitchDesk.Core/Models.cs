using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwitchDesk.Core;

public static class Wire
{
    public const ushort Version = 1;
    public const uint Magic = 0x314B4453; // SDK1, little endian
    public const int ChunkSize = 1024 * 1024;
    public const int MaxFrame = 16 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };
}

public sealed record DeviceInfo(string DeviceId, string Model, string Firmware, string Atmosphere,
    string Companion, string Environment, string UsbSpeed, string[] Capabilities, bool ApplicationMode);
public sealed record StorageInfo(string Id, string Name, long TotalBytes, long FreeBytes)
{
    public double UsedPercent => TotalBytes > 0 ? 100.0 * (TotalBytes - FreeBytes) / TotalBytes : 0;
    public string Summary => $"{Display.Size(FreeBytes)} 可用 / {Display.Size(TotalBytes)}";
}
public sealed record TitleComponent(string TitleId, string Kind, uint Version, string Storage, long Size)
{
    public string KindText => Kind == "Base" ? "本体" : Kind == "Update" ? "更新" : "DLC";
    public string StorageText => Storage == "SdCard" ? "SD 卡" : Storage == "BuiltInUser" ? "用户分区" : Storage == "GameCard" ? "卡带" : Storage;
}
public sealed record GameTitle(string TitleId, string Name, string Publisher, string DisplayVersion,
    string State, TitleComponent[] Components, string? IconBase64 = null)
{
    public long Size => Components.Sum(c => c.Size);
    public string SizeText => Display.Size(Size);
    public int DlcCount => Components.Count(c => c.Kind == "DLC");
    public string ComponentsText => $"{Components.Count(c => c.Kind == "Base")} 本体 · {Components.Count(c => c.Kind == "Update")} 更新 · {DlcCount} DLC";
    public string Locations => string.Join(" / ", Components.Select(c => c.StorageText).Distinct());
}
public sealed record SaveInfo(string Key, string TitleId, string GameName, string UserId, string UserName,
    string Type, long Size, string Environment);
public sealed record RemoteEntry(string Name, string Path, bool IsDirectory, long Size)
{ public string SizeText => IsDirectory ? "文件夹" : Display.Size(Size); }
public sealed record PackageInfo(string Name, long RequiredBytes, TitleComponent[] Components, string? TitleId,
    string? GameName, bool Recognized, string? Detail = null);
public sealed record TransferProgress(long Completed, long Total, string Stage)
{ public double Percent => Total > 0 ? Math.Clamp(100.0 * Completed / Total, 0, 100) : 0; }
public sealed record SnapshotFile(string Path, long Size, string Sha256);
public sealed record SaveManifest(int FormatVersion, string DeviceId, string Environment, string TitleId,
    string GameName, string UserId, string UserName, string SaveType, DateTimeOffset CreatedAt,
    SnapshotFile[] Files, string ContentHash, string[]? Directories = null);
public sealed record Snapshot(string Path, SaveManifest Manifest)
{
    public string Name => $"{Manifest.GameName} · {Manifest.UserName}";
    public string TimeText => Manifest.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeText => Display.Size(Manifest.Files.Sum(f => f.Size));
}
public sealed record GameManifest(int FormatVersion, string DeviceId, string Environment, string TitleId,
    string Name, TitleComponent Component, string Sha256, long Size, DateTimeOffset ExportedAt);
public sealed record LibraryItem(string Path, string Name, string? TitleId, string Kind, string Version,
    long Size, string? Sha256, string Status)
{ public string SizeText => Display.Size(Size); }
public sealed record AppSettings(string BackupDirectory, string LibraryDirectory, string ExportDirectory,
    string SysDvrClient, int Retention, string[] AutoBackupKeys)
{
    public string Theme { get; init; } = "System";
    public bool ListView { get; init; }
    public static AppSettings Default(string root) => new(Path.Combine(root, "Backups"), Path.Combine(root, "Library"),
        Path.Combine(root, "Exports"), "", 10, []);
}
public static class Display
{
    public static string Size(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = value; var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.##} {units[index]}";
    }
}
public sealed class DeviceException(string code, string message) : IOException(message)
{ public string Code { get; } = code; }
