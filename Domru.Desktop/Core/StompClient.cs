using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Domru.Desktop.Core;
public sealed class StompClient : IAsyncDisposable
{
    private readonly DomruApi api;
    private readonly string host;
    private readonly CancellationTokenSource stop = new();
    private Task? run;
    public event Action<string>? Status;
    public event Action<string, JsonNode?>? Message;
    public StompClient(DomruApi api, string host)
    {
        this.api = api;
        this.host = host;
    }

    public void Start() => run = RunAsync(stop.Token);
    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.AddSubProtocol("v12.stomp");
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                ws.Options.SetRequestHeader("Authorization", "Bearer " + api.Session?.AccessToken);
                ws.Options.SetRequestHeader("User-Agent", api.UserAgent);
                if (api.Session?.OperatorId is { } op)
                    ws.Options.SetRequestHeader("Operator", op);
                Status?.Invoke("WSS: подключение");
                await ws.ConnectAsync(new Uri($"wss://{host}/events"), ct);
                await Send(ws, $"CONNECT\naccept-version:1.2,1.1,1.0\nhost:{host}\nheart-beat:0,0\n\n\0", ct);
                var pending = new StringBuilder();
                var bytes = new byte[16384];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var frame = new MemoryStream();
                    WebSocketReceiveResult chunk;
                    do
                    {
                        chunk = await ws.ReceiveAsync(bytes, ct);
                        if (chunk.MessageType == WebSocketMessageType.Close)
                            throw new WebSocketException("сервер закрыл соединение");
                        frame.Write(bytes, 0, chunk.Count);
                        if (frame.Length > 1024 * 1024)
                            throw new InvalidDataException("Слишком большое сообщение WSS");
                    }
                    while (!chunk.EndOfMessage);
                    if (chunk.MessageType != WebSocketMessageType.Text)
                        continue;
                    pending.Append(Encoding.UTF8.GetString(frame.ToArray()));
                    while (true)
                    {
                        var text = pending.ToString();
                        var end = text.IndexOf('\0');
                        if (end < 0)
                        {
                            if (string.IsNullOrWhiteSpace(text))
                                pending.Clear();
                            break;
                        }

                        var raw = text[..end].TrimStart('\n', '\r');
                        pending.Remove(0, end + 1);
                        var split = raw.IndexOf("\n\n", StringComparison.Ordinal);
                        if (split < 0)
                            continue;
                        var command = raw.Split('\n')[0].Trim();
                        var body = raw[(split + 2)..];
                        if (command == "CONNECTED")
                        {
                            backoff = 2;
                            Status?.Invoke("WSS: подключено");
                            await Send(ws, "SUBSCRIBE\nid:desktop-events\ndestination:/user/queue\nack:auto\n\n\0", ct);
                            await api.RequestAsync("rest/v1/stomp/available-features", ct: ct);
                        }
                        else if (command == "ERROR")
                            throw new WebSocketException("STOMP отклонил подключение");
                        else if (command == "MESSAGE")
                        {
                            try
                            {
                                var n = JsonNode.Parse(body);
                                var type = Json.Str(n, "type") ?? "message";
                                var p = n?["payload"];
                                if (p is JsonValue v && v.TryGetValue<string>(out var s))
                                {
                                    try
                                    {
                                        p = JsonNode.Parse(s);
                                    }
                                    catch
                                    {
                                        p = JsonValue.Create(s);
                                    }
                                }

                                Message?.Invoke(type, p);
                            }
                            catch (System.Text.Json.JsonException)
                            {
                                Status?.Invoke("WSS: неизвестный формат сообщения");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                Status?.Invoke($"WSS: переподключение через {backoff} с");
                try
                {
                    if (api.Session?.RefreshToken is not null)
                        await api.RefreshAsync(ct: ct);
                }
                catch
                {
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = Math.Min(60, backoff * 2);
        }
    }

    private static Task Send(ClientWebSocket ws, string text, CancellationToken ct) => ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (run is not null)
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }

        stop.Dispose();
    }
}
