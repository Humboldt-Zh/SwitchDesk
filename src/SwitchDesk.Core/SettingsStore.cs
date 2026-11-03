using System.Diagnostics;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed class SettingsStore(string directory)
{
    public string Root => directory;
    public string RecoveryMessage { get; private set; } = "";
    public AppSettings Load()
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return AppSettings.Default(directory);
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Wire.Json) ?? throw new InvalidDataException("设置文件格式错误");
            settings = settings with { AutoBackupKeys = settings.AutoBackupKeys ?? [], SysDvrClient = settings.SysDvrClient ?? "", Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "System" };
            Validate(settings); return settings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            var backup = path + ".invalid-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
            try { File.Move(path, backup); RecoveryMessage = "设置损坏，已保留原文件并使用默认设置。可在设置中重新选择原有目录。"; }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { RecoveryMessage = "设置无法读取，原文件保持不变；本次使用默认设置。"; }
            return AppSettings.Default(directory);
        }
    }
    public void Save(AppSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(directory); var target = Path.Combine(directory, "settings.json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Wire.Json)); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(AppSettings settings)
    {
        if (settings.Retention < 1 || settings.Retention > 1000) throw new InvalidDataException("历史版本保留数量应为 1–1000");
        foreach (var path in new[] { settings.BackupDirectory, settings.LibraryDirectory, settings.ExportDirectory })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("请选择绝对路径");
        if (settings.AutoBackupKeys == null || settings.AutoBackupKeys.Any(x => x == null)) throw new InvalidDataException("自动备份设置无效");
    }
}
public static class Streaming
{
    public static Process Start(string executable)
    {
        var info = UsbStreamingRuntime.CreateStartInfo(executable, "SwitchDesk USB " + Guid.NewGuid().ToString("N"));
        return Process.Start(info) ?? throw new IOException("无法启动 SysDVR 客户端");
    }
}
