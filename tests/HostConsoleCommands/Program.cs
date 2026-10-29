using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CatPatchspace;
using HarmonyLib;
using Together;

class Program
{
    static int assertions;
    static void Assert(bool condition,string label) { assertions++; if(!condition) throw new Exception(label); }
    static void Reject(Action action,string label)
    { try { action(); } catch(Exception) { assertions++; return; } throw new Exception("Expected rejection: "+label); }
    static string[] Args(string command)=>command.Split(' ',StringSplitOptions.RemoveEmptyEntries);
    static ConditionalRequest Parse(string command)=>ConditionalRequest.Parse(Args(command));
    static void Run(string command) { ConsoleScript.instance.Executed.Clear(); HostConditionalCommands.Execute(Args(command)); }
    static void Main()
    {
        new Harmony("catpatch.console.checks").PatchAll(typeof(HostPlayerSelectorPatch).Assembly);
        ConsoleScript.Commands.Add(new Command("log","",_=>{},null));
        ConsoleScript.Commands.Add(new Command("give","",_=>{},null));
        ConsoleScript.Commands.Add(new Command("setbodyfield","",_=>{},null));
        HostConditionalCommands.Register();
        HostConditionalCommands.Register();
        Assert(ConsoleScript.Commands.Count(c=>c.name=="if")==1,"registration is idempotent");
        Assert(Con.BannedAdmin.Contains("if"),"delegated admins cannot wrap banned commands");
        Color24.TryParseHex("#FF0000",out var red);
        Color24.TryParseHex("#0000FF",out var blue);
        var host=new ScavPlayer {playerId=0,playerName="Host",playerColor=red,body=new Body {stamina=40,happiness=-10}};
        var peer=new ScavPlayer {playerId=1,playerName="Peer",playerColor=blue,body=new Body {stamina=60,happiness=10,alive=false}};
        var unspawned=new ScavPlayer {playerId=2,playerName="Waiting",playerColor=red};
        ScavPlayer.LocalPlayer=host;
        ScavPlayer.ClientIdToPlayerDict=new Dictionary<int,ScavPlayer> {{0,host},{1,peer},{2,unspawned}};
        host.body.Items.Add("first_aid_kit");
        Assert(Con.GetPlayersForCommand("@red",host,false,true).Count==2,"color registry selection");
        Assert(Con.GetPlayersForCommand("@red",host,false,true,new List<ScavPlayer>{peer}).Count==0,"candidate restrictions retained");
        Assert(Con.GetPlayersForCommand("@red",host,true,true).Count==0,"group rejected in single-target context");
        Assert(Con.GetPlayersForCommand("@blue",host,true,true).Single()==peer,"single matching color allowed");
        Assert(Con.GetPlayersForCommand("@red",host,false,false).Count==0,"macros disabled");
        Assert(Con.GetPlayersForCommand("@a",host,false,true).Count==3,"native selector passes through");
        Assert(Con.GetPlayersForCommand("@alive",host,false,true).Single()==host,"alive selector");
        Assert(Con.GetPlayersForCommand("@dead",host,false,true).Single()==peer,"dead selector");
        Assert(Con.GetPlayersForCommand("@#FF0000",host,false,true).Count==2,"hex selector");

        string[] presetNames = { "red", "green", "blue", "cyan", "yellow", "magenta", "white",
            "orange", "darkorange", "lightorange", "purple", "mediumpurple", "darkpurple",
            "pink", "lightpink", "rose", "lightgreen", "forestgreen", "darkgreen", "sagegreen",
            "skyblue", "royalblue", "lightblue", "darkblue", "teal", "darkcyan", "lightyellow",
            "gold", "mustard", "brown", "darkbrown", "tan", "mint", "hotpink", "darkred", "brickred", "lime" };
        for (int i=0; i<presetNames.Length; i++)
        {
            Color24 native = ScavPlayer.NAMETAG_DEFAULT_COLORS[i];
            Assert(HostPlayerSelectors.TryParse("@"+presetNames[i],out var selected) && selected==native,
                "menu palette selector "+presetNames[i]);
        }
        foreach(var name in new[]{"yellow","orange","purple","pink"})
        {
            HostPlayerSelectors.TryParse("@"+name,out var color);
            peer.playerColor=color;
            Assert(Con.GetPlayersForCommand("@"+name,host,false,true).Single()==peer,"menu color selects player: "+name);
        }
        peer.playerColor=blue;
        Color24.TryParseHex("#FFEB04",out var menuYellow);
        Assert(HostPlayerSelectors.TryParse("@YeLLoW",out var yellow) && yellow==menuYellow,"Unity menu yellow, not HTML yellow");
        Color24.TryParseHex("#FFFF00",out var htmlYellow);
        Assert(HostPlayerSelectors.TryParse("@#FFFF00",out var customYellow) && customYellow==htmlYellow && customYellow!=yellow,"explicit hex still literal");
        Assert(HostPlayerSelectors.TryParse("@gray",out var gray) && HostPlayerSelectors.TryParse("@grey",out var grey) && gray==grey,"legacy gray aliases retained");
        var originalPreset=ScavPlayer.NAMETAG_DEFAULT_COLORS[7];
        ScavPlayer.NAMETAG_DEFAULT_COLORS[7]=new UnityEngine.Color(.25f,.5f,.75f);
        Assert(HostPlayerSelectors.TryParse("@orange",out var currentOrange) && currentOrange==(Color24)ScavPlayer.NAMETAG_DEFAULT_COLORS[7],"reads actual current menu palette");
        ScavPlayer.NAMETAG_DEFAULT_COLORS[7]=originalPreset;
        var fullPalette=ScavPlayer.NAMETAG_DEFAULT_COLORS;
        ScavPlayer.NAMETAG_DEFAULT_COLORS=fullPalette.Take(7).ToArray();
        Assert(!HostPlayerSelectors.TryParse("@orange",out _),"uninitialized extended palette does not throw");
        ScavPlayer.NAMETAG_DEFAULT_COLORS=fullPalette;

        var parsed=Parse("If stamina =< 50 @a \"give @t first_aid_kit\" else \"log healthy\"");
        Assert(parsed.Operator=="<=" && parsed.ThenCommand=="give @t first_aid_kit" && parsed.ElseCommand=="log healthy","quoted branches preserve item underscores");
        Assert(Parse("if tile = 14 0,0 give_@a_fentanyl give_@a_opium").ElseCommand=="give @a opium","proposal syntax");
        Assert(Parse("if alive == true @a log_alive").Operator=="=","equality alias");
        Assert(Parse("if stamina => 20 @a log_yes").Operator==">=","greater-equal alias");
        Assert(ConditionalRequest.BindTarget("log @thing @t @t","id:0")=="log @thing id:0 id:0","only whole target tokens replaced");
        Reject(()=>Parse("if stamina <= 50 @a \"log unfinished"),"quotes");
        Reject(()=>Parse("if stamina <= 50 @a log_yes else"),"missing else branch");
        Reject(()=>Parse("if stamina <= 50 @a log_yes log_no log_extra"),"extra branch");
        Reject(()=>Parse("if stamina nonsense 50 @a log_yes"),"operator");

        foreach(var op in new[]{"=","!=","<","<=",">",">="})
            Assert(ConditionalRequest.Compare(50f,op,"50")== (op=="=" || op=="<=" || op==">="),"numeric operator "+op);
        CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
        Assert(ConditionalRequest.Compare(0.5f,"=","0.5"),"invariant numeric culture");
        Reject(()=>ConditionalRequest.Compare(1f,"=","NaN"),"NaN");
        Reject(()=>ConditionalRequest.Compare(1f,"=","Infinity"),"infinity");
        Reject(()=>ConditionalRequest.Compare(true,">","false"),"boolean ordering");
        Reject(()=>ConditionalRequest.Compare(true,"=","1"),"invalid boolean");
        Assert(ConditionalRequest.Compare("CALM","=","calm"),"case-insensitive text");

        Run("if stamina <= 50 @a \"setbodyfield stamina 51 @t\" else \"log healthy @t\"");
        Assert(ConsoleScript.instance.Executed.SequenceEqual(new[]{"setbodyfield stamina 51 id:0","log healthy id:1"}),"per-player then/else; skip unspawned");
        Run("if item = first_aid_kit @a \"give @t naloxone\"");
        Assert(ConsoleScript.instance.Executed.SequenceEqual(new[]{"give id:0 naloxone"}),"item present");
        Run("if item != first_aid_kit @a \"log missing @t\"");
        Assert(ConsoleScript.instance.Executed.SequenceEqual(new[]{"log missing id:1"}),"item absent");
        Run("if body.totalHappiness < 0 @a \"log sad @t\"");
        Assert(ConsoleScript.instance.Executed.Single()=="log sad id:0","read-only property");
        Run("if alive = false @a \"log dead @t\"");
        Assert(ConsoleScript.instance.Executed.Single()=="log dead id:1","boolean check");
        Run("if tile = 14 0,0 \"give @a fentanyl\" else \"give @a opium\"");
        Assert(ConsoleScript.instance.Executed.Single()=="give @a fentanyl","fixed tile once");
        WorldGeneration.world.block=15;
        Run("if tile = 14 0,0 give_@a_fentanyl give_@a_opium");
        Assert(ConsoleScript.instance.Executed.Single()=="give @a opium","fixed tile false branch");
        Run("if tile >= 14 @a \"log tile @t\"");
        Assert(ConsoleScript.instance.Executed.Count==2,"tile at each player's position");
        host.body.stamina=40; peer.body.stamina=40;
        ConsoleScript.instance.OnExecute=_=>peer.body.stamina=80;
        Run("if stamina < 50 @a \"log low @t\"");
        Assert(ConsoleScript.instance.Executed.Count==2,"snapshot before mutations");
        ConsoleScript.instance.OnExecute=null;
        Reject(()=>Run("if tile = 14 0,0 \"give @t fentanyl\""),"no @t for fixed position");
        Reject(()=>Run("if tile = 14 999,999 log_yes"),"world bounds");
        Reject(()=>Run("if tile = -1 0,0 log_yes"),"invalid block ID");
        Reject(()=>Run("if item > fentanyl @a log_yes"),"invalid item operator");
        Reject(()=>Run("if missingfield = 1 @a log_yes"),"unknown body member");
        Reject(()=>Run("if Items = anything @a log_yes"),"non-scalar body member");
        Reject(()=>Run("if stamina < 50 @a \"missing_command @t\""),"unknown branch");
        Reject(()=>Run("if stamina < 50 @a \"log yes;give @a kit\""),"branch command chaining");
        Reject(()=>Run("if stamina < 50 Missing log_yes"),"missing targets");
        Con.Cheats=false;
        Reject(()=>Run("if stamina < 50 @a log_yes"),"cheat gate"); Con.Cheats=true;
        Net.IsClient=true;
        Reject(()=>Run("if stamina < 50 @a log_yes"),"client gate"); Net.IsClient=false;
        Con.Caller=peer;
        Reject(()=>Run("if stamina < 50 @a log_yes"),"remote admin gate"); Con.Caller=null;
        Net.IsRunning=false;
        Run("if tile = 15 0,0 log_yes");
        Assert(ConsoleScript.instance.Executed.Single()=="log yes","singleplayer fixed tile");
        Reject(()=>Run("if stamina < 50 @a log_yes"),"singleplayer selectors"); Net.IsRunning=true;
        ConsoleScript.instance.OnExecute=text=> { if(text.StartsWith("if ")) HostConditionalCommands.Execute(Args(text)); };
        Run("if tile = 15 0,0 \"if tile = 15 0,0 log_yes\"");
        Assert(ConsoleScript.instance.Executed.Last()=="log yes","nested conditional");
        ConsoleScript.instance.OnExecute=_=>HostConditionalCommands.Execute(Args("if tile = 15 0,0 log_yes"));
        Reject(()=>Run("if tile = 15 0,0 log_yes"),"recursive alias nesting bound");
        ConsoleScript.instance.OnExecute=null;
        Run("if tile = 15 0,0 log_yes");
        Assert(ConsoleScript.instance.Executed.Single()=="log yes","depth resets after exception");
        Console.WriteLine("PASS: "+assertions+" conditional-command and selector checks.");
    }
}
