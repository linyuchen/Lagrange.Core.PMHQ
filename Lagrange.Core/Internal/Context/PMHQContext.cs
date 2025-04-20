using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lagrange.Core.Common;
using Lagrange.Core.Internal.Packets;
using Lagrange.Core.Utility.Extension;

namespace Lagrange.Core.Internal.Context;

public class WebSocketMessage
{
    [JsonPropertyName("type")] 
    public string Type { get; set; }

    [JsonPropertyName("data")]
    public MessageData Data { get; set; }
}

public class MessageData
{
    
    [JsonPropertyName("echo")]
    public string? Echo { get; set; }
    
    [JsonPropertyName("cmd")]
    public string Command { get; set; }

    [JsonPropertyName("pb")]
    public string PayloadHex { get; set; }
}
internal class PMHQContext : ContextBase
{
    private const string Tag = nameof(PMHQContext);
    
    private readonly BotConfig _config;
    private ClientWebSocket _webSocket;
    private CancellationTokenSource _cts;
    private Uri _serverUri;

    public bool Connected = false;

    public PMHQContext(ContextCollection collection, BotKeystore keystore, BotAppInfo appInfo, BotDeviceInfo device, BotConfig config) 
        : base(collection, keystore, appInfo, device)
    {
        _config = config;
        _serverUri = new Uri(string.Format("ws://{0}:{1}/ws", config.PMHQ.Host, config.PMHQ.Port));
        _cts = new CancellationTokenSource();
    }

    public void Start()
    {
        if (!Connect().GetAwaiter().GetResult())
        {
            ScheduleReconnect();
        }
    }
    public async Task<bool> Connect()
    {
        if (Connected) return true;
        Collection.Log.LogInfo(Tag, "WS Connecting...");
        try
        {
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(_serverUri, _cts.Token);
            _ = StartReceiveLoop();
            Collection.Log.LogInfo(Tag, "WS Connect Success");
            Connected = true;
            return true;
        }
        catch (Exception ex)
        {
            Collection.Log.LogFatal(Tag, "WS Connect Failed: " + ex.Message);
            return false;
        }
    }

    private async Task StartReceiveLoop()
    {
        var buffer = new byte[4096];
        StringBuilder _textBuffer = new(); 
        try
        {
            while (Connected && !_cts.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(buffer, _cts.Token);
            
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _textBuffer.Append(chunk);

                    if (result.EndOfMessage)
                    {
                        string fullMessage = _textBuffer.ToString();
                        _textBuffer.Clear();
                        var message = JsonSerializer.Deserialize<WebSocketMessage>(fullMessage);
                        if (message != null)
                        {
                            string echo = message.Data.Echo;
                            if (string.IsNullOrEmpty(echo))
                            {
                                echo = "0";
                            }
                            var packet = new SsoPacket(12, message.Data.Command, uint.Parse(echo),
                                message.Data.PayloadHex.UnHex());
                            if (Collection.Packet != null)
                            {
                                Collection.Packet.DispatchPacket(packet);
                            }
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnDisconnect();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Collection.Log.LogFatal(Tag, "WS fetch message error: " + ex.Message);
        }
    }

    public async Task<bool> Send(SsoPacket packet)
    {
        if (!Connected) return false;
        var payload = new {
            type = "send",
            data = new {
                echo = packet.Sequence.ToString(),
                cmd = packet.Command,
                pb = packet.Payload.Hex()
            }
        };

        string json = JsonSerializer.Serialize(payload);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json); 
        try
        {
            await _webSocket.SendAsync(jsonBytes, WebSocketMessageType.Text, true, _cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            Collection.Log.LogWarning(Tag, ex.Message);
            return false;
        }
    }

    public void Disconnect()
    {
        try
        {
            _cts.Cancel();
            _webSocket.Abort();
            _webSocket.Dispose();
            _webSocket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
        }
        catch (Exception ex)
        {
            
        }
    }

    public void OnDisconnect()
    {
        Connected = false;
        Collection.Log.LogFatal(Tag, "WebSocket Disconnected");
        ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        Collection.Scheduler.Interval("WS Reconnect", 5 * 1000, async () =>
        {
            if (await Connect())
            {
                Collection.Scheduler.Cancel("WS Reconnect");
            }
            else
            {
                Collection.Log.LogWarning(Tag, "WS Reconnecting");
            }
        });
    }

    public void Dispose()
    {
        Disconnect();
        _webSocket.Dispose();
        _cts.Dispose();
    }
}