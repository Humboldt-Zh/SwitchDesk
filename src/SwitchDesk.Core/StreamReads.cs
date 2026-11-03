namespace SwitchDesk.Core;

public static class StreamReads
{
    public static async ValueTask<int> WithInactivityTimeoutAsync(Stream source, Memory<byte> buffer, CancellationToken ct, TimeSpan? inactivity = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(inactivity ?? TimeSpan.FromMinutes(2));
        try { return await source.ReadAsync(buffer, timeout.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new IOException("来源长时间没有响应，已保留可用的续传数据，请稍后重试", ex); }
    }
}
