using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DiscordRPC.IO;
using DiscordRPC.Logging;

namespace AppleMusicPresence.Specs;

internal sealed class FakeDiscordPipe : INamedPipeClient
{
    private readonly ConcurrentQueue<PipeFrame> _incoming = new();
    internal readonly ConcurrentQueue<JsonObject> Commands = new();
    internal int Handshakes;
    public ILogger Logger { get; set; } = new NullLogger();
    public bool IsConnected { get; private set; }
    public int ConnectedPipe => 0;
    public bool Connect(int pipe) { IsConnected = true; return true; }
    public bool ReadFrame(out PipeFrame frame) => _incoming.TryDequeue(out frame);

    public bool WriteFrame(PipeFrame frame)
    {
        if (frame.Opcode == Opcode.Handshake)
        {
            Interlocked.Increment(ref Handshakes);
            Queue("""
                {"cmd":"DISPATCH","evt":"READY","data":{"v":1,"config":{"cdn_host":"cdn.discordapp.com","api_endpoint":"//discord.com/api","environment":"production"},"user":{"id":"123456789012345678","username":"Test","discriminator":"0001","avatar":null}}}
                """);
        }
        else if (frame.Opcode == Opcode.Frame)
        {
            var command = JsonNode.Parse(frame.Message)!.AsObject();
            Commands.Enqueue(command);
            if (command["cmd"]?.GetValue<string>() == "SET_ACTIVITY")
            {
                var activity = command["args"]?["activity"]?.DeepClone();
                if (activity is JsonObject data)
                {
                    data["application_id"] = "123456789012345678";
                    data["name"] ??= "Apple Music";
                }
                Queue(new JsonObject { ["cmd"] = "SET_ACTIVITY", ["nonce"] = command["nonce"]?.DeepClone(), ["data"] = activity }.ToJsonString());
            }
        }
        return true;
    }

    internal void Disconnect()
    {
        var frame = new PipeFrame { Opcode = Opcode.Close };
        frame.Message = "{\"code\":1000,\"message\":\"test disconnect\"}";
        _incoming.Enqueue(frame);
    }

    internal void RejectPresence() => Queue("{\"cmd\":\"SET_ACTIVITY\",\"evt\":\"ERROR\",\"nonce\":\"1\",\"data\":{\"code\":4000,\"message\":\"invalid activity\"}}");

    private void Queue(string json)
    {
        var frame = new PipeFrame { Opcode = Opcode.Frame };
        frame.Message = json;
        _incoming.Enqueue(frame);
    }
    public void Close() => IsConnected = false;
    public void Dispose() => Close();
}
