using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class MonoBehaviour { }
    public static class Time { public static double realtimeSinceStartupAsDouble; }
}
public class PlayerCamera
{
    public static PlayerCamera main;
    public bool isActiveAndEnabled = true;
    public object alertText = new object(), alertBack = new object();
    public bool Fail;
    public int Displays;
    public string LastMessage;
    public void DoAlert(string message, bool important)
    {
        if (Fail) throw new Exception("HUD not ready");
        Displays++;
        LastMessage = message;
    }
}
namespace LiteNetLib { public enum DeliveryMethod { ReliableUnordered } }
namespace LiteNetLib.Utils
{
    public class NetDataWriter { public string Message; public void Put(string message, bool flag) { Message = message; } }
    public class NetDataReader
    {
        public string Message;
        public bool Fail;
        public void Get(out string message, bool flag) { if (Fail) throw new Exception("bad packet"); message = Message; }
    }
}
namespace Together
{
#pragma warning disable CS8981 // Match Together's existing lowercase public type.
    public struct knetid { public ushort Value; public knetid(ushort value) { Value = value; } }
#pragma warning restore CS8981
    public class ScavPlayer
    {
        public ulong SteamId;
        public knetid playerId;
        public static Dictionary<knetid, ScavPlayer> ClientIdToPlayerDict = new Dictionary<knetid, ScavPlayer>();
    }
    public class TransportSteamworks
    {
        public Dictionary<ulong, ScavPlayer> SteamIDToNetPlayerDict = new Dictionary<ulong, ScavPlayer>();
    }
    public static class ServerMain
    {
        public static IReadOnlyList<knetid> AllClientIdsExceptHost = new[] { new knetid(2) };
    }
    public static class Net
    {
        public static bool IsRunningSteam;
        public static TransportSteamworks Steam;
        public static bool LookupFails, SendFails;
        public static int Sends, SendAttempts;
        public static bool TryGetSteamTransport(out TransportSteamworks steam)
        {
            if (LookupFails) throw new Exception("identity lookup unavailable");
            steam = Steam; return Steam != null;
        }
        public static void Server_SendToClients(LiteNetLib.DeliveryMethod method, LiteNetLib.Utils.NetDataWriter writer, IReadOnlyList<knetid> recipients)
        {
            SendAttempts++;
            if (SendFails) throw new Exception("transport unavailable");
            Sends++;
        }
    }
}
namespace CatPatchspace
{
    public static class KrokoshaScavMultiplayer
    {
        public static bool network_system_is_running, is_server;
        public delegate void KrokoshaHandleNamedMessageDelegate(Together.knetid sender, ref LiteNetLib.Utils.NetDataReader reader);
    }
    public static class CatPatchNetwork
    {
        public static KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate Handler;
        public static int Registrations;
        public static bool IsRegistered(bool server, ushort id) { return Handler != null; }
        public static void RegisterClient(ushort id, KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate handler) { Handler = handler; Registrations++; }
        public static LiteNetLib.Utils.NetDataWriter CreateWriter(ushort id) { return new LiteNetLib.Utils.NetDataWriter(); }
    }
    public static class NetworkMessageIds { public const ushort CreatorJoinAlert = 31120; }
    public static class Plugin { public static TestLog Logger = new TestLog(); }
    public class TestLog
    {
        public readonly List<string> Lines = new List<string>();
        public void LogInfo(string message) { Lines.Add(message); }
        public void LogWarning(string message) { Lines.Add(message); }
    }
    public static class HugController
    {
        // Reproduce the old helper's swallowed display failure for baseline checks.
        public static void ShowLocalMessage(string message) { try { PlayerCamera.main?.DoAlert(message, false); } catch { } }
    }
}
