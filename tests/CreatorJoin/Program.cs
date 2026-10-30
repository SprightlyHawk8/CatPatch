using System;
using System.Linq;
using System.Reflection;
using CatPatchspace;
using Together;
using UnityEngine;
using LiteNetLib.Utils;

class Program
{
    const ulong Creator = 76561198303047174UL;
    const string Message = "Mr CatPatch has joined.";
    static int passed, failed;
    static CreatorJoinAnnouncer announcer;
    static void Assert(bool condition, string name) { if (!condition) throw new Exception(name); }
    static void Call(string name) { typeof(CreatorJoinAnnouncer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(announcer, null); }
    static void Tick(double now) { Time.realtimeSinceStartupAsDouble = now; Call("Update"); }
    static void Receive(string message = Message, bool malformed = false)
    {
        var reader = new NetDataReader { Message = message, Fail = malformed };
        CatPatchNetwork.Handler(new knetid(1), ref reader);
    }
    static void Reset(bool host = true)
    {
        if (announcer != null) Call("OnDestroy");
        Time.realtimeSinceStartupAsDouble = 0;
        KrokoshaScavMultiplayer.network_system_is_running = true;
        KrokoshaScavMultiplayer.is_server = host;
        ScavPlayer.ClientIdToPlayerDict.Clear();
        Net.Steam = null; Net.IsRunningSteam = false; Net.LookupFails = false; Net.SendFails = false;
        Net.Sends = 0; Net.SendAttempts = 0;
        PlayerCamera.main = null;
        Plugin.Logger.Lines.Clear();
        ServerMain.AllClientIdsExceptHost = new[] { new knetid(2) };
        CatPatchNetwork.Handler = null; CatPatchNetwork.Registrations = 0;
        announcer = new CreatorJoinAnnouncer();
        Call("Awake");
    }
    static void AddCreator(ulong steamId = Creator) { ScavPlayer.ClientIdToPlayerDict[new knetid(2)] = new ScavPlayer { SteamId = steamId, playerId = new knetid(2) }; }
    static void Test(string name, Action run)
    {
        try { run(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
    }
    static int Main()
    {
        Test("receiver registers before network starts", () => { Reset(false); Assert(CatPatchNetwork.Handler != null, "receiver missing"); });
        Test("received join survives missing camera", () => {
            Reset(false); Tick(0); Receive(); Tick(10);
            PlayerCamera.main = new PlayerCamera(); Tick(11); Tick(12);
            Assert(PlayerCamera.main.Displays == 1 && PlayerCamera.main.LastMessage == Message, "alert lost or duplicated");
        });
        Test("received join waits for initialized HUD fields", () => {
            Reset(false); Tick(0); PlayerCamera.main = new PlayerCamera { alertText = null, alertBack = null }; Receive(); Tick(1);
            Assert(PlayerCamera.main.Displays == 0, "display attempted before HUD ready");
            PlayerCamera.main.alertText = new object(); PlayerCamera.main.alertBack = new object(); Tick(2);
            Assert(PlayerCamera.main.Displays == 1, "queued HUD alert missing");
        });
        Test("disabled camera does not consume alert", () => {
            Reset(false); Tick(0); PlayerCamera.main = new PlayerCamera { isActiveAndEnabled = false }; Receive();
            Assert(PlayerCamera.main.Displays == 0, "inactive HUD used"); PlayerCamera.main.isActiveAndEnabled = true; Tick(1);
            Assert(PlayerCamera.main.Displays == 1, "alert not retained");
        });
        Test("display exception retains alert and logs once", () => {
            Reset(false); Tick(0); PlayerCamera.main = new PlayerCamera { Fail = true }; Receive(); Tick(1); Tick(2);
            Assert(Plugin.Logger.Lines.Count(s => s.Contains("HUD display failed")) == 1, "missing or repeated error");
            PlayerCamera.main.Fail = false; Tick(3); Assert(PlayerCamera.main.Displays == 1, "failed alert not retried");
        });
        Test("disconnect clears pending alert", () => {
            Reset(false); Tick(0); Receive(); KrokoshaScavMultiplayer.network_system_is_running = false; Tick(1);
            PlayerCamera.main = new PlayerCamera(); KrokoshaScavMultiplayer.network_system_is_running = true; Tick(2);
            Assert(PlayerCamera.main.Displays == 0, "stale session alert shown");
        });
        Test("host detects creator SteamId and announces once after delay", () => {
            Reset(); AddCreator(); PlayerCamera.main = new PlayerCamera(); Tick(0); Tick(0.5);
            Assert(Net.Sends == 0, "delay bypassed"); Tick(1); Tick(5);
            Assert(Net.Sends == 1 && PlayerCamera.main.Displays == 1, "wrong send/display count");
        });
        Test("authenticated transport identity works before player field", () => {
            Reset(); Net.IsRunningSteam = true; Net.Steam = new TransportSteamworks();
            Net.Steam.SteamIDToNetPlayerDict[Creator] = new ScavPlayer { SteamId = 0 }; Tick(0); Tick(1);
            Assert(Net.Sends == 1 && Plugin.Logger.Lines.Any(s => s.Contains("source=steam_transport")), "transport identity ignored");
        });
        Test("late Steam identity is detected", () => {
            Reset(); AddCreator(0); Tick(0); Tick(2); Assert(Net.Sends == 0, "zero Steam ID recognized");
            ScavPlayer.ClientIdToPlayerDict.Values.First().SteamId = Creator; Tick(3); Tick(4);
            Assert(Net.Sends == 1, "late identity ignored");
        });
        Test("other Steam identities do not announce", () => { Reset(); AddCreator(Creator + 1); Tick(0); Tick(10); Assert(Net.Sends == 0, "wrong identity matched"); });
        Test("client never originates creator broadcast", () => { Reset(false); AddCreator(); Tick(0); Tick(10); Assert(Net.Sends == 0, "client broadcast"); });
        Test("host queue survives missing local camera", () => {
            Reset(); AddCreator(); Tick(0); Tick(1); Assert(Net.Sends == 1, "remote broadcast blocked by local camera");
            PlayerCamera.main = new PlayerCamera(); Tick(2); Assert(PlayerCamera.main.Displays == 1, "host alert lost");
        });
        Test("creator rejoin rearms after confirmed departure", () => {
            Reset(); AddCreator(); Tick(0); Tick(1); ScavPlayer.ClientIdToPlayerDict.Clear(); Tick(2); Tick(4.25);
            AddCreator(); Tick(5); Tick(6); Assert(Net.Sends == 2, "rejoin not announced");
        });
        Test("brief missing player does not duplicate announcement", () => {
            Reset(); AddCreator(); Tick(0); Tick(1); ScavPlayer.ClientIdToPlayerDict.Clear(); Tick(2); AddCreator(); Tick(3); Tick(7);
            Assert(Net.Sends == 1, "brief gap duplicated join");
        });
        Test("transport failure retries at bounded interval", () => {
            Reset(); AddCreator(); Tick(0); Net.SendFails = true; Tick(1); Tick(1.5); Tick(2);
            Assert(Net.SendAttempts == 1, "retry unbounded"); Net.SendFails = false; Tick(3); Tick(4);
            Assert(Net.Sends == 1 && Net.SendAttempts == 2, "failed broadcast treated as sent");
        });
        Test("identity exceptions are logged and do not imply departure", () => {
            Reset(); AddCreator(); Tick(0); Tick(1); Net.LookupFails = true; Tick(2); Tick(5);
            Assert(Plugin.Logger.Lines.Count(s => s.Contains("identity lookup failed")) == 1, "lookup error spam/silence");
            Net.LookupFails = false; Tick(6); Tick(8); Assert(Net.Sends == 1, "lookup failure reset presence");
        });
        Test("host-only session still queues local announcement", () => {
            Reset(); AddCreator(); ServerMain.AllClientIdsExceptHost = Array.Empty<knetid>(); Tick(0); Tick(1);
            PlayerCamera.main = new PlayerCamera(); Tick(2); Assert(PlayerCamera.main.Displays == 1 && Net.Sends == 0, "host-only alert missing");
        });
        Test("malformed and unexpected announcements are rejected", () => {
            Reset(false); Tick(0); PlayerCamera.main = new PlayerCamera(); Receive("other"); Receive(Message, true);
            Assert(PlayerCamera.main.Displays == 0, "invalid packet displayed"); Assert(Plugin.Logger.Lines.Any(s => s.Contains("could not read")), "malformed packet not logged");
        });
        Console.WriteLine("CREATOR_JOIN_CHECKS passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}
