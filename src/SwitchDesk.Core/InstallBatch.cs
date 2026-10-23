namespace SwitchDesk.Core;

public sealed record InstallSource(string Path, long Length, DateTime LastWriteUtc, PackageInfo Package)
{
    public static InstallSource Capture(string path, PackageInfo package)
    { var file = new FileInfo(path); return new(file.FullName, file.Length, file.LastWriteTimeUtc, package); }
    public void Validate()
    {
        var file = new FileInfo(Path);
        if (!file.Exists || file.Length != Length || file.LastWriteTimeUtc != LastWriteUtc) throw new IOException("安装文件已变化，请重新加入清单：" + System.IO.Path.GetFileName(Path));
        if (!Package.Recognized || Package.RequiredBytes <= 0) throw new IOException("安装包尚未通过识别与空间预检");
    }
}

public static class InstallBatch
{
    public static long Validate(IEnumerable<InstallSource> sources, DeviceInfo reviewed, DeviceInfo current, StorageInfo target)
    {
        if (reviewed.DeviceId != current.DeviceId || reviewed.Environment != current.Environment) throw new IOException("连接的主机或系统环境已变化，请重新整理安装清单");
        if (!current.ApplicationMode || !current.Capabilities.Contains("install")) throw new IOException("请在完整应用模式下打开支持安装的配套程序");
        long required = 0;
        foreach (var source in sources) { source.Validate(); required = checked(required + source.Package.RequiredBytes); }
        if (target.FreeBytes < required) throw new IOException("目标空间不足，需要 " + Display.Size(required) + "，可用 " + Display.Size(target.FreeBytes));
        return required;
    }
}
