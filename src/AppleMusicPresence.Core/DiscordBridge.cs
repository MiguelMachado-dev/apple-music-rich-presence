using DiscordRPC;
using DiscordRPC.IO;

namespace AppleMusicPresence.Core;

public sealed class DiscordBridge(Func<INamedPipeClient>? pipeFactory = null) : IDisposable
{
    private DiscordRpcClient? _client;
    private DiscordCompatibilityPipe? _pipe;
    private string _applicationId = "";
    private PresenceData? _lastPresence;
    private bool _retryPresence;
    private DateTimeOffset _retryAfter;
    private bool _clearAcknowledged;
    public bool IsReady { get; private set; }
    public bool HasPublishedPresence { get; private set; }
    public string? Error { get; private set; }
    public DiscordActivityAcknowledgement? AcknowledgedActivity => _pipe?.LastActivity;

    public void Configure(string applicationId)
    {
        if (_applicationId == applicationId) return;
        Dispose();
        _applicationId = applicationId;
        Error = null;
        if (!AppSettings.IsValidApplicationId(applicationId)) return;
        _pipe = new DiscordCompatibilityPipe(pipeFactory?.Invoke() ?? new ManagedNamedPipeClient());
        _client = new DiscordRpcClient(applicationId, autoEvents: false, client: _pipe)
        { SkipIdenticalPresence = false };
        _client.OnReady += (_, _) =>
        {
            IsReady = true;
            Error = null;
            // Replay our desired cover; the library's acknowledged copy omits assets.
            if (_lastPresence is not null) _client.SetPresence(_lastPresence.ToRpc());
        };
        _client.OnClose += (_, message) =>
        {
            IsReady = false;
            HasPublishedPresence = false;
            if (message.Code != 0) Error = $"Discord recusou a conexão ({message.Code}): {message.Reason}. Confira o Application ID.";
        };
        _client.OnConnectionFailed += (_, _) => { IsReady = false; HasPublishedPresence = false; };
        _client.OnError += (_, message) =>
        {
            Error = $"Discord: {message.Message} ({message.Code}). Confira o Application ID.";
            HasPublishedPresence = false;
            // Permit retry after transient Discord errors.
            _retryPresence = true;
            _retryAfter = DateTimeOffset.UtcNow.AddSeconds(15);
        };
        _client.OnPresenceUpdate += (_, message) =>
        {
            if (message.Presence is null) _clearAcknowledged = true;
            HasPublishedPresence = message.Presence is not null && _lastPresence is not null;
            Error = null;
        };
        _client.Initialize();
    }

    public void Pump() => _client?.Invoke();

    public void Publish(PresenceData? presence)
    {
        if (_client is null) return;
        if (presence is null)
        {
            if (_lastPresence is not null || _retryPresence) _client.ClearPresence();
            _lastPresence = null;
            _retryPresence = false;
            HasPublishedPresence = false;
        }
        else if ((!presence.EquivalentTo(_lastPresence) || _retryPresence) &&
                 (!_retryPresence || DateTimeOffset.UtcNow >= _retryAfter))
        {
            _client.SetPresence(presence.ToRpc());
            _lastPresence = presence;
            _retryPresence = false;
            HasPublishedPresence = false;
        }
    }

    public void Dispose()
    {
        if (_client is not null)
        {
            // The library's shutdown flag can stop its loop before a queued clear is
            // written. Clear while connected, briefly await the ack, then close IPC.
            if (IsReady)
            {
                _client.Invoke();
                _clearAcknowledged = false;
                _client.ClearPresence();
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                // RPC 1.6 polls once per second; allow one write and one read cycle.
                while (!_clearAcknowledged && elapsed.ElapsedMilliseconds < 2500)
                {
                    _client.Invoke();
                    if (!_clearAcknowledged) Thread.Sleep(10);
                }
            }
            _client.ShutdownOnly = false;
            _client.Dispose();
        }
        _client = null;
        _pipe = null;
        _applicationId = "";
        _lastPresence = null;
        _retryPresence = false;
        IsReady = false;
        HasPublishedPresence = false;
    }
}
