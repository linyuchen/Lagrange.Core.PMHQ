using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lagrange.Core.Common;
using Lagrange.Core.Internal.Event.System;
using Lagrange.Core.Internal.Packets;
using Lagrange.Core.Utility.Extension;
using Lagrange.Core.Utility.Network;

namespace Lagrange.Core.Internal.Context;

public class GetSelfInfoResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }

    [JsonPropertyName("message")] public string Message { get; set; }

    [JsonPropertyName("type")] public string Type { get; set; }

    [JsonPropertyName("data")] public GetSelfInfoResponseData Data { get; set; }
}

public class GetSelfInfoResponseData
{
    [JsonPropertyName("echo")] public string Echo { get; set; }

    [JsonPropertyName("result")] public GetSelfInfoResultData Result { get; set; }
}

public class GetSelfInfoResultData
{
    [JsonPropertyName("uin")] public string Uin { get; set; }

    [JsonPropertyName("uid")] public string Uid { get; set; }
}

public class WebSocketMessage
{
    [JsonPropertyName("type")] public string Type { get; set; }

    [JsonPropertyName("data")] public MessageData Data { get; set; }
}

public class MessageData
{
    [JsonPropertyName("echo")] public string? Echo { get; set; }

    [JsonPropertyName("cmd")] public string? Command { get; set; }

    [JsonPropertyName("pb")] public string? PayloadHex { get; set; }
}

internal class PMHQContext : ContextBase
{
    private const string Tag = nameof(PMHQContext);

    private ClientWebSocket _webSocket;
    private CancellationTokenSource _cts;
    private Uri _wsServerUri;
    private Uri _httpServerUri;

    public bool Connected;

    public PMHQContext(ContextCollection collection, BotKeystore keystore, BotAppInfo appInfo, BotDeviceInfo device,
        BotConfig config)
        : base(collection, keystore, appInfo, device)
    {
        _wsServerUri = new Uri($"ws://{config.PMHQ.Host}:{config.PMHQ.Port}/ws");
        _httpServerUri = new Uri($"http://{config.PMHQ.Host}:{config.PMHQ.Port}/");
    }

    public async Task GetSelfInfo()
    {
        var requestData = new { type = "call", data = new { func = "getSelfInfo" } };

        string json = JsonSerializer.Serialize(requestData);

        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] responseBytes = await Http.PostAsync(_httpServerUri.ToString(), payload, "application/json");
        string responseJson = Encoding.UTF8.GetString(responseBytes);
        var response = JsonSerializer.Deserialize<GetSelfInfoResponse>(responseJson);
        if (response?.Code == 0)
        {
            Collection.Keystore.Uin = uint.Parse(response.Data.Result.Uin);
            Collection.Keystore.Uid = response.Data.Result.Uid;
            var events = await Collection.Business.SendEvent(FetchUserInfoEvent.Create(Collection.Keystore.Uin));
            if (events.Count != 0 && events[0] is FetchUserInfoEvent { } @event)
            {
                Collection.Keystore.Info = new BotKeystore.BotInfo
                {
                    Name = @event.UserInfo.Nickname,
                    Age = (byte)@event.UserInfo.Age,
                    Gender = (byte)@event.UserInfo.Gender
                };
            }
            else
            {
                Collection.Log.LogWarning(Tag, "Get BotInfo Failed");
            }
        }
    }

    public void Start()
    {
        Connect().GetAwaiter().GetResult();
        ScheduleReconnect();
    }

    public async Task<bool> Connect()
    {
        if (Connected) return true;
        Collection.Log.LogInfo(Tag, "WS Connecting...");
        try
        {
            _webSocket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            await _webSocket.ConnectAsync(_wsServerUri, _cts.Token);
            Connected = true;
            _ = StartReceiveLoop();
            Collection.Log.LogInfo(Tag, "WS Connect Success");
            await GetSelfInfo();
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
                        if (message != null && message.Data.PayloadHex != null)
                        {
                            uint.TryParse(message.Data.Echo, out uint echo);
                            var packet = new SsoPacket(12, message.Data.Command, echo,
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
        catch (WebSocketException ex)
        {
            if (_webSocket.State != WebSocketState.Open)
            {
                Collection.Log.LogFatal(Tag, "WS Server error: " + ex.Message);
                Connected = false;
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
        var payload = new
        {
            type = "send",
            data = new { echo = packet.Sequence.ToString(), cmd = packet.Command, pb = packet.Payload.Hex() }
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
    }

    private void ScheduleReconnect()
    {
        Collection.Scheduler.Interval("WS Reconnect", 5 * 1000, async () =>
        {
            await Connect();
        });
    }

    public void Dispose()
    {
        Disconnect();
        _webSocket.Dispose();
        _cts.Dispose();
    }
}