using System.Text.Json.Nodes;
using DiscordRPC.IO;
using DiscordRPC.Logging;

namespace AppleMusicPresence.Core;

public sealed record DiscordActivityAcknowledgement(
    string? Name, string? Title, string? Artist, string? ArtworkImage, string? ArtworkText);

/// <summary>
/// Bridges two gaps in DiscordRichPresence 1.6.1 without modifying Discord's activity:
/// its serializer predates activity.name, and Assets.Merge throws on absent image keys.
/// Only the copy of the acknowledgment passed to that library has assets removed;
/// the original Discord acknowledgment remains available for verification.
/// </summary>
public sealed class DiscordCompatibilityPipe(INamedPipeClient inner) : INamedPipeClient
{
    public DiscordActivityAcknowledgement? LastActivity { get; private set; }
    public ILogger Logger { get => inner.Logger; set => inner.Logger = value; }
    public bool IsConnected => inner.IsConnected;
    [Obsolete("The connected pipe is not necessary information.")]
    public int ConnectedPipe => -1;
    public bool Connect(int pipe) => inner.Connect(pipe);

    public bool ReadFrame(out PipeFrame frame)
    {
        if (!inner.ReadFrame(out frame)) return false;
        if (frame.Opcode != Opcode.Frame) return true;
        var payload = JsonNode.Parse(frame.Message)?.AsObject();
        if (payload?["cmd"]?.GetValue<string>() != "SET_ACTIVITY" ||
            payload["evt"]?.GetValue<string>() == "ERROR") return true;

        var activity = payload["data"] as JsonObject;
        var assets = activity?["assets"] as JsonObject;
        LastActivity = activity is null ? null : new(
            activity["name"]?.GetValue<string>(), activity["details"]?.GetValue<string>(),
            activity["state"]?.GetValue<string>(), assets?["large_image"]?.GetValue<string>(),
            assets?["large_text"]?.GetValue<string>());

        if (assets is not null)
        {
            // Assets.Merge dereferences both image keys even when Discord omits them.
            // Never add a dummy image to the user's activity to work around that bug.
            activity!.Remove("assets");
            frame.Message = payload.ToJsonString();
        }
        return true;
    }

    public bool WriteFrame(PipeFrame frame)
    {
        if (frame.Opcode == Opcode.Frame)
        {
            var payload = JsonNode.Parse(frame.Message)?.AsObject();
            if (payload?["cmd"]?.GetValue<string>() == "SET_ACTIVITY" &&
                payload["args"]?["activity"] is JsonObject activity &&
                activity["type"]?.GetValue<int>() == 2)
            {
                activity["name"] = activity["state"]?.DeepClone();
                frame.Message = payload.ToJsonString();
            }
        }
        return inner.WriteFrame(frame);
    }

    public void Close() => inner.Close();
    public void Dispose() => inner.Dispose();
}
