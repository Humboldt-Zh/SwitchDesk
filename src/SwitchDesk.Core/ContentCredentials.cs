using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed record SourceCredential(string UserName, string Password)
{ public override string ToString() => "[内容来源凭据已隐藏]"; }
public interface IContentCredentials { SourceCredential? Read(string reference); }
public static class ContentAuthentication
{
    public static void Apply(HttpRequestMessage request, ContentSource source, IContentCredentials? credentials)
    {
        if (source.CredentialReference == null) return;
        if (source.Kind != ContentSourceKind.Http || request.RequestUri?.Scheme != "https") throw new InvalidDataException("带凭据的内容来源要求 HTTPS");
        ContentSources.Resolve(source, request.RequestUri.AbsoluteUri);
        var value = credentials?.Read(source.CredentialReference) ?? throw new IOException("此来源的 Windows 安全凭据不可用，请重新配置");
        if (value.UserName == null || value.Password == null || value.UserName.Contains(':') || value.UserName.IndexOfAny(['\r', '\n']) >= 0) throw new InvalidDataException("来源用户名格式无效");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(value.UserName + ":" + value.Password)));
    }
}
/// <summary>Own generic credentials only; configuration contains a GUID reference, never a secret.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsContentCredentials : IContentCredentials
{
    private static string Target(string reference) => Guid.TryParseExact(reference, "N", out _) ? "SwitchDesk/content/" + reference : throw new ArgumentException("来源凭据引用无效");
    public void Write(string reference, SourceCredential value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json); if (bytes.Length > 2560) throw new ArgumentException("来源凭据过长");
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Target(reference), CredentialBlobSize = (uint)bytes.Length, CredentialBlob = memory, Persist = 2, UserName = "SwitchDesk content source" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(memory, i, 0); Marshal.FreeHGlobal(memory); System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    public SourceCredential? Read(string reference)
    {
        if (!CredRead(Target(reference), 1, 0, out var pointer)) { var error = Marshal.GetLastWin32Error(); if (error == 1168) return null; throw new Win32Exception(error); }
        try
        {
            var record = Marshal.PtrToStructure<Credential>(pointer); if (record.CredentialBlobSize > 2560) throw new InvalidDataException("来源凭据格式无效");
            var bytes = new byte[record.CredentialBlobSize]; Marshal.Copy(record.CredentialBlob, bytes, 0, bytes.Length);
            try { return JsonSerializer.Deserialize<SourceCredential>(bytes, Wire.Json); } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }
    public void Delete(string reference) { if (!CredDelete(Target(reference), 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type; public string? TargetName, Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public nint CredentialBlob; public uint Persist, AttributeCount; public nint Attributes; public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
}
