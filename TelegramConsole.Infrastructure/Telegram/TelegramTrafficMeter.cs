using System.Net;
using System.Net.Sockets;

namespace TelegramConsole.Infrastructure;

internal sealed record TelegramTrafficSnapshot(long UploadedBytes, long DownloadedBytes);

/// <summary>
/// Relays WTelegram TCP connections through a local loopback socket so traffic can be
/// counted without changing the MTProto protocol or WTelegram reconnect behavior.
/// </summary>
internal static class TelegramTrafficMeter
{
    private static long _uploadedBytes;
    private static long _downloadedBytes;

    public static TelegramTrafficSnapshot Snapshot => new(
        Interlocked.Read(ref _uploadedBytes),
        Interlocked.Read(ref _downloadedBytes));

    public static Task<TcpClient> ConnectDirectAsync(string host, int port) =>
        ConnectMeteredAsync(async () =>
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        });

    public static async Task<TcpClient> ConnectMeteredAsync(Func<Task<TcpClient>> connectUpstream)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var localClient = new TcpClient(AddressFamily.InterNetwork);
        TcpClient? relayClient = null;
        TcpClient? upstreamClient = null;
        try
        {
            var acceptTask = listener.AcceptTcpClientAsync();
            await localClient.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            relayClient = await acceptTask;
            upstreamClient = await connectUpstream();
            _ = RelayConnectionAsync(relayClient, upstreamClient);
            relayClient = null;
            upstreamClient = null;
            return localClient;
        }
        catch
        {
            localClient.Dispose();
            relayClient?.Dispose();
            upstreamClient?.Dispose();
            throw;
        }
    }

    private static async Task RelayConnectionAsync(TcpClient local, TcpClient upstream)
    {
        using (local)
        using (upstream)
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                var localStream = local.GetStream();
                var upstreamStream = upstream.GetStream();
                var upload = RelayAsync(localStream, upstreamStream, true, cancellation.Token);
                var download = RelayAsync(upstreamStream, localStream, false, cancellation.Token);
                await Task.WhenAny(upload, download);
                await cancellation.CancelAsync();
                local.Close();
                upstream.Close();
                try { await Task.WhenAll(upload, download); }
                catch (Exception) { /* A closed peer is the normal end of a relay. */ }
            }
            catch (Exception)
            {
                // Connection establishment and protocol failures are surfaced by WTelegram.
            }
        }
    }

    private static async Task RelayAsync(
        Stream source,
        Stream destination,
        bool upload,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (upload) Interlocked.Add(ref _uploadedBytes, read);
            else Interlocked.Add(ref _downloadedBytes, read);
        }
    }
}
