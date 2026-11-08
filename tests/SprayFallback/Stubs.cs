using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;

#pragma warning disable CS0067, CS0649, CS8981
namespace UnityEngine
{
    public class MonoBehaviour { public static void Destroy(object value) { } }
    public static class Time { public static float realtimeSinceStartup => Sim.Now; }
    public static class Mathf
    {
        public static int Clamp(int v, int min, int max) => Math.Clamp(v, min, max);
        public static float Min(float a, float b) => Math.Min(a, b);
    }
    public static class Random { public static float Range(float a, float b) => (a + b) / 2; }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { }
    public enum LoadSceneMode { Single }
    public static class SceneManager { public static event Action<Scene, LoadSceneMode> sceneLoaded; }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public class HarmonyPatch : Attribute { public HarmonyPatch(Type t, string m) { } }
    [AttributeUsage(AttributeTargets.Method)] public class HarmonyPostfix : Attribute { }
    public static class AccessTools
    {
        public static FieldInfo Field(Type t, string name) => t?.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        public static PropertyInfo Property(Type t, string name) => t?.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }
}
namespace BepInEx { public static class Paths { public static string ConfigPath => Sim.Active.Root; } }
namespace BepInEx.Configuration
{
    public class ConfigDescription { public ConfigDescription(string text, object range) { } }
    public class AcceptableValueRange<T> { public AcceptableValueRange(T min, T max) { } }
    public class ConfigEntry<T>
    {
        private T _value;
        public ConfigEntry(T value) { _value = value; }
        public T Value
        {
            get => _value;
            set { if (!Equals(_value, value)) { _value = value; SettingChanged?.Invoke(this, EventArgs.Empty); } }
        }
        public event EventHandler SettingChanged;
    }
    public class ConfigFile
    {
        private readonly Dictionary<string, object> _values = new();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, object description)
        {
            string id = section + "/" + key;
            if (!_values.TryGetValue(id, out var entry)) _values[id] = entry = new ConfigEntry<T>(value);
            return (ConfigEntry<T>)entry;
        }
        public void Save() { }
    }
}
namespace Together
{
    public struct knetid
    {
        public ushort id;
        public knetid(ushort value) { id = value; }
        public static implicit operator ushort(knetid value) => value.id;
    }
    public class ScavPlayer
    {
        public knetid playerId;
        public bool IsLocal;
        public static ScavPlayer LocalPlayer => Sim.Active.Player;
        public static event Action<ScavPlayer> OnPlayerJoined;
        public static event Action<ScavPlayer> OnPlayerLeft;
    }
    public class TransportSteamworks
    {
        public bool is_steamserver;
        public class SteamConnectionData { internal HSteamNetConnection connection; }
        internal readonly Dictionary<ulong, SteamConnectionData> connectionMapping = new();
        private SteamConnectionData serverUser { get; set; }
        public class IdMap { public bool TryGet(ushort peer, out ulong id) { id = peer; return false; } }
        public IdMap SteamIDToClientIDDict = new();
    }
    public static class Net
    {
        public static bool IsRunning => !Sim.Active.Offline;
        public static bool IsServer => Sim.Active.Id == 0;
        public static object Transport => null;
        public static void Client_Send(DeliveryMethod method, NetDataWriter writer) => Sim.Send(Sim.Active.Id, 0, writer.CopyData());
        public static void Server_SendTo(DeliveryMethod method, NetDataWriter writer, ushort peer) => Sim.Send(0, peer, writer.CopyData());
        public static void ShutdownReset() { }
    }
}
namespace CatPatchspace
{
    public sealed class Log
    {
        public void LogDebug(object message) { }
        public void LogInfo(object message) { Sim.Logs.Add(message.ToString()); }
        public void LogWarning(object message) { Sim.Logs.Add(message.ToString()); }
    }
    public static class Plugin { public const string ModVersion = "test"; public static Log Logger = new(); }
    public static class CatPatchUpdater { public const string DefaultUrl = "unused"; }
    public static class HugController { public static void ShowLocalMessage(string text) { } }
    public class Con { public static Con con; public void LogToConsole(string text) { } }
    public static class CatPatchHostModerationMenu
    {
        public static int GetHostSprayMuteRemainingSeconds(ulong peer) => Sim.Active.Muted.Contains((ushort)peer) ? 60 : 0;
    }
    public static class CatPatchServerSettings
    {
        public static bool AllowSprayFallback => Sim.Active.Allow;
        public static int MaxSprayFallbackBytes => Sim.Active.MaxBytes;
    }
    public static class CatPatchNetwork
    {
        public delegate void Handler(Together.knetid peer, ref NetDataReader reader);
        internal static Dictionary<ushort, Handler> Servers = new(), Clients = new();
        public static void RegisterServer(ushort id, Handler handler) => Servers[id] = handler;
        public static void RegisterClient(ushort id, Handler handler) => Clients[id] = handler;
        public static NetDataWriter CreateWriter(ushort id) { var writer = new NetDataWriter(); writer.Put(id); return writer; }
    }
    public class GarythecatSprayUploadResponse { public string spray_id; }
    public static class GarythecatBackendClient
    {
        public static bool UploadSpray(byte[] bytes, out GarythecatSprayUploadResponse response, out string error)
        {
            var node = Sim.WorkerNode;
            node.UploadGate?.Wait();
            node.UploadCalls++;
            error = "simulated backend failure";
            response = null;
            if (!node.UploadOk) return false;
            string id = CatPatchspace.SprayFallback.SprayImage.ComputeHash(bytes);
            lock (Sim.Backend) Sim.Backend[id] = bytes;
            response = new() { spray_id = id };
            return true;
        }
        public static bool DownloadSpray(string id, out byte[] bytes, out string error)
        {
            var node = Sim.WorkerNode;
            node.DownloadCalls++;
            error = "simulated backend failure";
            bytes = null;
            lock (Sim.Backend) return node.DownloadOk && Sim.Backend.TryGetValue(id, out bytes);
        }
    }
}
