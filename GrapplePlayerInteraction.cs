using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Together;
using UnityEngine;

namespace CatPatchspace
{
    /// <summary>
    /// Extends the vanilla grappling hook so it can latch onto another
    /// multiplayer player and pull both players toward each other.
    /// </summary>
    internal static class GrapplePlayerInteraction
    {
        private static readonly FieldInfo PullingField =
            AccessTools.Field(typeof(GrapplingHook), "pulling");

        private static readonly FieldInfo HookLatchedField =
            AccessTools.Field(typeof(GrapplingHook), "hookLatched");

        private static readonly FieldInfo FiredField =
            AccessTools.Field(typeof(GrapplingHook), "fired");

        private static readonly Dictionary<GrapplingHook, Body> OwnerByHook =
            new Dictionary<GrapplingHook, Body>();

        private static readonly Dictionary<GrapplingHook, Body> TargetByHook =
            new Dictionary<GrapplingHook, Body>();

        internal static void RegisterOwner(GrapplingHook hook, Body owner)
        {
            if (hook == null || owner == null)
                return;

            OwnerByHook[hook] = owner;
        }

        internal static void RegisterTarget(GrapplingHook hook, Body target)
        {
            if (hook == null || target == null)
                return;

            TargetByHook[hook] = target;
        }

        internal static Body GetOwner(GrapplingHook hook)
        {
            if (hook == null)
                return null;

            Body owner;
            OwnerByHook.TryGetValue(hook, out owner);
            return owner;
        }

        internal static void ClearState(GrapplingHook hook)
        {
            if (hook == null)
                return;

            OwnerByHook.Remove(hook);
            TargetByHook.Remove(hook);
        }

        internal static bool IsPlayerBody(Body body)
        {
            return body != null && ScavPlayer.BodyToPlayerDict.ContainsKey(body);
        }

        internal static bool IsFired(GrapplingHook hook)
        {
            return ReadBool(FiredField, hook);
        }

        internal static bool IsLatched(GrapplingHook hook)
        {
            return ReadBool(HookLatchedField, hook);
        }

        internal static bool IsPulling(GrapplingHook hook)
        {
            return ReadBool(PullingField, hook);
        }

        internal static void ApplyPlayerPull(GrapplingHook hook)
        {
            if (hook == null || !Net.IsRunning || !IsPulling(hook))
                return;

            Body owner;
            Body target;
            if (!OwnerByHook.TryGetValue(hook, out owner) ||
                !TargetByHook.TryGetValue(hook, out target) ||
                owner == null ||
                target == null ||
                !owner.alive ||
                !target.alive)
            {
                ClearState(hook);
                return;
            }

            Vector2 ownerPosition = owner.transform.position;
            Vector2 offset = (Vector2)target.transform.position - ownerPosition;
            float distance = offset.magnitude;
            if (distance < 0.2f)
                return;

            Vector2 direction = offset / Mathf.Max(0.0001f, distance);
            float distanceFactor = Mathf.Clamp01(distance / 12f);
            float pullSpeed = Mathf.Lerp(10f, 26f, distanceFactor);

            // Together synchronizes each player's resulting body velocity.
            // Applying the same deterministic force on every peer preserves the
            // historical interaction while each player's local simulation
            // remains the authority for its replicated movement.
            if (owner.rb != null)
                owner.rb.velocity = Vector2.Lerp(owner.rb.velocity, direction * pullSpeed, 0.18f);

            if (target.rb != null)
                target.rb.velocity = Vector2.Lerp(target.rb.velocity, -direction * pullSpeed, 0.18f);
        }

        private static bool ReadBool(FieldInfo field, GrapplingHook hook)
        {
            if (field == null || hook == null)
                return false;

            try
            {
                object value = field.GetValue(hook);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(GrapplingHook), "Use")]
    internal static class GrappleTrackOwnerPatch
    {
        [HarmonyPrefix]
        private static void Prefix(GrapplingHook __instance, Body body)
        {
            if (!Net.IsRunning || __instance == null || body == null)
                return;

            GrapplePlayerInteraction.RegisterOwner(__instance, body);
        }

        [HarmonyPostfix]
        private static void Postfix(GrapplingHook __instance)
        {
            GrapplePlayerInteraction.ApplyPlayerPull(__instance);
        }
    }

    [HarmonyPatch(typeof(HookPoint), "OnCollisionEnter2D")]
    internal static class GrapplePlayerCollisionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(HookPoint __instance, Collision2D collision)
        {
            if (!Net.IsRunning || __instance == null || collision == null)
                return true;

            GrapplingHook hook = __instance.hook;
            if (hook == null)
                return true;

            Body target = FindBody(collision.gameObject);
            if (!GrapplePlayerInteraction.IsPlayerBody(target))
                return true;

            Body owner = GrapplePlayerInteraction.GetOwner(hook);
            if (owner == null || owner == target)
                return true;

            try
            {
                GrapplePlayerInteraction.RegisterTarget(hook, target);
                hook.HookHit();
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    "Grapple player latch failed: " + exception.Message);
            }

            // A player collision is handled here even if HookHit was already
            // latched, preventing vanilla terrain-only collision handling from
            // changing the result.
            return false;
        }

        private static Body FindBody(GameObject collisionObject)
        {
            if (collisionObject == null)
                return null;

            try
            {
                Body body = collisionObject.GetComponent<Body>();
                if (body != null)
                    return body;

                Limb limb = collisionObject.GetComponent<Limb>();
                if (limb != null && limb.body != null)
                    return limb.body;

                return collisionObject.GetComponentInParent<Body>();
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(GrapplingHook), "FixedUpdate")]
    internal static class GrapplePlayerPullPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GrapplingHook __instance)
        {
            GrapplePlayerInteraction.ApplyPlayerPull(__instance);
        }
    }

    [HarmonyPatch(typeof(GrapplingHook), "Update")]
    internal static class GrapplePlayerCleanupPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GrapplingHook __instance)
        {
            if (__instance == null)
                return;

            if (!GrapplePlayerInteraction.IsFired(__instance) &&
                !GrapplePlayerInteraction.IsLatched(__instance) &&
                !GrapplePlayerInteraction.IsPulling(__instance))
            {
                GrapplePlayerInteraction.ClearState(__instance);
            }
        }
    }
}
