using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed record DownloadResume(string Url, long Size, string Sha256, string ETag);
public sealed class ContentDownloads : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> files = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    private readonly IContentCredentials? credentials;
    public ContentDownloads() : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromMinutes(10) }) { }
    public ContentDownloads(HttpClient client, IContentCredentials? credentials = null) { http = client; this.credentials = credentials; }
    public ContentDownloads(IContentCredentials credentials) : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromMinutes(10) }, credentials) { }
    public async Task<string> FetchAsync(ContentSource source, ContentEntry entry, string cache, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Also validates source scope when callers construct an entry directly rather than via an index.
        ContentSources.Parse(JsonSerializer.SerializeToUtf8Bytes(new ContentIndex(1, [entry]), Wire.Json), source);
        var location = ContentSources.Resolve(source, entry.Location);
        var extension = Path.GetExtension(source.Kind == ContentSourceKind.Http ? new Uri(location).AbsolutePath : location).ToLowerInvariant();
        if (extension is not (".nsp" or ".xci" or ".nsz" or ".xcz")) throw new InvalidDataException("来源条目必须指向 NSP/XCI/NSZ/XCZ 文件");
        Directory.CreateDirectory(cache); LocalSafety.DemandDirectory(cache); var destination = Path.Combine(cache, entry.Sha256.ToUpperInvariant() + extension);
        var fileGate = files.GetOrAdd(destination, _ => new(1)); await fileGate.WaitAsync(ct);
        try
        {
            if (File.Exists(destination))
            {
                try { await VerifyAsync(destination, entry, ct); return destination; }
                catch (InvalidDataException) { File.Move(destination, destination + ".invalid-" + Guid.NewGuid().ToString("N")); }
            }
            await slots.WaitAsync(ct);
            try
            {
                var partial = destination + ".part";
                if (File.Exists(partial)) LocalSafety.DemandRegularFile(partial);
                if (File.Exists(partial + ".resume.json")) LocalSafety.DemandRegularFile(partial + ".resume.json");
                if (source.Kind == ContentSourceKind.Local) await CopyLocalAsync(location, partial, entry, progress, ct);
                else await HttpAsync(source, location, partial, entry, progress, ct);
                await VerifyAsync(partial, entry, ct); File.Move(partial, destination); File.Delete(partial + ".resume.json"); return destination;
            }
            finally { slots.Release(); }
        }
        finally { fileGate.Release(); }
    }
    public static async Task VerifyAsync(string path, ContentEntry entry, CancellationToken ct)
    {
        LocalSafety.DemandRegularFile(path); await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize, true);
        if (file.Length != entry.Size || !Fingerprints.Equal(Convert.ToHexString(await SHA256.HashDataAsync(file, ct)), entry.Sha256)) throw new InvalidDataException("内容长度或 SHA-256 不匹配，未交给主机安装");
    }
    private static async Task CopyLocalAsync(string source, string partial, ContentEntry entry, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        LocalSafety.DemandRegularFile(source); var stamp = File.GetLastWriteTimeUtc(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize, true);
        if (input.Length != entry.Size) throw new InvalidDataException("来源文件长度已变化");
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, Wire.ChunkSize, true))
        { await CopyBoundedAsync(input, output, 0, entry.Size, progress, ct); output.Flush(true); }
        if (File.GetLastWriteTimeUtc(source) != stamp) throw new IOException("来源文件在复制期间发生变化");
    }
    private async Task HttpAsync(ContentSource source, string url, string partial, ContentEntry entry, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var resumePath = partial + ".resume.json"; DownloadResume? resume = null;
        if (File.Exists(partial) && File.Exists(resumePath) && new FileInfo(resumePath).Length < 16384)
            try { resume = JsonSerializer.Deserialize<DownloadResume>(await File.ReadAllTextAsync(resumePath, ct), Wire.Json); } catch (JsonException) { }
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (resume == null || resume.Url != url || resume.Size != entry.Size || !Fingerprints.Equal(resume.Sha256, entry.Sha256) || !StrongTag(resume.ETag) || offset <= 0 || offset >= entry.Size) { resume = null; offset = 0; }
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.AcceptEncoding.ParseAdd("identity");
        ContentAuthentication.Apply(request, source, credentials);
        if (resume != null) { request.Headers.Range = new RangeHeaderValue(offset, null); request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(resume.ETag)); }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.RequestMessage?.RequestUri is {} actual && actual.AbsoluteUri != url)
            throw new InvalidDataException("HTTP 客户端发生未审查的重定向，拒绝下载");
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            // No redirects: neither credentials nor private source paths are forwarded to another server.
            throw new IOException("来源返回重定向，请配置最终目录地址后重试");
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentEncoding.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("下载资源不能使用内容编码");
        var etag = response.Headers.ETag?.ToString();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (resume == null || etag != resume.ETag || range?.Unit != "bytes" || range.From != offset || range.To != entry.Size - 1 || range.Length != entry.Size)
            { File.Delete(partial); File.Delete(resumePath); throw new InvalidDataException("续传资源身份或范围已变化，请重新下载"); }
        }
        else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
        else throw new IOException("下载来源未返回完整文件或有效续传");
        var remaining = entry.Size - offset;
        if (response.Content.Headers.ContentLength is {} length && length != remaining) throw new InvalidDataException("HTTP 响应长度与索引不一致");
        if (StrongTag(etag))
        {
            var meta = new DownloadResume(url, entry.Size, entry.Sha256, etag!); var temporary = resumePath + ".new";
            if (File.Exists(temporary)) File.Delete(temporary);
            await DurableFile.WriteNewAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(meta, Wire.Json), ct); File.Move(temporary, resumePath, true);
        }
        else File.Delete(resumePath); // A weak/no validator is usable, but never resumable.
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None, Wire.ChunkSize, true);
        output.Position = offset; await CopyBoundedAsync(input, output, offset, entry.Size, progress, ct); output.Flush(true);
    }
    private static bool StrongTag(string? value) => EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak && tag.Tag != "*";
    private static async Task CopyBoundedAsync(Stream input, Stream output, long offset, long size, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[Wire.ChunkSize]; long written = offset;
        while (written < size)
        {
            var count = await StreamReads.WithInactivityTimeoutAsync(input, buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - written)), ct);
            if (count == 0) throw new EndOfStreamException("下载中断，保留有可靠资源标识的续传记录");
            await output.WriteAsync(buffer.AsMemory(0, count), ct); written += count; progress?.Report(new(written, size, "下载到电脑缓存"));
        }
        if (await StreamReads.WithInactivityTimeoutAsync(input, buffer.AsMemory(0, 1), ct) != 0) throw new InvalidDataException("下载内容超出索引声明长度");
    }
    public void Dispose() { if (disposed) return; disposed = true; http.Dispose(); slots.Dispose(); foreach (var gate in files.Values) gate.Dispose(); }
}
