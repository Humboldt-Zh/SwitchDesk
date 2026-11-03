using System.Net;
using System.Net.Http;

namespace SwitchDesk.Core;

public static class TitleDbProvider
{
    private static readonly string[] Endpoints = ["https://raw.githubusercontent.com/blawar/titledb/master/versions.txt", "https://api.github.com/repos/blawar/titledb/contents/versions.txt?ref=master"];
    public static async Task<CatalogSnapshot> FetchAsync(CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(8) });
        return await FetchAsync(http, ct);
    }
    public static async Task<CatalogSnapshot> FetchAsync(HttpClient http, CancellationToken ct)
    {
        Exception? failure = null;
        foreach (var endpoint in Endpoints)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint); request.Headers.UserAgent.ParseAdd("SwitchDesk/0.3"); request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                var catalog = await ContentCatalog.ReadTitleDbAsync(input, DateTimeOffset.UtcNow, response.Content.Headers.LastModified, timeout.Token);
                return catalog with { Endpoint = endpoint };
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            { ct.ThrowIfCancellationRequested(); failure = ex; }
        }
        throw new IOException("公共目录暂不可用，保留已有目录时间，可继续使用自有来源", failure);
    }
}
