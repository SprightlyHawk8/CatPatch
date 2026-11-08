using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Together;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;
using HarmonyLib;

namespace CatPatchspace.SprayFallback;

internal sealed class NetworkController : MonoBehaviour
{
    private const int ProtocolVersion = 2;
    private const ushort Probe = NetworkMessageIds.SprayFallbackProbe;
    private const ushort ProbeAck = NetworkMessageIds.SprayFallbackProbeAck;
    private const ushort ToHost = NetworkMessageIds.SprayFallbackToHost;
    private const ushort ToClient = NetworkMessageIds.SprayFallbackToClient;
    private const int MaxTransfers = 16;
    private const long TransferBudget = 64L * 1024 * 1024; // Don't commit to over 64MiB in transfers, this limits the maximum size a single image can have as well
    private const int DefaultFragmentSize = 16 * 1024; // 16KiB seems like a nice balance. These need to be small to be granular, but not too small to cause too much overhead
    private const int WindowFragments = 16; // Max potential fragments in-flight per image
    private const long PeerWindowBytes = 256 * 1024; // MP mod sets a 1MiB per-peer buffer for steam transport, stay _way_ below that
    private const long TotalWindowBytes = 5L * 1024 * 1024; // For sanity, don't exceed 5MiB in-flight total for all peers
    private const int FrameFragmentLimit = 32;
    private const float OfferIdleResend = 5;
    private const float ResendTimeout = 5;
    private const float TransferIdleTimeout = 30;
    private const float TransferTimeout = 60;
    private const int MaxPreparing = 2;
    private const int MaxServesPerPeer = 4;
    private const int MaxQueued = 64;
    private const int MaxQueuedPerPeer = MaxTransfers;
    private const float QueueExpiry = 15;
    private const float DownloadIdleTimeout = 30;
    private const float DownloadTimeout = 60;

    public static NetworkController Instance { get; private set; }
    internal SprayService Service { get; private set; }
    internal CancellationToken SessionToken => _session.Token;
    internal bool IsHost => !Net.IsRunning || Net.IsServer;
    internal int HostMaxSize { get; private set; }
    internal bool HostRequiresBackend { get; private set; }

    private CancellationTokenSource _session = new();
    private TaskCompletionSource<bool> _handshake = NewCompletion<bool>();
    private int _probeAttempts;
    private readonly HashSet<ushort> _peers = [];
    private readonly Dictionary<ushort, string> _peerSessions = [];
    private string _helloToken = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, Outgoing> _outgoing = [];
    private readonly Dictionary<string, Incoming> _incoming = [];
    private readonly Dictionary<string, Download> _downloads = [];
    private readonly Dictionary<string, Receipt> _receipts = [];
    private readonly HashSet<string> _preparing = [];
    private readonly List<Request> _queue = [];
    private bool _closed;
    private float _lastProbe = float.NegativeInfinity;
    private int _pumpRoundRobinRotation;
    private int _generation;
    private readonly Dictionary<ushort, PeerBudget> _budgets = new();
    private TransferRateLimit _allPackets = new(512, 1024);
    private TransferRateLimit _allOffers = new(64L * 1024 * 1024, 2L * 1024 * 1024);
    private TransferRateLimit _packetWarnings = new(4, 1);
    private TransferRateLimit _allRequests = new(16, 2);
    private readonly Dictionary<string, float> _placedImages = new();

    internal void MarkPlaced(string id)
    {
        if (!IsHost || !SprayImage.IsValidId(id)) return;
        if (!_placedImages.ContainsKey(id) && _placedImages.Count >= 256)
            _placedImages.Remove(_placedImages.OrderBy(p => p.Value).First().Key);
        _placedImages[id] = Time.realtimeSinceStartup;
    }
    internal void ClearPlaced() => _placedImages.Clear();
    internal bool WasPlaced(string id) => _placedImages.ContainsKey(id);

    private sealed class PeerBudget
    {
        internal readonly TransferRateLimit Packets = new(256, 512);
        internal readonly TransferRateLimit Controls = new(64, 32);
        internal readonly TransferRateLimit Bytes = new(2L * 1024 * 1024, 2L * 1024 * 1024);
        internal readonly TransferRateLimit Requests = new(8, 0.5);
        internal readonly TransferRateLimit Offers = new(2L * SprayImage.HardByteSizeLimit, SprayImage.HardByteSizeLimit / 30.0);
    }

    private bool AcceptPacket(ushort peer, NetDataReader reader, bool control = false)
    {
        if (reader.AvailableBytes > FragmentBuffer.MaxFragmentSize + 512) return false;
        if (!_budgets.TryGetValue(peer, out var budget))
        {
            if (_budgets.Count >= 256) return false;
            _budgets[peer] = budget = new PeerBudget();
        }
        float now = Time.realtimeSinceStartup;
        return budget.Packets.Take(1, now) && budget.Bytes.Take(reader.AvailableBytes, now)
            && (!control || budget.Controls.Take(1, now)) && _allPackets.Take(1, now);
    }

    private bool AcceptRequest(ushort peer, int offeredBytes = 0)
    {
        if (!_budgets.TryGetValue(peer, out var budget)) return false;
        float now = Time.realtimeSinceStartup;
        return budget.Requests.Take(1, now) && budget.Offers.Take(offeredBytes, now)
            && _allRequests.Take(1, now) && _allOffers.Take(offeredBytes, now);
    }

    private void PacketWarning(string message)
    {
        if (_packetWarnings.Take(1, Time.realtimeSinceStartup)) Plugin.Logger.LogWarning(message);
    }

    private enum Kind : byte
    {
        Offer, // I have this image, interested?
        Ack, // I am interested in this portion of it right now
        Fragment, // Here's a fragment from that portion
        Result, // Here's what happened with this transfer
        Get, // Give me this image
        Wait, // I'm busy, ask again later
        Cancel,
    }

    private sealed class Outgoing
    {
        public ushort Peer;
        public string Token;
        public SprayImage Image;
        public readonly int FragmentSize = DefaultFragmentSize;
        public bool Accepted;
        public int Acked; // Receiver has every fragment below this
        public int Limit; // Receiver allows fragments below this
        public int NextFragmentIndex;
        public float LastAck;
        public float OfferSent = float.NegativeInfinity;
        public float RetryAt = float.NegativeInfinity;
        public int Waits;
        public float Progress = Time.realtimeSinceStartup;
        public bool Stalled = false;
        public readonly float Started = Time.realtimeSinceStartup;
        public readonly TaskCompletionSource<bool> Completion = NewCompletion<bool>();

        public int Count => FragmentBuffer.CountFor(Image.Bytes.Length, FragmentSize);
        public int Offset(int index) => (int)Math.Min((long)index * FragmentSize, Image.Bytes.Length);
        public int FragmentLength(int index) => Offset(index + 1) - Offset(index);
        public long InFlightBytes => Offset(NextFragmentIndex) - Offset(Acked);

        public void Ack(int receive, int limit, float now)
        {
            receive = Mathf.Clamp(receive, 0, Count);
            limit = Mathf.Clamp(limit, 0, Count);
            if (!Accepted)
            {
                Accepted = true;
                LastAck = Progress = now;
            }

            // Acks may arrive out of order, so only ever move forward
            if (receive > Acked)
            {
                Acked = receive;
                LastAck = Progress = now;
                Stalled = false;
            }

            Limit = Math.Max(Limit, limit);
            NextFragmentIndex = Math.Max(NextFragmentIndex, Acked);
        }
    }

    private sealed class Incoming
    {
        public ushort Peer;
        public string Token;
        public string Id;
        public string Hash;
        public bool Backend;
        public bool Saving;
        public bool Canceled;
        public bool Preparing = true;
        public FragmentBuffer Buffer;
        public int Limit;
        public bool NeedsAck;
        public float Progress = Time.realtimeSinceStartup;
        public readonly float Started = Time.realtimeSinceStartup;
    }

    private sealed class Download
    {
        public string Id;
        public readonly float Started = Time.realtimeSinceStartup;
        public float LastActivity = Time.realtimeSinceStartup;
        public float NextGet = float.NegativeInfinity;
        public int Waits;
        public bool Receiving;
        public readonly TaskCompletionSource<SprayImage> Completion = NewCompletion<SprayImage>();
    }

    private sealed class Request
    {
        public ushort Peer;
        public string Token;
        public string Id;
        public int Size = -1;
        public float Seen = Time.realtimeSinceStartup;
    }

    private sealed class Receipt
    {
        public bool Success;
        public string Message;
        public readonly float Expires = Time.realtimeSinceStartup + 60;
    }

    private static TaskCompletionSource<T> NewCompletion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static string Key(ushort peer, string token)
    {
        return peer + ":" + token;
    }

    private static float Backoff(int waits)
    {
        float delay = Mathf.Min(4f, 0.25f * (1 << Mathf.Clamp(waits, 0, 5)));
        return delay * UnityEngine.Random.Range(0.75f, 1.25f);
    }

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        RegisterReceivers();
        Service = new SprayService(this);
        SceneManager.sceneLoaded += SceneLoaded;
        ScavPlayer.OnPlayerJoined += PlayerJoined;
        ScavPlayer.OnPlayerLeft += PlayerLeft;
        Plugin.Logger.LogInfo($"[Sprays] stage=fallback.init protocol={ProtocolVersion} send_enabled={CatPatchClientSettings.SendSprayFallback} relay_enabled={CatPatchServerSettings.AllowSprayFallback}");
    }

    private void PlayerJoined(ScavPlayer player)
    {
        EnsureHostSettingsSet();
        if (player != null && player.IsLocal)
            _lastProbe = float.NegativeInfinity;
    }

    private void PlayerLeft(ScavPlayer player)
    {
        if (player == null)
            return;
        if (player.IsLocal)
            ResetSession();
        else ForgetPeer(player.playerId.id);
    }

    private void ForgetPeer(ushort peer)
    {
        SprayFallbackLog.Verbose($"NetworkController: ForgetPeer: {peer}");
        _peers.Remove(peer);
        _peerSessions.Remove(peer);
        foreach (string key in _outgoing.Where(p => p.Value.Peer == peer).Select(p => p.Key).ToArray())
            FinishOutgoing(key, false);
        foreach (string key in _incoming.Where(p => p.Value.Peer == peer).Select(p => p.Key).ToArray())
            _incoming.Remove(key);
        foreach (string key in _preparing.Where(k => k.StartsWith(peer + ":", StringComparison.Ordinal)).ToArray())
            _preparing.Remove(key);
        _queue.RemoveAll(r => r.Peer == peer);
        foreach (string key in _receipts.Keys.Where(k => k.StartsWith(peer + ":", StringComparison.Ordinal)).ToArray())
            _receipts.Remove(key);
    }

    private bool SameSession(int generation, ushort peer, string session)
    {
        return generation == _generation && (!IsHost || _peerSessions.TryGetValue(peer, out string current) && current == session);
    }

    private void SceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!Net.IsRunning)
            ResetSession();
        Service.ClearScene();
    }

    private static void RegisterReceivers()
    {
        CatPatchNetwork.RegisterServer(Probe, ReceiveProbe);
        CatPatchNetwork.RegisterClient(ProbeAck, ReceiveProbeAck);
        CatPatchNetwork.RegisterServer(ToHost, ReceiveHost);
        CatPatchNetwork.RegisterClient(ToClient, ReceiveClient);
    }

    private void EnsureHostSettingsSet()
    {
        if (!IsHost)
            return;

        HostMaxSize = CatPatchServerSettings.AllowSprayFallback ? CatPatchServerSettings.MaxSprayFallbackBytes : 0;
        HostRequiresBackend = false;
    }

    private void Update()
    {
        if (_closed)
            return;

        EnsureHostSettingsSet();
        CancelDisabledTransfers();

        float now = Time.realtimeSinceStartup;
        if (!IsHost && _handshake.Task.IsCompleted && !_handshake.Task.Result && now - _lastProbe > 10)
        {
            _handshake = NewCompletion<bool>();
            _probeAttempts = 0;
        }
        if (!IsHost
            && ScavPlayer.LocalPlayer != null
            && !_handshake.Task.IsCompleted
            && now - _lastProbe > 1)
        {
            _lastProbe = now;
            if (_probeAttempts++ >= 5)
            {
                SprayFallbackLog.Verbose($"NetworkController: Update: Probe failed");
                _handshake.TrySetResult(false);
            }
            else
            {
                SprayFallbackLog.Verbose($"NetworkController: Update: Sending probe");
                var writer = CatPatchNetwork.CreateWriter(Probe);
                writer.Put(ProtocolVersion);
                writer.Put(Plugin.ModVersion);
                writer.Put(_helloToken);
                SendRaw(0, writer);
            }
        }

        if (_queue.Count > 0)
        {
            _queue.RemoveAll(r => now - r.Seen > QueueExpiry);
            PumpQueue();
        }

        foreach (var (key, item) in _outgoing.ToArray())
        {
            if (now - item.Progress > TransferIdleTimeout || now - item.Started > TransferTimeout)
            {
                SprayFallbackLog.Verbose($"NetworkController: Update: Outgoing: timed out id={item.Image.Id}");
                FinishOutgoing(key, false);
                continue;
            }

            if (!item.Accepted)
            {
                if (now >= item.RetryAt && now - item.OfferSent >= OfferIdleResend)
                {
                    item.OfferSent = now;
                    SendOffer(item);
                }
            }
            else if (item.Acked < item.NextFragmentIndex && now - item.LastAck > ResendTimeout)
            {
                if (item.Stalled)
                    continue;
                SprayFallbackLog.Verbose($"NetworkController: Update: Outgoing: No acks, resending from {item.Acked} id={item.Image.Id}");
                // Rewind the state by pretending we didn't send these fragments
                item.NextFragmentIndex = item.Acked;
                item.LastAck = now;
                // The unacked fragments we sent could still be in the buffer, taking up space
                // Only attempt to resend once, otherwise we might fill up the entire 1MiB buffer
                // If we still get no Acks, then the whole transfer will time out due to being idle
                item.Stalled = true;
            }
        }

        PumpFragments();

        foreach (var incoming in _incoming.Values)
        {
            if (incoming.NeedsAck && !incoming.Preparing && !incoming.Saving)
                SendAck(incoming);
        }

        foreach (var (key, item) in _incoming.ToArray())
        {
            if ((now - item.Progress > TransferIdleTimeout || now - item.Started > TransferTimeout) && !item.Saving)
            {
                SprayFallbackLog.Verbose($"NetworkController: Update: Incoming: timed out id={key}");
                _incoming.Remove(key);
                if (!IsHost && _downloads.TryGetValue(item.Token, out var download))
                {
                    _downloads.Remove(item.Token);
                    download.Completion.TrySetResult(null);
                }

                Result(item.Peer, item.Token, false, "Image transfer timed out.");
            }
        }

        foreach (var (key, item) in _downloads.ToArray())
        {
            if (!item.Receiving && now - item.LastActivity > DownloadIdleTimeout || now - item.Started > DownloadTimeout)
            {
                SprayFallbackLog.Verbose($"NetworkController: Update: Download: timed out id={key}");
                _downloads.Remove(key);
                _incoming.Remove(Key(0, key));
                item.Completion.TrySetResult(null);
                continue;
            }

            if (!item.Receiving && now >= item.NextGet)
            {
                SprayFallbackLog.Verbose($"NetworkController: Update: Download: Sending get for id={item.Id}");
                item.NextGet = now + 2;
                Send(0, Kind.Get, w =>
                {
                    w.Put(key);
                    w.Put(item.Id);
                });
            }
        }

        foreach (string key in _receipts.Where(p => p.Value.Expires < now).Select(p => p.Key).ToArray())
            _receipts.Remove(key);
    }

    internal bool HasIncoming(string id) => _incoming.Values.Any(t => t.Id == id);

    // This preference governs personal uploads/downloads, never the host's relay for other players.
    private void CancelDisabledTransfers()
    {
        if (IsHost)
        {
            if (CatPatchServerSettings.AllowSprayFallback)
                return;
            foreach (var item in _outgoing.ToArray())
            {
                Result(item.Value.Peer, item.Value.Token, false, "Lobby spray fallback was disabled by the host.");
                FinishOutgoing(item.Key, false);
            }
            foreach (var item in _incoming.Values.ToArray())
                Result(item.Peer, item.Token, false, "Lobby spray fallback was disabled by the host.");
            foreach (var item in _queue.ToArray())
                Result(item.Peer, item.Token, false, "Lobby spray fallback was disabled by the host.");
            _incoming.Clear();
            _queue.Clear();
            return;
        }

        if (!CatPatchClientSettings.SendSprayFallback)
        {
            foreach (var item in _outgoing.ToArray())
            {
                Send(0, Kind.Cancel, w => w.Put(item.Value.Token));
                FinishOutgoing(item.Key, false);
            }
        }
        if (!CatPatchClientSettings.ShowSprays)
        {
            foreach (var item in _downloads.ToArray())
            {
                Send(0, Kind.Cancel, w => w.Put(item.Key));
                _incoming.Remove(Key(0, item.Key));
                _downloads.Remove(item.Key);
                item.Value.Completion.TrySetResult(null);
            }
        }
    }

    internal async Task<bool> EnsureHostAsync()
    {
        if (IsHost)
        {
            EnsureHostSettingsSet();
            return HostMaxSize > 0;
        }

        var token = SessionToken;
        Task<bool> handshake = _handshake.Task;

        await Task.WhenAny(handshake, Task.Delay(5000, token));
        token.ThrowIfCancellationRequested();

        return handshake.IsCompleted && handshake.Result;
    }

    internal async Task<bool> UploadAsync(SprayImage image)
    {
        SprayFallbackLog.Verbose($"NetworkController: UploadAsync id={image?.Id}");
        var token = SessionToken;
        if (!CatPatchClientSettings.SendSprayFallback || !await EnsureHostAsync())
            return false;
        token.ThrowIfCancellationRequested();

        if (!CatPatchClientSettings.SendSprayFallback)
            return false;

        if (image == null
            || image.Bytes.Length > HostMaxSize
            || HostRequiresBackend && !image.BackendUploaded)
            return false;

        if (IsHost)
        {
            SprayFallbackLog.Verbose($"NetworkController: UploadAsync: Storing on host");
            var stored = await Service.StoreAsync(image);
            token.ThrowIfCancellationRequested();

            if (!await Service.ContainsAsync(stored.Id, stored.Bytes.Length))
                return false;
            token.ThrowIfCancellationRequested();

            SprayFallbackLog.Verbose($"NetworkController: UploadAsync: Announcing stored image");
            Announce(stored);
            return true;
        }

        if (!CanSend(image.Bytes.Length))
            return false;
        var transfer = new Outgoing { Peer = 0, Token = Guid.NewGuid().ToString("N"), Image = image };
        _outgoing.Add(Key(0, transfer.Token), transfer);
        SprayFallbackLog.Verbose($"NetworkController: UploadAsync: Awaiting upload");
        return await transfer.Completion.Task;
    }

    internal async Task<SprayImage> DownloadAsync(string id)
    {
        SprayFallbackLog.Verbose($"NetworkController: DownloadAsync id={id}");
        var token = SessionToken;
        if (!CatPatchClientSettings.ShowSprays || !await EnsureHostAsync())
            return null;
        token.ThrowIfCancellationRequested();

        if (!CatPatchClientSettings.ShowSprays)
            return null;

        if (IsHost)
        {
            SprayFallbackLog.Verbose($"NetworkController: DownloadAsync: Reading from host...");
            return await Service.ReadAsync(id);
        }

        var existing = _downloads.Values.FirstOrDefault(d => d.Id == id);
        if (existing != null)
        {
            SprayFallbackLog.Verbose($"NetworkController: DownloadAsync: Awaiting existing download");
            return await existing.Completion.Task;
        }

        if (_downloads.Count >= MaxTransfers)
        {
            SprayFallbackLog.Verbose($"NetworkController: DownloadAsync: Too many downloads!");
            return null;
        }

        var request = Guid.NewGuid().ToString("N");
        var download = new Download { Id = id };
        _downloads[request] = download;
        SprayFallbackLog.Verbose($"NetworkController: DownloadAsync: Awaiting download");
        return await download.Completion.Task;
    }

    private bool CanSend(int size)
    {
        return _outgoing.Count < MaxTransfers && _outgoing.Values.Sum(t => (long)t.Image.Bytes.Length) + size <= TransferBudget;
    }

    private static void ReceiveProbe(knetid peer, ref NetDataReader reader)
    {
        SprayFallbackLog.Verbose($"ReceiveProbe: Received probe from {peer}");
        if (Instance == null || !Instance.AcceptPacket(peer.id, reader, true))
            return;

        try
        {
            int protocol = reader.GetInt();
            string version = reader.GetString(64);
            string session = reader.GetString(32);

            if (protocol != ProtocolVersion)
            {
                return;
            }

            if (string.IsNullOrEmpty(version)
                || session.Length != 32
                || !SprayImage.IsValidId(session))
            {
                SprayFallbackLog.Verbose($"ReceiveProbe: Go away, {peer}");
                return;
            }

            if (Instance._peerSessions.TryGetValue(peer.id, out string old) && old != session)
            {
                SprayFallbackLog.Verbose($"ReceiveProbe: Forgetting {peer}");
                Instance.ForgetPeer(peer.id);
            }

            Instance._peers.Add(peer.id);
            Instance._peerSessions[peer.id] = session;
            Instance.EnsureHostSettingsSet();
            var writer = CatPatchNetwork.CreateWriter(ProbeAck);
            writer.Put(ProtocolVersion);
            writer.Put(Plugin.ModVersion);
            writer.Put(session);
            writer.Put(Instance.HostMaxSize);
            writer.Put(Instance.HostRequiresBackend);
            Instance.SendRaw(peer.id, writer);
            SprayFallbackLog.Verbose($"ReceiveProbe: Sent ack to {peer} with token {session}");
        }
        catch (Exception ex)
        {
            Instance.PacketWarning("Invalid spray handshake: " + ex.Message);
        }
    }

    private static void ReceiveProbeAck(knetid sender, ref NetDataReader reader)
    {
        SprayFallbackLog.Verbose($"ReceiveProbeAck");
        if (Instance == null || !Instance.AcceptPacket(0, reader, true))
            return;

        try
        {
            var protocol = reader.GetInt();
            var modVersion = reader.GetString(64);

            if (protocol != ProtocolVersion)
            {
                var message = $"FullResSprays disabled: Your version {Plugin.ModVersion} is incompatible with host's {modVersion}";
                Con.con?.LogToConsole(message);
                SprayService.Alert(message);
                return;
            }

            if (reader.GetString(32) != Instance._helloToken)
            {
                SprayFallbackLog.Verbose($"ReceiveProbeAck: Not my token. Expected: {Instance._helloToken}");
                return;
            }

            int limit = reader.GetInt();
            bool require = reader.GetBool();
            if (limit < 0 || limit > SprayImage.HardByteSizeLimit)
                return;

            Instance.HostMaxSize = limit;
            Instance.HostRequiresBackend = require;
            Instance._handshake.TrySetResult(limit > 0);
            Plugin.Logger.LogInfo($"[Sprays] stage=fallback.host protocol={protocol} max_bytes={limit}");
            SprayFallbackLog.Verbose($"ReceiveProbeAck: Complete with maxSize={limit} and requiresBackend={require}");
        }
        catch (Exception ex)
        {
            Instance.PacketWarning("Invalid spray handshake response: " + ex.Message);
        }
    }

    private static void ReceiveHost(knetid peer, ref NetDataReader reader)
    {
        if (Instance != null && Instance._peers.Contains(peer.id) && Instance.AcceptPacket(peer.id, reader))
            Instance.Receive(peer.id, ref reader);
    }

    private static void ReceiveClient(knetid sender, ref NetDataReader reader)
    {
        if (Instance != null && Instance._handshake.Task.IsCompletedSuccessfully && Instance._handshake.Task.Result
            && Instance.AcceptPacket(0, reader))
            Instance.Receive(0, ref reader);
    }

    private void Receive(ushort peer, ref NetDataReader reader)
    {
        try
        {
            EnsureHostSettingsSet();
            var kind = (Kind)reader.GetByte();
            if (kind != Kind.Fragment && (!_budgets.TryGetValue(peer, out var controlBudget)
                || !controlBudget.Controls.Take(1, Time.realtimeSinceStartup))) return;
            string session = reader.GetString(32);
            if (IsHost
                    ? !_peerSessions.TryGetValue(peer, out string expectedSession) || expectedSession != session
                    : session != _helloToken)
            {
                SprayFallbackLog.Verbose($"NetworkController: Receive: Bad session");
                return;
            }

            string token = reader.GetString(32);
            if (token.Length != 32 || !SprayImage.IsValidId(token))
                throw new ArgumentException("Invalid transfer token.");

            string key = Key(peer, token);
            if (IsHost && !CatPatchServerSettings.AllowSprayFallback && kind != Kind.Cancel)
            {
                Result(peer, token, false, "Lobby spray fallback is disabled by the host.");
                return;
            }
            SprayFallbackLog.Verbose($"NetworkController: Receive: TRANSFER KEY {key}");

            switch (kind)
            {
                case Kind.Cancel:
                    if (_incoming.TryGetValue(key, out var canceled) && canceled.Saving)
                        canceled.Canceled = true; // Keep its allocation charged until the worker exits.
                    else _incoming.Remove(key);
                    _preparing.Remove(key);
                    _queue.RemoveAll(r => r.Peer == peer && r.Token == token);
                    FinishOutgoing(key, false);
                    break;
                case Kind.Get:
                    string getId = ReadId(reader);
                    SprayFallbackLog.Verbose($"NetworkController: Receive: Get for {getId}");
                    if (IsHost)
                        Serve(peer, token, getId);
                    break;
                case Kind.Offer:
                    string id = ReadId(reader);
                    int size = reader.GetInt();
                    string hash = reader.GetString(64);
                    bool backend = reader.GetBool();
                    int fragmentSize = reader.GetInt();
                    SprayFallbackLog.Verbose($"NetworkController: Receive: Offer for {id} of size {size / 1024}KiB");
                    Offer(peer, token, id, size, hash, backend, fragmentSize);
                    break;
                case Kind.Ack:
                    int received = reader.GetInt();
                    int limit = reader.GetInt();
                    SprayFallbackLog.Verbose($"NetworkController: Receive: Ack received={received} limit={limit}");
                    if (_outgoing.TryGetValue(key, out var outgoing))
                        outgoing.Ack(received, limit, Time.realtimeSinceStartup);
                    break;
                case Kind.Fragment:
                    int index = reader.GetInt();
                    SprayFallbackLog.Verbose($"NetworkController: Receive: Fragment of index={index} size={reader.AvailableBytes / 1024}KiB");

                    if (_receipts.TryGetValue(key, out var receipt))
                    {
                        SprayFallbackLog.Verbose($"NetworkController: Receive: Fragment: Have receipt");
                        Result(peer, token, receipt.Success, receipt.Message);
                        break;
                    }

                    if (!_incoming.TryGetValue(key, out var incoming)
                        || incoming.Saving
                        || incoming.Preparing)
                    {
                        SprayFallbackLog.Verbose($"NetworkController: Receive: Fragment: No prior request or bad timing (null={incoming == null})");
                        break;
                    }

                    if (index >= incoming.Limit)
                    {
                        SprayFallbackLog.Verbose($"NetworkController: Receive: Fragment: Outside granted credit ({incoming.Limit})");
                        break;
                    }

                    if (incoming.Buffer.Add(index, reader))
                    {
                        incoming.Progress = Time.realtimeSinceStartup;
                        incoming.Limit = Math.Min(incoming.Buffer.Count, incoming.Buffer.ToReceiveIndex + WindowFragments);
                    }

                    incoming.NeedsAck = true;

                    if (incoming.Buffer.IsComplete)
                    {
                        SprayFallbackLog.Verbose($"NetworkController: Receive: Fragment: Completing");
                        // Ack now to stop the sender from resending
                        SendAck(incoming);
                        CompleteIncoming(incoming);
                    }

                    break;
                case Kind.Wait:
                    SprayFallbackLog.Verbose($"NetworkController: Receive: Wait");
                    float waitNow = Time.realtimeSinceStartup;
                    if (_outgoing.TryGetValue(key, out var waiting) && !waiting.Accepted)
                    {
                        waiting.Progress = waitNow;
                        waiting.OfferSent = float.NegativeInfinity;
                        waiting.RetryAt = waitNow + Backoff(waiting.Waits++);
                    }

                    if (!IsHost && _downloads.TryGetValue(token, out var pending) && !pending.Receiving)
                    {
                        pending.LastActivity = waitNow;
                        pending.NextGet = waitNow + Backoff(pending.Waits++);
                    }

                    break;
                case Kind.Result:
                    bool success = reader.GetBool();
                    string message = reader.GetString(256);
                    if (!success)
                        PacketWarning($"[Sprays] stage=fallback.reject peer={peer} reason={message}");
                    SprayFallbackLog.Verbose($"NetworkController: Receive: Result: {success}, {message}");
                    FinishOutgoing(key, success);
                    if (!IsHost && _downloads.TryGetValue(token, out var download) && !success)
                    {
                        _downloads.Remove(token);
                        _incoming.Remove(key);
                        download.Completion.TrySetResult(null);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            PacketWarning("Invalid spray packet: " + ex.Message);
        }
    }

    private static string ReadId(NetDataReader reader)
    {
        string id = reader.GetString(128);
        if (!SprayImage.IsValidId(id))
            throw new ArgumentException("Invalid spray ID.");
        return id;
    }

    private async void Offer(ushort peer, string token, string id, int size, string hash, bool backend, int fragmentSize)
    {
        SprayFallbackLog.Verbose($"NetworkController: Offer: peer={peer}, id={id}, size={size / 1024}KiB");
        string key = Key(peer, token);
        int generation = _generation;
        _peerSessions.TryGetValue(peer, out string session);
        try
        {
            if (_receipts.TryGetValue(key, out var receipt))
            {
                SprayFallbackLog.Verbose($"NetworkController: Offer: Have receipt");
                Result(peer, token, receipt.Success, receipt.Message);
                return;
            }

            if (_incoming.TryGetValue(key, out var existing))
            {
                SprayFallbackLog.Verbose($"NetworkController: Offer: existing...");
                if (!existing.Saving && !existing.Preparing)
                    SendAck(existing); // The sender missed our Ack
                return;
            }

            if (_preparing.Contains(key))
                return;
            if (!IsHost && (!_downloads.TryGetValue(token, out var expected) || expected.Id != id))
                return;
            if (!IsHost)
                _downloads[token].LastActivity = Time.realtimeSinceStartup;

            if (size <= 0
                || size > SprayImage.HardByteSizeLimit
                || fragmentSize is < FragmentBuffer.MinFragmentSize or > FragmentBuffer.MaxFragmentSize
                || hash.Length != 64
                || !SprayImage.IsValidId(hash)
                || id.StartsWith("custom-", StringComparison.Ordinal) && (id != "custom-" + hash || backend))
            {
                throw new ArgumentException("Invalid image metadata.");
            }

            if (IsHost && (size > HostMaxSize || HostRequiresBackend && !backend
                || CatPatchHostModerationMenu.GetHostSprayMuteRemainingSeconds(peer) > 0)
                || !IsHost && !CatPatchClientSettings.ShowSprays)
            {
                SprayFallbackLog.Verbose($"NetworkController: Offer: Rejecting");
                Result(peer, token, false, "Host image policy rejected the upload.");
                return;
            }

            if (IsHost && !AcceptRequest(peer, size))
            {
                Result(peer, token, false, "Spray transfer rate limit exceeded. Try again later.");
                return;
            }

            if (_preparing.Count >= MaxTransfers
                || _incoming.Count >= MaxTransfers
                || _incoming.Values.Count(t => t.Peer == peer) >= MaxServesPerPeer
                || _incoming.Values.Sum(t => (long)t.Buffer.Bytes.Length) + size > TransferBudget)
            {
                SprayFallbackLog.Verbose($"NetworkController: Offer: Busy, asking to wait");
                SendWait(peer, token);
                return;
            }

            var incoming = new Incoming
            {
                Peer = peer,
                Token = token,
                Id = id,
                Hash = hash,
                Backend = backend,
                Buffer = new FragmentBuffer(size, fragmentSize)
            };
            _incoming[key] = incoming;

            SprayFallbackLog.Verbose($"NetworkController: Offer: Reading cache");
            var cached = await Service.ReadAsync(id);
            if (!SameSession(generation, peer, session) || !_incoming.ContainsKey(key))
                return;

            if (cached != null && cached.Hash != hash)
                throw new ArgumentException("Conflicting content for an existing spray ID.");
            if (IsHost && cached != null && cached.Hash == hash)
            {
                SprayFallbackLog.Verbose($"NetworkController: Offer: Already available, sending cache update");
                _incoming.Remove(key);
                RememberReceipt(peer, token, true, "Already available.");
                Announce(cached);
                return;
            }

            if (!IsHost)
                _downloads[token].Receiving = true;
            incoming.Preparing = false;
            incoming.Limit = Math.Min(incoming.Buffer.Count, WindowFragments);
            SendAck(incoming);
            SprayFallbackLog.Verbose($"NetworkController: Offer: Accepted with limit={incoming.Limit}");
        }
        catch (Exception ex)
        {
            if (!SameSession(generation, peer, session))
                return;
            _incoming.Remove(key);
            Result(peer, token, false, ex.Message);
        }
    }

    private async void CompleteIncoming(Incoming incoming)
    {
        SprayFallbackLog.Verbose($"NetworkController: CompleteIncoming: {incoming.Id}, {incoming.Buffer.Bytes.Length / 1024}KiB");
        incoming.Saving = true;
        string key = Key(incoming.Peer, incoming.Token);
        int generation = _generation;
        _peerSessions.TryGetValue(incoming.Peer, out string session);
        try
        {
            SprayFallbackLog.Verbose($"NetworkController: CompleteIncoming: Creating image");
            var image = await Task.Run(() =>
            {
                var value = new SprayImage(incoming.Id, incoming.Buffer.Bytes, incoming.Backend);
                if (value.Hash != incoming.Hash)
                    throw new ArgumentException("Image checksum mismatch.");
                return value;
            });
            if (!SameSession(generation, incoming.Peer, session))
                return;

            if (incoming.Canceled || !_incoming.TryGetValue(key, out var current) || current != incoming)
                return;
            if (IsHost)
                image = await Service.VerifyRelayAsync(image, SessionToken);
            if (!SameSession(generation, incoming.Peer, session) || incoming.Canceled)
                return;
            SprayFallbackLog.Verbose($"NetworkController: CompleteIncoming: Saving image");
            var stored = await Service.StoreAsync(image);
            if (!SameSession(generation, incoming.Peer, session) || !_incoming.ContainsKey(key))
                return;

            if (IsHost && !await Service.ContainsAsync(stored.Id, stored.Bytes.Length))
                throw new InvalidOperationException("Image does not fit the host caches.");
            if (!SameSession(generation, incoming.Peer, session))
                return;

            SprayFallbackLog.Verbose($"NetworkController: CompleteIncoming: Storing receipt");
            _incoming.Remove(key);
            RememberReceipt(incoming.Peer, incoming.Token, true, "Stored.");
            if (IsHost)
            {
                SprayFallbackLog.Verbose($"NetworkController: CompleteIncoming: Announcing");
                Announce(stored);
            }
            else if (_downloads.TryGetValue(incoming.Token, out var download))
            {
                SprayFallbackLog.Verbose($"NetworkController: CompleteIncoming: Remove from downloads");
                _downloads.Remove(incoming.Token);
                download.Completion.TrySetResult(stored);
            }
        }
        catch (Exception ex)
        {
            if (!SameSession(generation, incoming.Peer, session))
                return;

            _incoming.Remove(key);
            RememberReceipt(incoming.Peer, incoming.Token, false, ex.Message);
            if (_downloads.TryGetValue(incoming.Token, out var download))
            {
                _downloads.Remove(incoming.Token);
                download.Completion.TrySetResult(null);
            }
        }
        finally
        {
            if (SameSession(generation, incoming.Peer, session)
                && _incoming.TryGetValue(key, out var current) && current == incoming)
                _incoming.Remove(key);
        }
    }

    private void Serve(ushort peer, string token, string id)
    {
        SprayFallbackLog.Verbose($"NetworkController: Serve: peer={peer}, id={id}");
        if (!WasPlaced(id))
        {
            Result(peer, token, false, "Image was not recently placed in this lobby.");
            return;
        }
        string key = Key(peer, token);
        if (_outgoing.ContainsKey(key) || _preparing.Contains(key))
            return;

        var request = _queue.Find(r => r.Peer == peer && r.Token == token);
        if (request == null)
        {
            if (!AcceptRequest(peer))
            {
                Result(peer, token, false, "Spray request rate limit exceeded. Try again later.");
                return;
            }
            if (_queue.Count >= MaxQueued || _queue.Count(r => r.Peer == peer) >= MaxQueuedPerPeer)
            {
                SprayFallbackLog.Verbose($"NetworkController: Serve: Queue full");
                SendWait(peer, token);
                return;
            }

            request = new Request { Peer = peer, Token = token, Id = id };
            _queue.Add(request);
        }

        request.Seen = Time.realtimeSinceStartup;
        PumpQueue();
        if (_queue.Contains(request))
        {
            SprayFallbackLog.Verbose($"NetworkController: Serve: Queued at {_queue.IndexOf(request)}");
            SendWait(peer, token);
        }
    }

    private int ServesFor(ushort peer)
    {
        string prefix = peer + ":";
        return _outgoing.Values.Count(t => t.Peer == peer) + _preparing.Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    private void PumpQueue()
    {
        for (int i = 0; i < _queue.Count;)
        {
            if (_preparing.Count >= MaxPreparing || _outgoing.Count + _preparing.Count >= MaxTransfers)
                break;

            var request = _queue[i];
            if (ServesFor(request.Peer) >= MaxServesPerPeer || request.Size >= 0 && !CanSend(request.Size))
            {
                i++;
                continue;
            }

            _queue.RemoveAt(i);
            StartServe(request);
        }
    }

    private async void StartServe(Request request)
    {
        ushort peer = request.Peer;
        string token = request.Token;
        string key = Key(peer, token);
        _preparing.Add(key);
        int generation = _generation;
        _peerSessions.TryGetValue(peer, out string session);
        try
        {
            SprayFallbackLog.Verbose($"NetworkController: Serve: Reading image");
            var image = await Service.ReadHostedAsync(request.Id, SessionToken);
            if (!SameSession(generation, peer, session) || !_preparing.Contains(key)
                || !CatPatchServerSettings.AllowSprayFallback)
                return;

            SprayFallbackLog.Verbose($"NetworkController: Serve: Image check");
            if (image == null || image.Bytes.Length > CatPatchServerSettings.MaxSprayFallbackBytes)
            {
                Result(peer, token, false, "Image is unavailable on the host.");
                return;
            }

            if (!CanSend(image.Bytes.Length))
            {
                SprayFallbackLog.Verbose($"NetworkController: Serve: Over budget, requeueing");
                request.Size = image.Bytes.Length;
                request.Seen = Time.realtimeSinceStartup;
                _queue.Insert(0, request);
                SendWait(peer, token);
                return;
            }

            SprayFallbackLog.Verbose($"NetworkController: Serve: Adding to outgoing");
            _outgoing[key] = new Outgoing { Peer = peer, Token = token, Image = image };
        }
        catch (Exception ex)
        {
            if (SameSession(generation, peer, session))
                Result(peer, token, false, ex.Message);
        }
        finally
        {
            if (SameSession(generation, peer, session))
                _preparing.Remove(key);
        }
    }

    private void SendOffer(Outgoing item)
    {
        SprayFallbackLog.Verbose($"NetworkController: SendOffer: token={item.Token} id={item.Image.Id} size={item.Image.Bytes.Length / 1024}KiB");
        try
        {
            Send(item.Peer, Kind.Offer, w =>
            {
                w.Put(item.Token);
                w.Put(item.Image.Id);
                w.Put(item.Image.Bytes.Length);
                w.Put(item.Image.Hash);
                w.Put(item.Image.BackendUploaded);
                w.Put(item.FragmentSize);
            });
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Spray offer failed: " + ex.Message);
        }
    }

    private void SendAck(Incoming incoming)
    {
        incoming.NeedsAck = false;
        Send(incoming.Peer, Kind.Ack, w =>
        {
            w.Put(incoming.Token);
            w.Put(incoming.Buffer.ToReceiveIndex);
            w.Put(incoming.Limit);
        });
    }

    private void PumpFragments()
    {
        if (_outgoing.Count == 0)
            return;
        List<Outgoing> transfers = [];
        Dictionary<ushort, long> peerInFlight = [];
        foreach (var item in _outgoing.Values)
        {
            peerInFlight[item.Peer] = peerInFlight.GetValueOrDefault(item.Peer) + item.InFlightBytes;
            if (item.Accepted && item.NextFragmentIndex < item.Limit)
                transfers.Add(item);
        }

        if (transfers.Count == 0)
            return;

        // Steam can be very picky and fails easily, so get the actual number of bytes in flight including gameplay packets
        if (Net.Transport is TransportSteamworks steam)
        {
            foreach (ushort peer in peerInFlight.Keys.ToArray())
            {
                if (TryGetSteamPendingBytes(steam, peer, out long pending))
                    peerInFlight[peer] = pending;
            }
        }

        long totalInFlight = peerInFlight.Values.Sum();

        int start = _pumpRoundRobinRotation++ % transfers.Count;
        _pumpRoundRobinRotation &= int.MaxValue;
        int sent = 0;
        bool any = true;
        while (any && sent < FrameFragmentLimit)
        {
            any = false;
            for (int i = 0; i < transfers.Count && sent < FrameFragmentLimit; i++)
            {
                var item = transfers[(start + i) % transfers.Count];
                if (item.NextFragmentIndex >= item.Limit)
                    continue;

                int length = item.FragmentLength(item.NextFragmentIndex);
                long peerBytes = peerInFlight[item.Peer];
                if (peerBytes > 0 && peerBytes + length > PeerWindowBytes
                    || totalInFlight > 0 && totalInFlight + length > TotalWindowBytes)
                    continue;

                SendFragment(item, item.NextFragmentIndex);
                item.NextFragmentIndex++;
                peerInFlight[item.Peer] = peerBytes + length;
                totalInFlight += length;
                sent++;
                any = true;
            }
        }
    }

    private static bool TryGetSteamPendingBytes(TransportSteamworks steam, ushort peer, out long pending)
    {
        pending = 0;
        // Installed Together keeps these members internal. Cache metadata rather than
        // publicizing the entire dependency or looking fields up for every fragment.
        HSteamNetConnection connection;
        object data;
        if (Net.IsServer)
        {
            if (!steam.SteamIDToClientIDDict.TryGet(peer, out ulong steamId)
                || !(ConnectionMappingField?.GetValue(steam) is IDictionary mapping)
                || !mapping.Contains(steamId))
                return false;
            data = mapping[steamId];
        }
        else
        {
            data = ServerUserProperty?.GetValue(steam);
        }

        if (data == null || !(ConnectionField?.GetValue(data) is HSteamNetConnection handle))
            return false;
        connection = handle;

        if (connection == HSteamNetConnection.Invalid)
            return false;

        var status = new SteamNetConnectionRealTimeStatus_t();
        var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
        EResult result;
        if (steam.is_steamserver)
            result = SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes);
        else
            result = SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, 0, ref lanes);

        if (result != EResult.k_EResultOK)
            return false;

        pending = (long)status.m_cbPendingReliable + status.m_cbPendingUnreliable;
        return true;
    }

    private static readonly FieldInfo ConnectionMappingField = AccessTools.Field(typeof(TransportSteamworks), "connectionMapping");
    private static readonly PropertyInfo ServerUserProperty = AccessTools.Property(typeof(TransportSteamworks), "serverUser");
    private static readonly FieldInfo ConnectionField = AccessTools.Field(
        typeof(TransportSteamworks).GetNestedType("SteamConnectionData", BindingFlags.Public | BindingFlags.NonPublic), "connection");

    private void SendFragment(Outgoing item, int index)
    {
        SprayFallbackLog.Verbose($"NetworkController: SendFragment: token={item.Token} id={item.Image.Id} index={index}");
        try
        {
            int offset = item.Offset(index);
            int length = item.FragmentLength(index);
            Send(item.Peer, Kind.Fragment, w =>
            {
                w.Put(item.Token);
                w.Put(index);
                w.Put(item.Image.Bytes, offset, length);
            });
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Spray send failed: " + ex.Message);
        }
    }

    private void FinishOutgoing(string key, bool success)
    {
        SprayFallbackLog.Verbose($"NetworkController: FinishOutgoing");
        if (!_outgoing.TryGetValue(key, out var item))
            return;
        _outgoing.Remove(key);
        item.Completion.TrySetResult(success);
    }

    private void RememberReceipt(ushort peer, string token, bool success, string message)
    {
        SprayFallbackLog.Verbose($"NetworkController: RememberReceipt");
        if (_receipts.Count >= 256)
            _receipts.Remove(_receipts.Keys.First());
        _receipts[Key(peer, token)] = new Receipt { Success = success, Message = message };
        Result(peer, token, success, message);
    }

    private void SendWait(ushort peer, string token)
    {
        Send(peer, Kind.Wait, w => w.Put(token));
    }

    private void Result(ushort peer, string token, bool success, string message)
    {
        message ??= "";
        if (message.Length > 256)
            message = message.Substring(0, 256);
        Send(peer, Kind.Result, w =>
        {
            w.Put(token);
            w.Put(success);
            w.Put(message);
        });
    }

    private void Announce(SprayImage image)
    {
        Plugin.Logger.LogInfo($"[Sprays] stage=fallback.cache id={image.Id} bytes={image.Bytes.Length}");
    }
    private void Send(ushort peer, Kind kind, Action<NetDataWriter> write)
    {
        var writer = CatPatchNetwork.CreateWriter(IsHost ? ToClient : ToHost);
        if (IsHost && !_peerSessions.ContainsKey(peer))
            return;

        writer.Put((byte)kind);
        writer.Put(IsHost ? _peerSessions[peer] : _helloToken);

        write(writer);
        SendRaw(peer, writer);
    }

    private void SendRaw(ushort peer, NetDataWriter writer)
    {
        if (IsHost)
            Net.Server_SendTo(DeliveryMethod.ReliableUnordered, writer, peer);
        else
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
    }

    public void ResetSession()
    {
        SprayFallbackLog.Verbose($"NetworkController: ResetSession");
        _generation++;
        _budgets.Clear();
        _allPackets = new TransferRateLimit(512, 1024);
        _allOffers = new TransferRateLimit(64L * 1024 * 1024, 2L * 1024 * 1024);
        _packetWarnings = new TransferRateLimit(4, 1);
        _allRequests = new TransferRateLimit(16, 2);
        _placedImages.Clear();
        _session.Cancel();
        _session.Dispose();
        _session = new CancellationTokenSource();
        foreach (var item in _outgoing.Values)
            item.Completion.TrySetCanceled();
        foreach (var item in _downloads.Values)
            item.Completion.TrySetCanceled();
        _handshake.TrySetResult(false);
        _handshake = NewCompletion<bool>();
        _outgoing.Clear();
        _incoming.Clear();
        _downloads.Clear();
        _receipts.Clear();
        _preparing.Clear();
        _queue.Clear();
        _peers.Clear();
        _peerSessions.Clear();
        _helloToken = Guid.NewGuid().ToString("N");
        _lastProbe = float.NegativeInfinity;
        _probeAttempts = 0;
        HostMaxSize = 0;
        HostRequiresBackend = false;
        Service?.ResetSession();
    }

    private void OnApplicationQuit()
    {
        Close();
    }

    private void OnDestroy()
    {
        Close();
        SceneManager.sceneLoaded -= SceneLoaded;
        ScavPlayer.OnPlayerJoined -= PlayerJoined;
        ScavPlayer.OnPlayerLeft -= PlayerLeft;
        if (Instance == this)
            Instance = null;
    }

    private void Close()
    {
        SprayFallbackLog.Verbose($"NetworkController: Close");
        if (_closed)
            return;

        _closed = true;
        ResetSession();
        _session.Cancel();
        Service?.Close();
    }
}

[HarmonyPatch(typeof(Net), nameof(Net.ShutdownReset))]
internal static class SprayFallbackSessionReset
{
    [HarmonyPostfix]
    private static void Reset() => NetworkController.Instance?.ResetSession();
}
