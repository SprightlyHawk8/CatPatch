using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class HugTargetFinder
    {
        public ScavPlayer FindNearestHuggableTarget(ScavPlayer localPlayer, Vector2 position)
        {
            if (localPlayer == null)
                return null;

            List<ScavPlayer> nearbyPlayers = ScavPlayer.GetPlayersInRadius(position, HugSettings.SearchRadius);
            ScavPlayer best = null;
            float bestDistance = float.PositiveInfinity;

            foreach (ScavPlayer player in nearbyPlayers)
            {
                if (player == null || player == localPlayer || player.body == null)
                    continue;

                if (!HugValidation.IsBodyHuggable(player.body))
                    continue;

                float distance = Vector2.Distance(position, player.body.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = player;
                }
            }

            return best;
        }
    }
}