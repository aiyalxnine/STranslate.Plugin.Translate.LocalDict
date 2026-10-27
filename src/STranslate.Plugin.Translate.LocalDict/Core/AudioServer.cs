using System.Net;
using System.Net.Sockets;
using System.Text;

namespace STranslate.Plugin.Translate.LocalDict.Core;

/// <summary>
/// 仅监听 127.0.0.1 的极简 HTTP 服务，把 MDD 里的发音交给 STranslate 的发音按钮。
/// 宿主只会通过 HTTP 下载 Symbol.AudioUrl，所以本地音频需要这样转一道。
/// 地址带随机令牌；端口和令牌持久化，历史记录里的发音地址在重启后仍然有效。
/// </summary>
public sealed class AudioServer : IDisposable
{
    private const int MaxRequestBytes = 8 * 1024;

    private readonly Func<string, string, byte[]?> _resolve;
    private readonly Action<int>? _onPortChanged;
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public string Token { get; }
    public int Port { get; private set; }

    /// <param name="resolve">(词库标识, 资源路径) → 音频字节</param>
    /// <param name="preferredPort">优先使用的端口，0 表示由系统分配</param>
    /// <param name="token">地址令牌</param>
    /// <param name="onPortChanged">实际端口与首选端口不同时回调，用于保存</param>
    public AudioServer(Func<string, string, byte[]?> resolve, int preferredPort, string token, Action<int>? onPortChanged = null)
    {
        _resolve = resolve;
        Port = preferredPort;
        Token = token;
        _onPortChanged = onPortChanged;
    }

    public string BuildUrl(string dictionaryId, string resourcePath)
    {
        EnsureStarted();
        var escaped = string.Join('/', resourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"http://127.0.0.1:{Port}/{Token}/{dictionaryId}/{escaped}";
    }

    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_listener != null)
                return;

            var listener = TryStart(Port) ?? TryStart(0)
                ?? throw new InvalidOperationException("无法启动本地发音服务");
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _cts = new CancellationTokenSource();
            if (port != Port)
            {
                Port = port;
                _onPortChanged?.Invoke(port);
            }
            _ = AcceptLoopAsync(listener, _cts.Token);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static TcpListener? TryStart(int port)
    {
        if (port is < 0 or > 65535)
            return null;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();

                var requestLine = await ReadRequestLineAsync(stream, timeout.Token).ConfigureAwait(false);
                var parts = requestLine?.Split(' ');
                if (parts is not { Length: >= 2 } || (parts[0] != "GET" && parts[0] != "HEAD"))
                {
                    await WriteStatusAsync(stream, "405 Method Not Allowed", timeout.Token).ConfigureAwait(false);
                    return;
                }

                var data = Resolve(parts[1]);
                if (data == null)
                {
                    await WriteStatusAsync(stream, "404 Not Found", timeout.Token).ConfigureAwait(false);
                    return;
                }

                var header = $"HTTP/1.1 200 OK\r\nContent-Type: {data.Value.ContentType}\r\nContent-Length: {data.Value.Bytes.Length}\r\nCache-Control: max-age=86400\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token).ConfigureAwait(false);
                if (parts[0] == "GET")
                    await stream.WriteAsync(data.Value.Bytes, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 连接被提前关闭、超时等：丢弃本次请求即可
            }
        }
    }

    private (byte[] Bytes, string ContentType)? Resolve(string target)
    {
        var path = target.Split('?', 2)[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments[0] != Token)
            return null;

        var resourcePath = string.Join('/', segments.Skip(2).Select(Uri.UnescapeDataString));
        if (resourcePath.Contains("..", StringComparison.Ordinal))
            return null;

        var bytes = _resolve(segments[1], resourcePath);
        return bytes == null ? null : (bytes, ContentType(resourcePath));
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" or ".spx" => "audio/ogg",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            // 只需要请求行，但读完头部再回应，避免对方还在发送时被重置
            if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        var text = Encoding.ASCII.GetString(buffer, 0, total);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end > 0 ? text[..end] : null;
    }

    private static Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken).AsTask();
}
