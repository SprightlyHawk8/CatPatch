using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;

// Only the game boundary is faked. The check runner compiles the production
// parser, comparator, selector patches, and conditional-command execution code.
namespace UnityEngine
{
    public struct Color
    {
        public float r, g, b;
        public Color(float r, float g, float b) { this.r=r; this.g=g; this.b=b; }
    }
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x=x; this.y=y; } }
    public struct Vector2Int { public int x, y; public Vector2Int(int x, int y) { this.x=x; this.y=y; } }
    public class Transform { public Vector2 position; }
}
public class Item { }
public class Body
{
    public float stamina, happiness;
    public bool alive = true, conscious = true;
    public float totalHappiness => happiness;
    public string mood = "calm";
    public UnityEngine.Transform transform = new UnityEngine.Transform();
    public HashSet<string> Items = new HashSet<string>();
    public bool FindByIdThorough(string id, out Item item) { item=null; return Items.Contains(id); }
}
public class WorldGeneration
{
    public static WorldGeneration world = new WorldGeneration();
    public uint width=100, height=100;
    public ushort block = 14;
    public UnityEngine.Vector2Int WorldToBlockPos(UnityEngine.Vector2 p) => new UnityEngine.Vector2Int((int)p.x+50,(int)p.y+50);
    public ushort GetBlock(UnityEngine.Vector2Int p) => block;
}
public class Command
{
    public delegate void Action(string[] args);
    public string name, description;
    public (string, string)[] argDescription;
    public Dictionary<int,List<string>> argAutofill;
    public Action action;
    public Command(string name,string description,Action action,Dictionary<int,List<string>> autofill,params (string,string)[] args)
    { this.name=name; this.description=description; this.action=action; argAutofill=autofill; argDescription=args; }
}
public class ConsoleScript
{
    public static ConsoleScript instance = new ConsoleScript();
    public static List<Command> Commands = new List<Command>();
    public readonly List<string> Executed = new List<string>();
    public bool HasWorld=true;
    public Action<string> OnExecute;
    public void Start() { }
    public void CheckForWorld() { if (!HasWorld) throw new Exception("No world"); }
    public static Command SearchExact(string name) => Commands.FirstOrDefault(c=>c.name.Equals(name,StringComparison.OrdinalIgnoreCase));
    public void LogToConsole(string message) { }
    public void ExecuteCommand(string text) { Executed.Add(text); OnExecute?.Invoke(text); }
    public UnityEngine.Vector2 ParsePosition(string text)
    {
        if(text=="player" || text=="cursor" || text=="random") return new UnityEngine.Vector2(0,0);
        var parts=text.Split(','); return new UnityEngine.Vector2(float.Parse(parts[0],CultureInfo.InvariantCulture),float.Parse(parts[1],CultureInfo.InvariantCulture));
    }
}
namespace Together
{
    public struct Color24
    {
        private string hex;
        public static implicit operator Color24(UnityEngine.Color color)
        {
            byte Channel(float value) => (byte)Math.Round(Math.Clamp(value,0f,1f)*255f);
            TryParseHex($"#{Channel(color.r):X2}{Channel(color.g):X2}{Channel(color.b):X2}",out var result);
            return result;
        }
        public static bool TryParseHex(string text,out Color24 color)
        { color=new Color24 {hex=text.ToUpperInvariant()}; return text.Length==7 && text[0]=='#' && text.Skip(1).All(Uri.IsHexDigit); }
        public static bool operator ==(Color24 a,Color24 b)=>a.hex==b.hex;
        public static bool operator !=(Color24 a,Color24 b)=>!(a==b);
        public override bool Equals(object o)=>o is Color24 c && this==c;
        public override int GetHashCode()=>hex?.GetHashCode()??0;
    }
    public class ScavPlayer
    {
        // Installed Together 5.1.6test1 menu palette: seven base colors followed
        // by the thirty shades appended by Multiplayer._FIRST_INIT.
        public static UnityEngine.Color[] NAMETAG_DEFAULT_COLORS = new[]
        {
            new UnityEngine.Color(1,0,0), new UnityEngine.Color(0,1,0), new UnityEngine.Color(0,0,1),
            new UnityEngine.Color(0,1,1), new UnityEngine.Color(1,0.92156863f,0.015686275f),
            new UnityEngine.Color(1,0,1), new UnityEngine.Color(1,1,1),
            new UnityEngine.Color(1,.5f,0), new UnityEngine.Color(1,.4f,0), new UnityEngine.Color(1,.6f,.2f),
            new UnityEngine.Color(.5f,0,1), new UnityEngine.Color(.6f,.2f,.8f), new UnityEngine.Color(.4f,0,.6f),
            new UnityEngine.Color(1,.4f,.7f), new UnityEngine.Color(1,.6f,.8f), new UnityEngine.Color(.9f,.3f,.5f),
            new UnityEngine.Color(.5f,1,.5f), new UnityEngine.Color(.2f,.8f,.2f), new UnityEngine.Color(.1f,.5f,.1f),
            new UnityEngine.Color(.3f,.7f,.3f), new UnityEngine.Color(.4f,.7f,1), new UnityEngine.Color(.2f,.4f,.8f),
            new UnityEngine.Color(.5f,.8f,1), new UnityEngine.Color(.1f,.3f,.6f), new UnityEngine.Color(0,.5f,.5f),
            new UnityEngine.Color(0,.7f,.7f), new UnityEngine.Color(1,.9f,.3f), new UnityEngine.Color(1,.8f,0),
            new UnityEngine.Color(.9f,.7f,.1f), new UnityEngine.Color(.6f,.3f,0), new UnityEngine.Color(.4f,.2f,.1f),
            new UnityEngine.Color(.7f,.5f,.3f), new UnityEngine.Color(0,1,.6f), new UnityEngine.Color(1,0,.6f),
            new UnityEngine.Color(.6f,0,0), new UnityEngine.Color(.8f,.2f,.2f), new UnityEngine.Color(.6f,.8f,.2f)
        };
        public static ScavPlayer LocalPlayer;
        public static Dictionary<int,ScavPlayer> ClientIdToPlayerDict = new Dictionary<int,ScavPlayer>();
        public int playerId;
        public string playerName;
        public Body body;
        public Color24 playerColor;
    }
    public static class Net { public static bool IsRunning=true, IsClient; }
    public static class Con
    {
        public static bool Cheats=true;
        public static ScavPlayer Caller;
        public static HashSet<string> BannedAdmin = new HashSet<string>();
        public static ScavPlayer GetCommandCallerPlayer()=>Caller??ScavPlayer.LocalPlayer;
        public static void ConFailIfCheatsDisabled() { if(!Cheats) throw new Exception("Cheats disabled"); }
        public static Command RegisterCommand_BannedAdmin(Command c) { ConsoleScript.Commands.Add(c); BannedAdmin.Add(c.name); return c; }
        public static void UpdatePlayerListAutocomplete() { }
        public static List<ScavPlayer> GetPlayersForCommand(string plrname,ScavPlayer caller,bool onlyOne,bool allowMacros,List<ScavPlayer> toFilter=null)
        {
            var all=(toFilter??ScavPlayer.ClientIdToPlayerDict.Values.ToList()).ToList();
            if(plrname=="@a" && allowMacros && !onlyOne) return all;
            if(plrname=="@m" && allowMacros) return all.Where(p=>p==caller).ToList();
            return all.Where(p=>p.playerName==plrname || "id:"+p.playerId==plrname).ToList();
        }
    }
}
namespace CatPatchspace
{
    public class Logger { public void LogWarning(object text) { } public void LogInfo(object text) { } }
    public static class Plugin { public static Logger Logger = new Logger(); }
}
