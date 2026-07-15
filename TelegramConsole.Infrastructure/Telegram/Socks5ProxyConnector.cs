using System.Net.Sockets;
using System.Text;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

internal static class Socks5ProxyConnector
{
    public static async Task<TcpClient> ConnectAsync(ProxySettings proxy, string targetHost, int targetPort)
    {
        var tcp = new TcpClient();
        try
        {
            ConfigureSocket(tcp);
            await tcp.ConnectAsync(proxy.Host, proxy.Port);
            var stream = tcp.GetStream();
            var useAuthentication = !string.IsNullOrEmpty(proxy.UserName);
            var greeting = useAuthentication
                ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
                : new byte[] { 0x05, 0x01, 0x00 };
            await stream.WriteAsync(greeting);
            var reply = await ReadExactAsync(stream, 2);
            if (reply[0] != 0x05 || reply[1] == 0xFF)
                throw new IOException("SOCKS5 代理不接受客户端支持的认证方式");
            if (reply[1] == 0x02)
                await AuthenticateAsync(stream, proxy.UserName, proxy.Password);
            else if (reply[1] != 0x00)
                throw new IOException($"SOCKS5 返回不支持的认证方式：{reply[1]}");

            var hostBytes = Encoding.UTF8.GetBytes(targetHost);
            if (hostBytes.Length > 255) throw new IOException("目标主机名过长");
            var request = new byte[7 + hostBytes.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = 0x03;
            request[4] = (byte)hostBytes.Length;
            hostBytes.CopyTo(request, 5);
            request[^2] = (byte)(targetPort >> 8);
            request[^1] = (byte)targetPort;
            await stream.WriteAsync(request);

            var header = await ReadExactAsync(stream, 4);
            if (header[0] != 0x05 || header[1] != 0x00)
                throw new IOException($"SOCKS5 连接目标失败，错误码：{header[1]}");
            var addressLength = header[3] switch
            {
                0x01 => 4,
                0x03 => (await ReadExactAsync(stream, 1))[0],
                0x04 => 16,
                _ => throw new IOException($"SOCKS5 返回未知地址类型：{header[3]}")
            };
            await ReadExactAsync(stream, addressLength + 2);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static void ConfigureSocket(TcpClient tcp)
    {
        tcp.NoDelay = true;
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var keepAlive = new byte[12];
            BitConverter.GetBytes(1u).CopyTo(keepAlive, 0);
            BitConverter.GetBytes(60_000u).CopyTo(keepAlive, 4);
            BitConverter.GetBytes(15_000u).CopyTo(keepAlive, 8);
            tcp.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);
        }
        catch
        {
            // Some platforms do not support tuning TCP keep-alive intervals.
        }
    }

    private static async Task AuthenticateAsync(NetworkStream stream, string userName, string password)
    {
        var user = Encoding.UTF8.GetBytes(userName);
        var pass = Encoding.UTF8.GetBytes(password);
        if (user.Length > 255 || pass.Length > 255) throw new IOException("代理用户名或密码过长");
        var request = new byte[3 + user.Length + pass.Length];
        request[0] = 0x01;
        request[1] = (byte)user.Length;
        user.CopyTo(request, 2);
        request[2 + user.Length] = (byte)pass.Length;
        pass.CopyTo(request, 3 + user.Length);
        await stream.WriteAsync(request);
        var reply = await ReadExactAsync(stream, 2);
        if (reply[1] != 0x00) throw new IOException("SOCKS5 用户名或密码错误");
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0) throw new EndOfStreamException("SOCKS5 代理提前关闭了连接");
            offset += read;
        }
        return buffer;
    }
}
