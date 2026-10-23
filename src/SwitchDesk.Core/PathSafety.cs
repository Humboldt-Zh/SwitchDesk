namespace SwitchDesk.Core;

public static class PathSafety
{
    public static string Remote(string path)
    {
        if (path.Contains('\\') || path.Contains('\0') || !path.StartsWith('/') || path.Contains(':'))
            throw new InvalidDataException("主机路径格式错误");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(p => p is "." or "..")) throw new InvalidDataException("路径不允许包含相对跳转");
        var result = "/" + string.Join('/', segments);
        if (System.Text.Encoding.UTF8.GetByteCount(result) > 700) throw new InvalidDataException("主机路径过长");
        return result;
    }
    public static void DemandWritable(string path)
    {
        path = Remote(path);
        string[] protectedRoots = ["/Nintendo", "/emuMMC", "/atmosphere", "/bootloader", "/switch/SwitchDesk", "/config/SwitchDesk"];
        string[] protectedFiles = ["/exosphere.ini", "/hekate_ipl.ini", "/hbmenu.nro", "/boot.dat", "/boot.ini"];
        if (path == "/" || protectedRoots.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)) ||
            protectedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) throw new DeviceException("ProtectedPath", "该目录由系统或配套程序使用，禁止通过文件管理修改");
    }
    public static string Relative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Contains('\0')) throw new InvalidDataException("无效的备份文件路径");
        if (path.Split('/').Any(p => string.IsNullOrEmpty(p) || p is "." or "..")) throw new InvalidDataException("备份路径越界");
        return path;
    }
    public static string SafeName(string value)
    {
        var name = string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (name.Length == 0) return "_";
        if (ReservedName(name)) name = "_" + name;
        return name.Length <= 120 ? name : name[..120].TrimEnd('.', ' ');
    }
    private static bool ReservedName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9';
    }
    public static string Child(string root, string relative)
    {
        Relative(relative); var fullRoot = Path.GetFullPath(root);
        if (relative.Split('/').Any(name => name.EndsWith('.') || name.EndsWith(' ') || ReservedName(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("主机文件名不能安全地保存到 Windows：" + relative);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("本地路径越界");
        return target;
    }
}
