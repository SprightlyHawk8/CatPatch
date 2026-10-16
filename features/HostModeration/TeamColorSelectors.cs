using HarmonyLib;
using Together;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace CatPatchspace
{
    internal static class TeamColorSelectors
    {
        private static readonly Dictionary<string, string> NamedColorHex =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "white", "#FFFFFF" },
                { "black", "#000000" },
                { "red", "#FF0000" },
                { "green", "#00FF00" },
                { "blue", "#0000FF" },
                { "yellow", "#FFFF00" },
                { "cyan", "#00FFFF" },
                { "magenta", "#FF00FF" },
                { "orange", "#FFA500" },
                { "purple", "#800080" },
                { "pink", "#FFC0CB" },
                { "gray", "#808080" },
                { "grey", "#808080" }
            };

        internal static IEnumerable<string> NamedSelectors
        {
            get
            {
                yield return "@alive";
                yield return "@dead";

                foreach (string colorName in NamedColorHex.Keys)
                    yield return "@" + colorName;
            }
        }

        internal static bool TryGetMatchingPlayers(string selector, out List<ScavPlayer> matches)
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
                foreach (ScavPlayer player in ScavPlayer.ClientIdToPlayerDict.Values)
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
            string hex;
            if (NamedColorHex.TryGetValue(value, out hex))
                return Color24.TryParseHex(hex, out color);

            if (value.StartsWith("#", StringComparison.Ordinal))
                return Color24.TryParseHex(value, out color);

            return false;
        }

    }

    [HarmonyPatch]
    internal static class TeamColorPlayerSelectorPatch
    {
        [ThreadStatic]
        private static bool _callingOriginalForPlayer;

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Con), "PerformActionOnPlayersByName");
        }

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool found = TargetMethod() != null;
            if (!found)
                Plugin.Logger?.LogWarning("Team color selectors disabled: Together player selector method was not found.");

            return found;
        }

        [HarmonyPrefix]
        private static bool Prefix(
            MethodBase __originalMethod,
            object[] __args,
            ref Tuple<bool, string> __result)
        {
            if (_callingOriginalForPlayer || __args == null || __args.Length == 0)
                return true;

            string selector = __args[0] as string;
            List<ScavPlayer> matches;
            if (!TeamColorSelectors.TryGetMatchingPlayers(selector, out matches))
                return true;

            if (matches.Count == 0)
            {
                __result = Tuple.Create(false, "No players matched " + selector + ".");
                return false;
            }

            bool anySucceeded = false;
            string lastError = null;

            try
            {
                _callingOriginalForPlayer = true;
                for (int i = 0; i < matches.Count; i++)
                {
                    object[] playerArgs = (object[])__args.Clone();
                    playerArgs[0] = "id:" + matches[i].playerId;

                    Tuple<bool, string> playerResult =
                        __originalMethod.Invoke(null, playerArgs) as Tuple<bool, string>;

                    if (playerResult != null && playerResult.Item1)
                        anySucceeded = true;
                    else if (playerResult != null && !string.IsNullOrWhiteSpace(playerResult.Item2))
                        lastError = playerResult.Item2;
                }
            }
            catch (TargetInvocationException ex)
            {
                Exception cause = ex.InnerException ?? ex;
                lastError = cause.Message;
                Plugin.Logger?.LogWarning("Team color selector failed: " + cause);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Plugin.Logger?.LogWarning("Team color selector failed: " + ex);
            }
            finally
            {
                _callingOriginalForPlayer = false;
            }

            __result = anySucceeded
                ? Tuple.Create(true, string.Empty)
                : Tuple.Create(false, lastError ?? ("Could not act on players matching " + selector + "."));
            return false;
        }
    }

    [HarmonyPatch]
    internal static class TeamColorSelectorAutocompletePatch
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

                    foreach (string selector in TeamColorSelectors.NamedSelectors)
                    {
                        if (!suggestions.Contains(selector))
                            suggestions.Add(selector);
                    }
                }
            }
        }
    }
}
