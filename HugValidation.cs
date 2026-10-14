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
    internal static class HugValidation
    {
        public static bool IsBodyHuggable(Body body)
        {
            return body != null && body.alive && body.conscious;
        }

        public static bool CanStartHug(knetid clientA, knetid clientB, out ScavPlayer playerA, out Body bodyA, out ScavPlayer playerB, out Body bodyB)
        {
            playerA = null;
            bodyA = null;
            playerB = null;
            bodyB = null;

            if (clientA == clientB)
                return false;

            if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientA, out playerA, out bodyA))
                return false;

            if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientB, out playerB, out bodyB))
                return false;

            return IsBodyHuggable(bodyA) && IsBodyHuggable(bodyB);
        }

        public static bool CanStartHugWithinDistance(
            knetid clientA,
            knetid clientB,
            float maxDistance,
            out ScavPlayer playerA,
            out Body bodyA,
            out ScavPlayer playerB,
            out Body bodyB)
        {
            if (!CanStartHug(clientA, clientB, out playerA, out bodyA, out playerB, out bodyB))
                return false;

            return AreBodiesWithinDistance(bodyA, bodyB, maxDistance);
        }

        public static bool AreBodiesWithinDistance(Body bodyA, Body bodyB, float maxDistance)
        {
            if (bodyA == null || bodyB == null)
                return false;

            Vector2 delta = (Vector2)bodyA.transform.position - (Vector2)bodyB.transform.position;
            return delta.sqrMagnitude <= maxDistance * maxDistance;
        }
    }
}