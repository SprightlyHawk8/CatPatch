using HarmonyLib;
using Together;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CatPatchspace
{
    internal static class HostPlayerSelectors
    {
        // Together's menu cycles this same array in this order. Read its Color
        // values directly so Color24 rounding and shades match the actual picker.
        private static readonly Dictionary<string, int> NamedColorPresets =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "red", 0 }, { "green", 1 }, { "blue", 2 }, { "cyan", 3 },
                { "yellow", 4 }, { "magenta", 5 }, { "white", 6 },
                { "orange", 7 }, { "darkorange", 8 }, { "lightorange", 9 },
                { "purple", 10 }, { "mediumpurple", 11 }, { "darkpurple", 12 },
                { "pink", 13 }, { "lightpink", 14 }, { "rose", 15 },
                { "lightgreen", 16 }, { "forestgreen", 17 }, { "darkgreen", 18 },
                { "sagegreen", 19 }, { "skyblue", 20 }, { "royalblue", 21 },
                { "lightblue", 22 }, { "darkblue", 23 }, { "teal", 24 },
                { "darkcyan", 25 }, { "lightyellow", 26 }, { "gold", 27 },
                { "mustard", 28 }, { "brown", 29 }, { "darkbrown", 30 },
                { "tan", 31 }, { "mint", 32 }, { "hotpink", 33 },
                { "darkred", 34 }, { "brickred", 35 }, { "lime", 36 }
            };

        // Retain existing custom-color aliases that are not menu presets.
        private static readonly Dictionary<string, string> NamedColorHex =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "black", "#000000" },
                { "gray", "#808080" },
                { "grey", "#808080" }
            };

        internal static IEnumerable<string> NamedSelectors
        {
            get
            {
                yield return "@alive";
                yield return "@dead";

                foreach (string colorName in NamedColorPresets.Keys)
                    yield return "@" + colorName;

                foreach (string colorName in NamedColorHex.Keys)
                    yield return "@" + colorName;
            }
        }

        internal static bool TryGetMatchingPlayers(string selector, IEnumerable<ScavPlayer> candidates, out List<ScavPlayer> matches)
        {
            matches = new List<ScavPlayer>();

            bool selectAlive = string.Equals(selector, "@alive", StringComparison.OrdinalIgnoreCase);
            bool selectDead = string.Equals(selector, "@dead", StringComparison.OrdinalIgnoreCase);

            Color24 color;
            bool selectColor = TryParse(selector, out color);
            if (!selectAlive && !selectDead && !selectColor)
                return false;

            try
            {
                foreach (ScavPlayer player in candidates)
                {
                    if (player == null)
                        continue;

                    if (selectAlive && player.body != null && player.body.alive)
                        matches.Add(player);
                    else if (selectDead && player.body != null && !player.body.alive)
                        matches.Add(player);
                    else if (selectColor && player.playerColor == color)
                        matches.Add(player);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("Could not resolve player selector " + selector + ": " + ex.Message);
            }

            return true;
        }

        internal static bool TryParse(string selector, out Color24 color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(selector) || selector.Length < 2 || selector[0] != '@')
                return false;

            string value = selector.Substring(1);
            if (NamedColorPresets.TryGetValue(value, out int index))
            {
                Color[] presets = ScavPlayer.NAMETAG_DEFAULT_COLORS;
                if (presets == null || index >= presets.Length)
                    return false;

                color = presets[index];
                return true;
            }

            string hex;
            if (NamedColorHex.TryGetValue(value, out hex))
                return Color24.TryParseHex(hex, out color);

            if (value.StartsWith("#", StringComparison.Ordinal))
                return Color24.TryParseHex(value, out color);

            return false;
        }

    }

    [HarmonyPatch]
    internal static class HostPlayerSelectorPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Con), "GetPlayersForCommand");
        }

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool found = TargetMethod() != null;
            if (!found)
                Plugin.Logger?.LogWarning("CatPatch player selectors disabled: Together player selector method was not found.");

            return found;
        }

        [HarmonyPrefix]
        private static bool Prefix(
            string plrname, bool onlyOne, bool allowMacros, List<ScavPlayer> toFilter,
            ref List<ScavPlayer> __result)
        {
            if (!allowMacros)
                return true;

            List<ScavPlayer> matches;
            IEnumerable<ScavPlayer> candidates = toFilter ?? ScavPlayer.ClientIdToPlayerDict.Values.ToList();
            if (!HostPlayerSelectors.TryGetMatchingPlayers(plrname, candidates, out matches))
                return true;

            // Resolve at Together's common boundary so body commands (e.g. give)
            // see the same selectors while retaining its body and single-target rules.
            __result = onlyOne && matches.Count != 1 ? new List<ScavPlayer>() : matches;
            return false;
        }
    }

    internal static class HostConditionalCommands
    {
        internal const string Usage = "if <tile|item|bodyfield> <operator> <value> <player selector|x,y> \"then command\" [else \"else command\"]";
        [ThreadStatic] private static int _depth;

        internal static void Register()
        {
            if (ConsoleScript.SearchExact("if") != null)
            {
                Plugin.Logger?.LogWarning("CatPatch conditional command not registered: 'if' already exists.");
                return;
            }

            var autofill = new Dictionary<int, List<string>>
            {
                [0] = new List<string> { "tile", "item", "stamina", "happiness", "alive", "conscious" },
                [1] = new List<string> { "=", "!=", "<", "<=", ">", ">=" },
                [3] = new List<string> { "@a", "@m", "@o", "@c", "@r", "0,0" }
            };
            autofill[3].AddRange(HostPlayerSelectors.NamedSelectors);
            // An arbitrary nested command must not bypass Together's delegated-admin
            // command sanitizer. This version runs only for the actual host.
            Con.RegisterCommand_BannedAdmin(new Command("if", "Host conditional command. " + Usage,
                Execute, autofill, ("check", "tile, item, or a public Body field/property"),
                ("operator", "= != < <= > >="), ("value", "value to compare"),
                ("player or position", "player selector or tile world position"),
                ("then", "quoted command; @t is the player being checked"),
                ("else", "optional quoted command")));
            Plugin.Logger?.LogInfo("[HostConsoleCommands] registered host-only if command.");
        }

        internal static void Execute(string[] args)
        {
            ConsoleScript console = ConsoleScript.instance;
            ScavPlayer caller = Con.GetCommandCallerPlayer();
            if (Net.IsRunning && (Net.IsClient || (caller != null && caller != ScavPlayer.LocalPlayer)))
                throw new InvalidOperationException("Conditional commands are available only to the multiplayer host.");
            console.CheckForWorld();
            Con.ConFailIfCheatsDisabled();
            if (_depth >= 8)
                throw new InvalidOperationException("Conditional commands may nest at most 8 levels.");

            ConditionalRequest request = ConditionalRequest.Parse(args);
            ValidateBranch(request.ThenCommand);
            ValidateBranch(request.ElseCommand);
            bool tile = request.Check.Equals("tile", StringComparison.OrdinalIgnoreCase);
            bool item = request.Check.Equals("item", StringComparison.OrdinalIgnoreCase);
            if (item && request.Operator != "=" && request.Operator != "!=")
                throw new ArgumentException("Item checks support only = and != (present or absent).");

            MemberInfo member = null;
            if (!tile && !item)
            {
                string name = request.Check.StartsWith("body.", StringComparison.OrdinalIgnoreCase)
                    ? request.Check.Substring(5) : request.Check;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
                member = (MemberInfo)typeof(Body).GetField(name, flags) ?? typeof(Body).GetProperty(name, flags);
                Type type = member is FieldInfo f ? f.FieldType : (member as PropertyInfo)?.PropertyType;
                if (type == null || !ConditionalRequest.IsScalar(type)
                    || (member is PropertyInfo p && (!p.CanRead || p.GetIndexParameters().Length != 0)))
                    throw new ArgumentException("Unknown or unsupported Body field/property: " + name);
                // Validate the comparison even when no players match.
                ConditionalRequest.Compare(ActivatorValue(type), request.Operator, request.Value);
            }
            if (tile)
            {
                if (!ushort.TryParse(request.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException("Tile value must be a block ID from 0 to 65535.");
            }

            // Snapshot all conditions before commands can change the world or players.
            var decisions = new List<(string Target, bool Match)>();
            if (tile && IsPosition(request.Target))
            {
                if (ConditionalRequest.HasTargetToken(request.ThenCommand) || ConditionalRequest.HasTargetToken(request.ElseCommand))
                    throw new ArgumentException("@t needs a player target; a fixed tile position has no player target.");
                decisions.Add((null, CheckTile(console.ParsePosition(request.Target), request)));
            }
            else
            {
                if (!Net.IsRunning)
                    throw new InvalidOperationException("Player selectors require multiplayer; use a fixed position for singleplayer tile checks.");
                List<ScavPlayer> players = Con.GetPlayersForCommand(request.Target, caller, false, true)
                    .Where(p => p != null && p.body != null).ToList();
                if (players.Count == 0)
                    throw new ArgumentException("No spawned players matched " + request.Target + ".");
                foreach (ScavPlayer player in players)
                {
                    bool match;
                    if (tile)
                        match = CheckTile(player.body.transform.position, request);
                    else if (item)
                    {
                        bool present = player.body.FindByIdThorough(request.Value, out _);
                        match = request.Operator == "=" ? present : !present;
                    }
                    else
                    {
                        object value = member is FieldInfo field ? field.GetValue(player.body) : ((PropertyInfo)member).GetValue(player.body);
                        match = ConditionalRequest.Compare(value, request.Operator, request.Value);
                    }
                    decisions.Add(("id:" + player.playerId, match));
                }
            }

            int executed = 0;
            try
            {
                _depth++;
                foreach (var decision in decisions)
                {
                    string branch = decision.Match ? request.ThenCommand : request.ElseCommand;
                    if (branch == null)
                        continue;
                    console.ExecuteCommand(ConditionalRequest.BindTarget(branch, decision.Target));
                    executed++;
                }
            }
            finally { _depth--; }
            console.LogToConsole("if: checked " + decisions.Count + ", true " + decisions.Count(d => d.Match)
                + ", dispatched " + executed + " command(s).");
        }

        private static object ActivatorValue(Type type) => type == typeof(string) ? "" : Activator.CreateInstance(type);

        private static bool IsPosition(string target) => target.Contains(",")
            || target.Equals("cursor", StringComparison.OrdinalIgnoreCase)
            || target.Equals("player", StringComparison.OrdinalIgnoreCase)
            || target.Equals("random", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("plr:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("player:", StringComparison.OrdinalIgnoreCase);

        private static bool CheckTile(Vector2 position, ConditionalRequest request)
        {
            WorldGeneration world = WorldGeneration.world;
            Vector2Int block = world.WorldToBlockPos(position);
            if (block.x < 0 || block.y < 0 || block.x >= world.width || block.y >= world.height)
                throw new ArgumentException("Tile position is outside the world.");
            return ConditionalRequest.Compare(world.GetBlock(block), request.Operator, request.Value);
        }

        private static void ValidateBranch(string branch)
        {
            if (branch == null)
                return;
            if (branch.IndexOf(';') >= 0 || branch.IndexOf('\n') >= 0 || branch.IndexOf('\r') >= 0)
                throw new ArgumentException("Use one command per branch (no semicolons or newlines).");
            string name = branch.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
            if (ConsoleScript.SearchExact(name) == null)
                throw new ArgumentException("Unknown branch command: " + name);
        }
    }

    // Parsing/comparison rules contain no Unity state so they can be regression-tested.
    internal sealed class ConditionalRequest
    {
        internal string Check, Operator, Value, Target, ThenCommand, ElseCommand;

        internal static ConditionalRequest Parse(string[] args)
        {
            if (args == null || args.Length < 6)
                throw new ArgumentException(HostConditionalCommands.Usage);
            string op = args[2];
            if (op == "==") op = "=";
            if (op == "=<") op = "<=";
            if (op == "=>") op = ">=";
            if (op != "=" && op != "!=" && op != "<" && op != "<=" && op != ">" && op != ">=")
                throw new ArgumentException("Unknown operator. Use = != < <= > >=.");
            List<string> branches = ParseBranches(string.Join(" ", args.Skip(5)));
            return new ConditionalRequest { Check = args[1], Operator = op, Value = args[3], Target = args[4],
                ThenCommand = branches[0], ElseCommand = branches.Count == 2 ? branches[1] : null };
        }

        private static List<string> ParseBranches(string text)
        {
            var result = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i == text.Length) break;
                bool quoted = text[i] == '"';
                var command = new StringBuilder();
                if (quoted)
                {
                    i++;
                    bool closed = false;
                    while (i < text.Length)
                    {
                        char c = text[i++];
                        if (c == '\\' && i < text.Length && (text[i] == '"' || text[i] == '\\'))
                            command.Append(text[i++]);
                        else if (c == '"') { closed = true; break; }
                        else command.Append(c);
                    }
                    if (!closed || (i < text.Length && !char.IsWhiteSpace(text[i])))
                        throw new ArgumentException("Branch commands need matching double quotes separated by spaces.");
                }
                else
                {
                    while (i < text.Length && !char.IsWhiteSpace(text[i])) command.Append(text[i++]);
                }
                string value = command.ToString();
                if (!quoted && value.Equals("else", StringComparison.OrdinalIgnoreCase) && result.Count == 1)
                {
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (i == text.Length) throw new ArgumentException("else needs a command.");
                    continue;
                }
                if (!quoted) value = value.Replace('_', ' ');
                if (string.IsNullOrWhiteSpace(value) || result.Count == 2)
                    throw new ArgumentException(HostConditionalCommands.Usage);
                result.Add(value.Trim());
            }
            if (result.Count == 0) throw new ArgumentException(HostConditionalCommands.Usage);
            return result;
        }

        internal static bool IsScalar(Type type) => type.IsEnum || type == typeof(string) || type == typeof(bool)
            || Type.GetTypeCode(type) >= TypeCode.SByte && Type.GetTypeCode(type) <= TypeCode.Decimal;

        internal static bool Compare(object actual, string op, string expected)
        {
            if (actual == null) throw new ArgumentException("Cannot compare a null Body value.");
            bool equal;
            if (actual is bool flag)
            {
                if (!bool.TryParse(expected, out bool wanted))
                    throw new ArgumentException("Boolean fields require true or false.");
                equal = flag == wanted;
            }
            else if (actual is string || actual.GetType().IsEnum)
                equal = string.Equals(actual.ToString(), expected, StringComparison.OrdinalIgnoreCase);
            else
            {
                if (!double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double wanted)
                    || double.IsNaN(wanted) || double.IsInfinity(wanted))
                    throw new ArgumentException("Numeric comparisons require a finite number (use a decimal point).");
                double value = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new ArgumentException("Body value is not a finite number.");
                switch (op)
                {
                    case "=": return value == wanted;
                    case "!=": return value != wanted;
                    case "<": return value < wanted;
                    case "<=": return value <= wanted;
                    case ">": return value > wanted;
                    case ">=": return value >= wanted;
                    default: throw new ArgumentException("Unknown comparison operator.");
                }
            }
            if (op != "=" && op != "!=")
                throw new ArgumentException("Boolean, text and enum checks support only = and !=.");
            return op == "=" ? equal : !equal;
        }

        internal static bool HasTargetToken(string command) => command != null && command.Split(' ').Contains("@t");
        internal static string BindTarget(string command, string target) => string.Join(" ",
            command.Split(' ').Select(token => token == "@t" ? target : token));
    }

    [HarmonyPatch(typeof(ConsoleScript), "Start")]
    internal static class HostConditionalCommandRegistrationPatch
    {
        private static void Postfix() => HostConditionalCommands.Register();
    }

    [HarmonyPatch]
    internal static class HostPlayerSelectorAutocompletePatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Con), "UpdatePlayerListAutocomplete");
        }

        [HarmonyPrepare]
        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            foreach (Command command in ConsoleScript.Commands)
            {
                if (command == null || command.argDescription == null || command.argAutofill == null)
                    continue;

                for (int i = 0; i < command.argDescription.Length; i++)
                {
                    if (!command.argDescription[i].Item1.StartsWith("player", StringComparison.OrdinalIgnoreCase))
                        continue;

                    List<string> suggestions;
                    if (!command.argAutofill.TryGetValue(i, out suggestions) || suggestions == null)
                        continue;

                    foreach (string selector in HostPlayerSelectors.NamedSelectors)
                    {
                        if (!suggestions.Contains(selector))
                            suggestions.Add(selector);
                    }
                }
            }
        }
    }
}

