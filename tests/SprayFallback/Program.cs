using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using CatPatchspace;
using CatPatchspace.SprayFallback;
using LiteNetLib.Utils;
using Together;

internal sealed class PeerContext : SynchronizationContext
{
    internal Node Owner;
    public override void Post(SendOrPostCallback action, object state) => Sim.Work.Enqueue(() => Sim.Use(Owner, () => action(state)));
}
internal sealed class Node
{
    internal ushort Id;
    internal string Root;
    internal NetworkController Network;
    internal PeerContext Context;
    internal ConfigFile Config = new();
    internal ScavPlayer Player;
    internal Dictionary<FieldInfo, object> Settings;
    internal bool UploadOk = true, DownloadOk = true, Allow = true, Offline;
    internal int MaxBytes = 10 * 1024 * 1024, UploadCalls, DownloadCalls;
    internal HashSet<ushort> Muted = new();
    internal ManualResetEventSlim UploadGate;
}
internal static class Sim
{
    internal static Node Active;
    internal static float Now = 1;
    internal static List<Node> Nodes = new();
    internal static ConcurrentQueue<Action> Work = new();
    internal static Queue<(ushort from, ushort to, byte[] data)> Wire = new();
    internal static Dictionary<string, byte[]> Backend = new();
    internal static List<string> Logs = new();
    private static AsyncLocal<Node> Worker = new();
    internal static Node WorkerNode => Worker.Value;
    internal static bool HoldFragments, DropServer, CorruptNextFragment;
    internal static int GetRequests;
    private static readonly FieldInfo[] SettingsFields = typeof(CatPatchClientSettings).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(f => !f.IsLiteral).ToArray();
    private static readonly PropertyInfo Instance = typeof(NetworkController).GetProperty("Instance");
    internal static void Use(Node node, Action action)
    {
        if (Active != node)
        {
            if (Active != null) Active.Settings = SettingsFields.ToDictionary(f => f, f => f.GetValue(null));
            Active = node;
            foreach (var field in SettingsFields)
                field.SetValue(null, node.Settings != null ? node.Settings[field] : field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
        }
        Worker.Value = node;
        SynchronizationContext.SetSynchronizationContext(node.Context);
        Instance.SetValue(null, node.Network);
        action();
    }
    internal static Node New(ushort id, string root)
    {
        var node = new Node { Id = id, Root = Path.Combine(root, "peer-" + id), Player = new() { playerId = new(id), IsLocal = true } };
        node.Context = new() { Owner = node };
        Nodes.Add(node);
        Use(node, () =>
        {
            CatPatchClientSettings.Bind(node.Config);
            CatPatchClientSettings.ShowSprays = true;
            CatPatchClientSettings.SendSprayFallback = true;
            node.Network = new NetworkController();
            Invoke(node.Network, "Awake");
        });
        return node;
    }
    internal static object Invoke(object instance, string name) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, null);
    internal static int Count(Node node, string field)
    {
        object collection = typeof(NetworkController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(node.Network);
        return (int)collection.GetType().GetProperty("Count").GetValue(collection);
    }
    internal static void Send(ushort from, ushort to, byte[] data)
    {
        if (data.Length > 2 && data[2] == 4) GetRequests++;
        Wire.Enqueue((from, to, data));
    }
    internal static void Pump()
    {
        Now += 0.01f;
        foreach (var node in Nodes) Use(node, () => Invoke(node.Network, "Update"));
        for (int i = 0; i < 200 && Work.TryDequeue(out var action); i++) action();
        int count = Math.Min(200, Wire.Count);
        for (int i = 0; i < count; i++)
        {
            var packet = Wire.Dequeue();
            if (HoldFragments && packet.data.Length > 2 && packet.data[2] == 2) { Wire.Enqueue(packet); continue; }
            if (CorruptNextFragment && packet.data.Length > 2 && packet.data[2] == 2)
            {
                packet.data[packet.data.Length - 1] ^= 1;
                CorruptNextFragment = false;
            }
            if (DropServer && packet.to == 0) continue;
            var node = Nodes.FirstOrDefault(n => n.Id == packet.to);
            if (node == null) continue;
            Use(node, () =>
            {
                var reader = new NetDataReader(packet.data);
                ushort id = reader.GetUShort();
                var handlers = packet.to == 0 ? CatPatchNetwork.Servers : CatPatchNetwork.Clients;
                if (handlers.TryGetValue(id, out var handler)) handler(new(packet.from), ref reader);
            });
        }
        Thread.Sleep(1);
    }
    internal static void Until(Func<bool> done, string label, int milliseconds = 12000)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > milliseconds) throw new Exception("Timed out: " + label + "\n" + string.Join("\n", Logs.TakeLast(12)));
            Pump();
        }
    }
    internal static T Await<T>(Node node, Func<Task<T>> action)
    {
        Task<T> task = null;
        Use(node, () => task = action());
        Until(() => task.IsCompleted, "task completion");
        return task.GetAwaiter().GetResult();
    }
}
internal static class Program
{
    private static int Passed;
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAILED: " + label);
        Passed++;
        Console.WriteLine("PASS " + label);
    }
    private static void Throws(Action action, string label)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException) { Check(true, label); return; }
        throw new Exception("FAILED: " + label);
    }
    private static string Upload(Node node, string path)
    {
        string id = null, error = null;
        Sim.Use(node, () => node.Network.Service.Upload(path, (value, bytes, file) => id = value, e => error = e));
        Sim.Until(() => id != null || error != null, "upload callback");
        if (error != null) throw new Exception(error);
        Sim.Use(Sim.Nodes.First(n => n.Id == 0), () => Sim.Nodes.First(n => n.Id == 0).Network.MarkPlaced(id));
        return id;
    }
    private static byte[] Download(Node node, string id)
    {
        Sim.Use(Sim.Nodes.First(n => n.Id == 0), () => Sim.Nodes.First(n => n.Id == 0).Network.MarkPlaced(id));
        byte[] bytes = null;
        Sim.Use(node, () => node.Network.Service.Download(id, value => bytes = value));
        Sim.Until(() => bytes != null, "download callback");
        return bytes;
    }
    private static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "CatPatch-spray-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var host = Sim.New(0, root);
            var sender = Sim.New(1, root);
            var viewer = Sim.New(2, root);
            Check(Sim.Await(sender, () => sender.Network.EnsureHostAsync()), "sender negotiates host support");
            Check(Sim.Await(viewer, () => viewer.Network.EnsureHostAsync()), "viewer negotiates host support");
            Sim.Use(sender, () =>
            {
                Check(CatPatchClientSettings.ClearSprayFallbackCacheOnStartup, "startup cache cleanup defaults on when sprays are shown");
                CatPatchClientSettings.ShowSprays = false;
                Check(!CatPatchClientSettings.ClearSprayFallbackCacheOnStartup, "startup cleanup is inactive when ShowSprays is off");
                CatPatchClientSettings.ShowSprays = true;
                Check(CatPatchClientSettings.ClearSprayFallbackCacheOnStartup, "showing sprays enables the default startup cleanup preference");
                CatPatchClientSettings.ClearSprayFallbackCacheOnStartup = false;
                CatPatchClientSettings.ShowSprays = false;
                CatPatchClientSettings.ShowSprays = true;
                Check(!CatPatchClientSettings.ClearSprayFallbackCacheOnStartup, "explicit cache cleanup opt-out survives hiding and showing sprays");
                Check(!sender.Config.Bind("Client Settings", "ClearSprayFallbackCacheOnStartup", true, "").Value, "cache cleanup choice is persisted");
                CatPatchClientSettings.ClearSprayFallbackCacheOnStartup = true;
                CatPatchClientSettings.SendSprayFallback = true;
            });
            byte[] payload = ImageFixture(1);
            string path = Path.Combine(root, "fixture.gif");
            File.WriteAllBytes(path, payload);

            Sim.HoldFragments = true;
            string backendId = Upload(sender, path);
            Check(!backendId.StartsWith("custom-"), "working upload retains backend ID");
            Check(Sim.Count(sender, "_outgoing") > 0, "working backend callback does not wait for backup transfer");
            Sim.HoldFragments = false;
            Sim.Until(() => Sim.Count(sender, "_outgoing") == 0, "background host backup");
            Check(Sim.Await(host, () => host.Network.Service.ReadAsync(backendId)).Bytes.SequenceEqual(payload), "host receives byte-exact background backup");

            viewer.DownloadOk = false;
            Sim.Use(host, () => CatPatchClientSettings.ShowSprays = false);
            Sim.Use(viewer, () => CatPatchClientSettings.SendSprayFallback = false);
            Check(Download(viewer, backendId).SequenceEqual(payload), "viewer backend failure recovers from hidden host with own sending off");
            Check(viewer.DownloadCalls == 1, "normal IDs try backend before lobby");
            int before = viewer.DownloadCalls;
            Check(Download(viewer, backendId).SequenceEqual(payload), "cached download is byte-exact");
            Check(viewer.DownloadCalls == before, "cache prevents repeat backend download");

            sender.UploadOk = false;
            payload = ImageFixture(2);
            File.WriteAllBytes(path, payload);
            string custom = Upload(sender, path);
            Check(custom == "custom-" + SprayImage.ComputeHash(payload), "failed upload creates content-addressed ID");
            Check(Sim.Await(host, () => host.Network.Service.ReadAsync(custom)).Bytes.SequenceEqual(payload), "custom callback waits for host storage");
            before = viewer.DownloadCalls;
            Check(Download(viewer, custom).SequenceEqual(payload), "custom spray relays unchanged to viewer");
            Check(viewer.DownloadCalls == before, "custom ID never contacts HTTP backend");

            Sim.Use(sender, () =>
            {
                CatPatchClientSettings.ShowSprays = false;
                Check(!CatPatchClientSettings.SendSprayFallback, "hiding sprays disables fallback sending");
                CatPatchClientSettings.SendSprayFallback = true;
                Check(!CatPatchClientSettings.SendSprayFallback, "sending cannot be enabled while hidden");
                CatPatchClientSettings.ShowSprays = true;
                Check(!CatPatchClientSettings.SendSprayFallback, "showing sprays again keeps sending switched off");
                Check(!sender.Config.Bind("Client Settings", "SendSprayFallback", true, "").Value, "automatic disable persists in config");
                CatPatchClientSettings.SendSprayFallback = true;
                sender.Config.Bind("Client Settings", "ShowSprays", true, "").Value = false;
                Check(!sender.Config.Bind("Client Settings", "SendSprayFallback", true, "").Value, "external ShowSprays config edit also disables sending");
                CatPatchClientSettings.ShowSprays = true;
                CatPatchClientSettings.SendSprayFallback = true;
            });

            // Hide during an already-started fragmented transfer.
            Sim.HoldFragments = true;
            byte[] other = ImageFixture(3);
            var customImage = new SprayImage("custom-" + SprayImage.ComputeHash(other), other, false);
            Task<bool> transfer = null;
            Sim.Use(sender, () => transfer = sender.Network.UploadAsync(customImage));
            Sim.Until(() => Sim.Count(host, "_incoming") > 0, "partial upload offer");
            Sim.Use(sender, () => CatPatchClientSettings.ShowSprays = false);
            Sim.Until(() => transfer.IsCompleted && Sim.Count(host, "_incoming") == 0, "cancel partial upload");
            Check(!transfer.Result, "hiding sprays cancels active personal upload");
            Check(Sim.Count(sender, "_outgoing") == 0, "canceled upload releases outgoing state");
            Check(!Sim.Await(host, () => host.Network.Service.ContainsAsync(customImage.Id)), "canceled partial image never enters host cache");
            Sim.HoldFragments = false;

            Sim.Use(sender, () => { CatPatchClientSettings.ShowSprays = true; CatPatchClientSettings.SendSprayFallback = true; });
            host.Muted.Add(sender.Id);
            Check(!Sim.Await(sender, () => sender.Network.UploadAsync(customImage)), "host rejects muted player's fallback upload");
            host.Muted.Clear();
            int savedLimit = sender.Network.HostMaxSize;
            typeof(NetworkController).GetProperty("HostMaxSize", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(sender.Network, 4);
            Check(!Sim.Await(sender, () => sender.Network.UploadAsync(customImage)), "negotiated host byte limit rejects oversized upload");
            typeof(NetworkController).GetProperty("HostMaxSize", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(sender.Network, savedLimit);

            host.MaxBytes = 4;
            Check(!Sim.Await(sender, () => sender.Network.UploadAsync(customImage)), "host independently enforces byte limit on incoming offers");
            Check(!Sim.Await(host, () => host.Network.Service.ContainsAsync(customImage.Id)), "oversized offer is never cached");
            host.MaxBytes = 10 * 1024 * 1024;

            Sim.CorruptNextFragment = true;
            Check(!Sim.Await(sender, () => sender.Network.UploadAsync(customImage)), "checksum mismatch rejects corrupted image transfer");
            Check(!Sim.Await(host, () => host.Network.Service.ContainsAsync(customImage.Id)), "checksum failure never enters cache");

            Sim.Await(host, () => host.Network.Service.StoreAsync(customImage));
            Sim.Use(host, () => host.Network.MarkPlaced(customImage.Id));
            Sim.HoldFragments = true;
            Task<SprayImage> receiving = null;
            Sim.Use(viewer, () => receiving = viewer.Network.DownloadAsync(customImage.Id));
            Sim.Until(() => Sim.Count(viewer, "_incoming") > 0, "partial download offer");
            Sim.Use(viewer, () => CatPatchClientSettings.ShowSprays = false);
            Sim.Until(() => receiving.IsCompleted && Sim.Count(host, "_outgoing") == 0, "cancel hidden viewer download");
            Check(receiving.Result == null, "hiding sprays stops active fallback download");
            Check(Sim.Count(viewer, "_incoming") == 0, "canceled download releases incoming buffer");
            Sim.Use(viewer, () => CatPatchClientSettings.ShowSprays = true);
            Sim.HoldFragments = false;

            byte[] raceBytes = ImageFixture(4);
            File.WriteAllBytes(path, raceBytes);
            sender.UploadOk = true;
            viewer.DownloadOk = false;
            Sim.HoldFragments = true;
            string raceId = Upload(sender, path);
            Sim.Until(() => Sim.Count(host, "_incoming") > 0, "background seed in progress");
            byte[] raced = null;
            int callbacks = 0;
            Sim.Use(viewer, () => viewer.Network.Service.Download(raceId, value => { raced = value; callbacks++; }));
            for (int i = 0; i < 30; i++) Sim.Pump();
            Sim.HoldFragments = false;
            Sim.Until(() => raced != null, "fallback download overtaking seed");
            Check(raced.SequenceEqual(raceBytes) && callbacks == 1, "download racing background seed completes once with exact bytes");

            byte[] healthy = ImageFixture(5, 0);
            string healthyId = SprayImage.ComputeHash(healthy);
            lock (Sim.Backend) Sim.Backend[healthyId] = healthy;
            viewer.DownloadOk = true;
            int gets = Sim.GetRequests;
            Check(Download(viewer, healthyId).SequenceEqual(healthy), "healthy download uses backend successfully");
            Check(Sim.GetRequests == gets, "healthy backend download sends no fallback image request");

            Sim.HoldFragments = true;
            byte[] disableBytes = ImageFixture(6);
            var disableImage = new SprayImage("custom-" + SprayImage.ComputeHash(disableBytes), disableBytes, false);
            Sim.Use(sender, () => transfer = sender.Network.UploadAsync(disableImage));
            Sim.Until(() => Sim.Count(host, "_incoming") > 0, "upload before host disable");
            host.Allow = false;
            Sim.Until(() => transfer.IsCompleted, "host cancels active transfer");
            Check(!transfer.Result, "host disable promptly rejects active upload");
            Check(Sim.Count(host, "_incoming") == 0, "host disable clears incoming transfer memory");
            host.Allow = true;
            Sim.HoldFragments = false;

            sender.UploadOk = true;
            sender.UploadGate = new(false);
            File.WriteAllBytes(path, other);
            string stale = null;
            int uploads = sender.UploadCalls;
            Sim.Use(sender, () => sender.Network.Service.Upload(path, (id, bytes, file) => stale = id, e => stale = "failure"));
            for (int i = 0; i < 30; i++) Sim.Pump();
            Sim.Use(sender, () => { CatPatchClientSettings.ShowSprays = false; CatPatchClientSettings.ShowSprays = true; });
            sender.UploadGate.Set();
            Sim.Until(() => sender.UploadCalls > uploads, "held HTTP request completes");
            for (int i = 0; i < 100; i++) Sim.Pump();
            Check(stale == null, "hide then show suppresses stale upload callback");
            sender.UploadGate = null;

            // Pending callbacks from a previous layer/session must never place a spray.
            sender.UploadGate = new(false);
            other = ImageFixture(7);
            File.WriteAllBytes(path, other);
            uploads = sender.UploadCalls;
            Sim.Use(sender, () => sender.Network.Service.Upload(path, (id, bytes, file) => stale = id, e => stale = "failure"));
            for (int i = 0; i < 30; i++) Sim.Pump();
            Sim.Use(sender, () => sender.Network.Service.ClearScene());
            sender.UploadGate.Set();
            Sim.Until(() => sender.UploadCalls > uploads, "old layer HTTP completes");
            for (int i = 0; i < 80; i++) Sim.Pump();
            Check(stale == null, "layer change suppresses stale callback");
            sender.UploadGate = null;

            Sim.Use(sender, () => CatPatchClientSettings.SendSprayFallback = true);
            Sim.HoldFragments = true;
            Sim.Use(sender, () => transfer = sender.Network.UploadAsync(customImage));
            Sim.Until(() => Sim.Count(host, "_incoming") > 0, "transfer before reset");
            Sim.Use(sender, () => sender.Network.ResetSession());
            Sim.Until(() => transfer.IsCompleted, "reset cancellation continuation");
            Check(transfer.IsCanceled, "session reset cancels pending upload task");
            Check(Sim.Count(sender, "_outgoing") == 0, "session reset clears transfer registry");
            Sim.Use(host, () => host.Network.ResetSession());
            Sim.HoldFragments = false;

            Sim.DropServer = true;
            File.WriteAllBytes(path, ImageFixture(11, 0));
            Check(!Upload(sender, path).StartsWith("custom-"), "working backend upload still succeeds without fallback-capable host");
            sender.UploadOk = false;
            File.WriteAllBytes(path, ImageFixture(14, 0));
            string unavailable = null;
            Sim.Use(sender, () => sender.Network.Service.Upload(path,
                (id, bytes, file) => unavailable = "unexpected success", e => unavailable = e));
            Sim.Until(() => unavailable != null, "unsupported host reports upload failure");
            Check(unavailable != "unexpected success", "failed backend with unsupported host reports failure without placing");
            Sim.DropServer = false;
            Sim.Use(host, () => { CatPatchClientSettings.ShowSprays = true; CatPatchClientSettings.SendSprayFallback = true; host.Offline = true; });
            host.UploadOk = false;
            Check(Upload(host, path).StartsWith("custom-"), "offline local spraying can use cached custom bytes without backend");
            host.Offline = false;

            SecurityChecks(host, sender, viewer);
            FragmentChecks();
            CacheChecks(root);
            Check(!SprayImage.IsValidId("../escape"), "reject path traversal IDs");
            Check(!SprayImage.IsValidId("custom-abcd"), "reject malformed custom IDs");
            Throws(() => new SprayImage(customImage.Id, ImageFixture(99, 0), false), "reject custom content hash mismatch");
            Check(SprayImage.IsValidId(backendId), "backend IDs remain accepted");
            Console.WriteLine($"SPRAY_FALLBACK_CHECKS_COMPLETE passed={Passed} failed=0");
        }
        finally
        {
            foreach (var node in Sim.Nodes) Sim.Use(node, () => Sim.Invoke(node.Network, "OnDestroy"));
            // Test-created directory only. Cache disposal is asynchronous.
            Thread.Sleep(100);
            string resolved = Path.GetFullPath(root);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("CatPatch-spray-fallback-", StringComparison.Ordinal))
                throw new Exception("Refusing cleanup outside the generated test directory.");
            Directory.Delete(resolved, true);
        }
    }

    private static void FragmentChecks()
    {
        byte[] bytes = Enumerable.Range(0, 10001).Select(i => (byte)i).ToArray();
        var buffer = new FragmentBuffer(bytes.Length, 4096);
        bool Add(int index)
        {
            int offset = index * 4096;
            return buffer.Add(index, new NetDataReader(bytes.Skip(offset).Take(Math.Min(4096, bytes.Length - offset)).ToArray()));
        }
        Check(Add(2) && buffer.ToReceiveIndex == 0, "out-of-order final fragment accepted");
        Check(!Add(2) && buffer.ReceivedCount == 1, "duplicate fragment does not advance receipt count");
        Check(Add(0) && buffer.ToReceiveIndex == 1, "contiguous ACK tracks first missing fragment");
        Check(Add(1) && buffer.IsComplete && buffer.Bytes.SequenceEqual(bytes), "out-of-order fragments reassemble exact bytes");
        Throws(() => buffer.Add(-1, new NetDataReader(new byte[1])), "reject negative fragment index");
        Throws(() => buffer.Add(3, new NetDataReader(new byte[1])), "reject fragment beyond image bounds");
        Throws(() => new FragmentBuffer(10, 1), "reject undersized transfer fragments");
        Throws(() => new FragmentBuffer(SprayImage.HardByteSizeLimit + 1, 4096), "reject image above hard byte limit");
        var shortBuffer = new FragmentBuffer(5000, 4096);
        Throws(() => shortBuffer.Add(1, new NetDataReader(new byte[1000])), "reject wrong final-fragment byte count");
    }

    private static void CacheChecks(string root)
    {
        var first = new SprayImage("aa", ImageFixture(1, 0), true);
        var second = new SprayImage("bb", ImageFixture(2, 0), true);
        int limit = first.Bytes.Length + 1;
        var cache = new SprayCache(Path.Combine(root, "small-cache"), limit, limit);
        cache.Put(first);
        cache.Put(second);
        Check(cache.MemoryBytes <= limit && cache.DiskBytes <= limit, "memory and disk cache budgets enforced");
        Check(!cache.Contains("aa") && cache.Contains("bb"), "least recently used bytes evicted");
        Check(cache.Get("bb").Bytes.SequenceEqual(second.Bytes), "cache retrieval preserves bytes");
        cache.Close(false);
        Check(cache.Get("bb") == null, "closed cache returns no bytes");
        var reopened = new SprayCache(Path.Combine(root, "small-cache"), limit, limit);
        Check(reopened.Get("bb").Bytes.SequenceEqual(second.Bytes), "disk cache survives restart");
        reopened.Close(false);
        string library = Path.Combine(root, "spray-library");
        Directory.CreateDirectory(library);
        string original = Path.Combine(library, "bb.img");
        File.WriteAllBytes(original, second.Bytes);
        string note = Path.Combine(root, "small-cache", "unrelated.txt");
        File.WriteAllText(note, "not cached image data");
        var cleared = new SprayCache(Path.Combine(root, "small-cache"), limit, limit, clearOnStartup: true);
        Check(cleared.Get("bb") == null && !File.Exists(Path.Combine(root, "small-cache", "v2-bb.gif")), "startup cleanup removes previous downloaded cache bytes");
        Check(cleared.MemoryBytes == 0 && cleared.DiskBytes == 0, "startup cleanup begins with empty cache budgets");
        Check(File.Exists(original) && File.ReadAllBytes(original).SequenceEqual(second.Bytes), "startup cleanup preserves the original spray library");
        Check(File.Exists(note), "startup cleanup only removes recognized cache files");
        cleared.Put(first);
        Check(cleared.Get("aa") != null, "cache accepts new downloads after startup cleanup");
        cleared.Close(false);
    }

    private static void SecurityChecks(Node host, Node sender, Node viewer)
    {
        Sim.Use(host, () => host.Network.ResetSession());
        Sim.Use(sender, () => sender.Network.ResetSession());
        Sim.Use(viewer, () => viewer.Network.ResetSession());
        Sim.Now += 60;
        Check(Sim.Await(sender, () => sender.Network.EnsureHostAsync()), "security sender negotiates protocol 2");
        Check(Sim.Await(viewer, () => viewer.Network.EnsureHostAsync()), "security viewer negotiates protocol 2");
        byte[] honest = ImageFixture(40, 0), substitute = ImageFixture(41);
        string id = new string('c', 32);
        lock (Sim.Backend) Sim.Backend[id] = honest;
        var forged = new SprayImage(id, substitute, true);
        Check(!Sim.Await(sender, () => sender.Network.UploadAsync(forged)), "peer cannot first-claim backend ID with substitute bytes");
        Check(Sim.Await(host, () => host.Network.Service.ReadAsync(id)).Bytes.SequenceEqual(honest), "host caches authoritative backend bytes after attempted poisoning");
        Check(!Sim.Await(sender, () => sender.Network.UploadAsync(forged)), "peer cannot overwrite a verified cached ID with a larger file");
        viewer.DownloadOk = false;
        Check(Download(viewer, id).SequenceEqual(honest), "other viewer receives verified bytes after cache-poison attempt");
        var custom = new SprayImage("custom-" + SprayImage.ComputeHash(substitute), substitute, false);
        host.DownloadOk = false;
        Check(!Sim.Await(sender, () => sender.Network.UploadAsync(new SprayImage(new string('d',32), honest, true))),
            "unverifiable backend claim rejected when host cannot reach backend");
        Check(Sim.Await(sender, () => sender.Network.UploadAsync(custom)), "content-addressed fallback still works with host backend unavailable");
        host.DownloadOk = true;
        string session = (string)typeof(NetworkController).GetField("_helloToken", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sender.Network);
        void Send(byte kind, string token, Action<NetDataWriter> payload)
        {
            Sim.Use(sender, () =>
            {
                var writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SprayFallbackToHost);
                writer.Put(kind); writer.Put(session); writer.Put(token); payload(writer);
                Sim.Send(sender.Id, host.Id, writer.CopyData());
            });
        }
        Sim.Now += 60;
        int before = host.DownloadCalls;
        Send(4, Guid.NewGuid().ToString("N"), w => w.Put(new string('e',32)));
        for (int i = 0; i < 40; i++) Sim.Pump();
        Check(host.DownloadCalls == before, "Get for unplaced uncached ID cannot make host query backend");
        Check(Sim.Count(host, "_preparing") == 0 && Sim.Count(host, "_queue") == 0,
            "unplaced image request cannot occupy serving slots");
        string largeToken = Guid.NewGuid().ToString("N");
        Send(0, largeToken, w => { w.Put(custom.Id); w.Put(SprayImage.HardByteSizeLimit + 1); w.Put(custom.Hash); w.Put(false); w.Put(16384); });
        for (int i = 0; i < 20; i++) Sim.Pump();
        Check(Sim.Count(host, "_incoming") == 0, "raw peer offer above 20 MB rejected before buffer allocation");

        // Send a tiny malicious GIF through actual packet handlers; validation
        // must reject it before storage/native decoding, even with a valid hash.
        byte[] giant = ImageFixture(42, 0); giant[6] = giant[8] = 0xff; giant[7] = giant[9] = 0x7f;
        string giantHash = SprayImage.ComputeHash(giant), giantId = "custom-" + giantHash;
        string giantToken = Guid.NewGuid().ToString("N");
        Send(0, giantToken, w => { w.Put(giantId); w.Put(giant.Length); w.Put(giantHash); w.Put(false); w.Put(16384); });
        Sim.Until(() => Sim.Count(host,"_incoming") > 0, "small attack transfer offered");
        for (int i = 0; i < 20; i++) Sim.Pump();
        Send(2, giantToken, w => { w.Put(0); w.Put(giant, 0, giant.Length); });
        Sim.Until(() => Sim.Count(host,"_incoming") == 0, "malicious header rejected after reassembly");
        Check(!Sim.Await(host, () => host.Network.Service.ContainsAsync(giantId)), "oversized GIF canvas never enters host cache");

        // Session-token changes must not reset the cumulative peer budget.
        var budgets = typeof(NetworkController).GetField("_budgets", BindingFlags.Instance | BindingFlags.NonPublic);
        object ledger = ((System.Collections.IDictionary)budgets.GetValue(host.Network))[sender.Id];
        string newSession = Guid.NewGuid().ToString("N");
        Sim.Use(sender, () =>
        {
            var writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SprayFallbackProbe);
            writer.Put(2); writer.Put("test"); writer.Put(newSession);
            Sim.Send(sender.Id, host.Id, writer.CopyData());
        });
        for (int i = 0; i < 20; i++) Sim.Pump();
        Check(ReferenceEquals(ledger, ((System.Collections.IDictionary)budgets.GetValue(host.Network))[sender.Id]), "changing session token does not reset peer quota ledger");
        session = newSession;
        Sim.Now += 60;
        for (int i = 0; i < 40; i++)
        {
            string token = Guid.NewGuid().ToString("N");
            Send(0, token, w => { w.Put(custom.Id); w.Put(custom.Bytes.Length); w.Put(custom.Hash); w.Put(false); w.Put(16384); });
            Send(6, token, _ => { });
        }
        for (int i = 0; i < 30; i++) Sim.Pump();
        var requests = ledger.GetType().GetField("Requests", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ledger) as TransferRateLimit;
        Check(!requests.Take(1, Sim.Now), "offer/cancel churn exhausts request quota rather than evading it");
        Check(Sim.Count(host,"_incoming") <= 4 && Sim.Count(host,"_queue") <= 16, "attack traffic leaves transfer/queue counts bounded");

        var other = ImageFixture(90, 0);
        Sim.Use(viewer, () => CatPatchClientSettings.SendSprayFallback = true);
        Check(Sim.Await(viewer, () => viewer.Network.UploadAsync(new SprayImage("custom-" + SprayImage.ComputeHash(other), other, false))),
            "another peer can share a valid image after attacker quota is exhausted");
        Sim.Use(host, () =>
        {
            string first = 10000.ToString("x");
            for (int i = 10000; i < 10257; i++) { Sim.Now += 0.01f; host.Network.MarkPlaced(i.ToString("x")); }
            Check(Sim.Count(host, "_placedImages") == 256 && !host.Network.WasPlaced(first), "placed-image authorization registry is bounded");
            host.Network.Service.ClearScene();
            Check(Sim.Count(host, "_placedImages") == 0, "layer change clears relay authorizations");
        });
    }

    internal static byte[] ImageFixture(int tag, int padding = 250000)
    {
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        gif[13] = (byte)tag;
        using var stream = new MemoryStream();
        stream.Write(gif, 0, gif.Length - 1);
        if (padding > 0)
        {
            stream.WriteByte(0x21); stream.WriteByte(0xfe);
            for (int left = padding; left > 0;)
            {
                int n = Math.Min(left, 255); stream.WriteByte((byte)n);
                for (int j = 0; j < n; j++) stream.WriteByte((byte)(tag + j));
                left -= n;
            }
            stream.WriteByte(0);
        }
        stream.WriteByte(0x3b);
        return stream.ToArray();
    }
}
