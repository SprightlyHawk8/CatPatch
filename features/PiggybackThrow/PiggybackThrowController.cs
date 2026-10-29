using Together;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class PiggybackThrowController : MonoBehaviour
    {
        private const ushort ThrowRequestMsg = NetworkMessageIds.PiggybackThrowRequest;
        private const ushort ThrowExecuteMsg = NetworkMessageIds.PiggybackThrowExecute;
        private const double RegistrationVerifyIntervalSeconds = 2.0;
        private const double ThrowCooldownSeconds = 0.35;
        private const float DefaultThrowAngleDegrees = 50.0f;

        private static PiggybackThrowController _instance;
        private static bool _handlersRegistered;
        private static double _nextRegistrationVerifyAt;
        private readonly Dictionary<knetid, double> _nextAllowedThrowByClientId = new Dictionary<knetid, double>();

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Update()
        {
            RegisterHandlersOnce();

            if (!KrokoshaScavMultiplayer.network_system_is_running)
                return;

            if (CatPatchConsoleGuard.IsGameInputUiActive())
                return;

            if (!Input.GetKeyDown(KeyBinds.GetBind("casualtiestogether_push")))
                return;

            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            NetBody carrier = null;
            if (localPlayer != null)
                localPlayer.TryGetNetBody(out carrier);
            if (localPlayer == null)
                return;

            if (carrier == null)
                return;

            if (carrier.carrying_person == null)
                return;

            bool straightUp = Input.GetKey(KeyBinds.GetBind("up"));

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerThrowRequest(localPlayer.playerId, straightUp);
                return;
            }

            if (!CatPatchHostCompatibility.CanSendHostFeature("Piggyback throw"))
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(ThrowRequestMsg);
            writer.Put(straightUp);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        private static void OnServerThrowRequestReceived(knetid senderClientId, ref NetDataReader reader)
        {
            bool straightUp = false;
            try
            {
                reader.Get(out straightUp);
            }
            catch
            {
            }

            if (_instance != null)
                _instance.HandleServerThrowRequest(senderClientId, straightUp);
        }

        private void HandleServerThrowRequest(knetid carrierClientId, bool straightUp)
        {
            if (!CatPatchServerSettings.PiggybackThrowEnabled)
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            double nextAllowed;
            if (_nextAllowedThrowByClientId.TryGetValue(carrierClientId, out nextAllowed) && now < nextAllowed)
                return;

            ScavPlayer carrierPlayer;
            NetBody carrier;
            if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(
                    carrierClientId,
                    out carrierPlayer,
                    out carrier))
                return;

            if (carrier == null || carrier.body == null || !carrier.body.alive || carrier.carrying_person == null)
                return;

            NetBody thrown = carrier.carrying_person;
            if (thrown == null || thrown.body == null || thrown.piggybacking_on != carrier)
                return;

            _nextAllowedThrowByClientId[carrierClientId] = now + ThrowCooldownSeconds;
            float strength = CatPatchServerSettings.PiggybackThrowStrength;
            ThrowCarriedPlayer(carrier, thrown, straightUp, strength, true);
            BroadcastThrowExecute(carrier.netId, thrown.netId, straightUp, strength);
        }

        private static void ThrowCarriedPlayer(NetBody carrier, NetBody thrown, bool straightUp, float strength, bool remindServer)
        {
            Vector2 direction = straightUp
                ? Vector2.up
                : GetForwardThrowDirection(carrier);

            Vector2 carrierVelocity = carrier.body != null && carrier.body.rb != null
                ? carrier.body.rb.velocity
                : Vector2.zero;

            thrown.StopPiggyback(true);
            thrown.SetNetIgnoreTime(0.25f);
            thrown.body.Ragdoll();
            thrown.body.shock = Mathf.Max(thrown.body.shock, 2.0f);
            thrown.body.SetVelocity(carrierVelocity * 0.35f + direction * strength);

            if (thrown.body.limbs != null)
            {
                for (int i = 0; i < thrown.body.limbs.Length; i++)
                {
                    Limb limb = thrown.body.limbs[i];
                    if (limb != null && limb.rb != null)
                        limb.rb.velocity = carrierVelocity * 0.35f + direction * strength;
                }
            }

            if (remindServer && KrokoshaScavMultiplayer.is_server)
                thrown.Server_RemindPlayersCurrentState(true);
        }

        private static void BroadcastThrowExecute(knetid carrierClientId, knetid thrownClientId, bool straightUp, float strength)
        {
            IReadOnlyList<knetid> destinations = GetClientIdsExceptHost();
            if (destinations == null || destinations.Count == 0)
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(ThrowExecuteMsg);
            writer.Put(carrierClientId);
            writer.Put(thrownClientId);
            writer.Put(straightUp);
            writer.Put(strength);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, destinations);
        }

        private static Vector2 GetForwardThrowDirection(NetBody carrier)
        {
            float x = carrier != null && carrier.body != null && carrier.body.isRight ? 1.0f : -1.0f;
            float radians = DefaultThrowAngleDegrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(radians) * x, Mathf.Sin(radians)).normalized;
        }

        private static IReadOnlyList<knetid> GetClientIdsExceptHost()
        {
            try
            {
                return ServerMain.AllClientIdsExceptHost;
            }
            catch
            {
                return new List<knetid>();
            }
        }

        private static void RegisterHandlersOnce()
        {
            if (_handlersRegistered)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (now < _nextRegistrationVerifyAt)
                    return;

                _nextRegistrationVerifyAt = now + RegistrationVerifyIntervalSeconds;
                if (CatPatchNetwork.IsRegistered(true, ThrowRequestMsg)
                    && CatPatchNetwork.IsRegistered(false, ThrowExecuteMsg))
                    return;
            }

            _handlersRegistered = true;
            _nextRegistrationVerifyAt = Time.realtimeSinceStartupAsDouble + RegistrationVerifyIntervalSeconds;
            CatPatchNetwork.RegisterServer(ThrowRequestMsg, OnServerThrowRequestReceived);
            CatPatchNetwork.RegisterClient(ThrowExecuteMsg, OnClientThrowExecuteReceived);
        }

        private static void OnClientThrowExecuteReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid carrierClientId;
            knetid thrownClientId;
            bool straightUp;
            float strength;
            reader.Get(out carrierClientId);
            reader.Get(out thrownClientId);
            reader.Get(out straightUp);
            reader.Get(out strength);

            NetBody carrier = NetBody.GetNetBodyFromId(carrierClientId);
            NetBody thrown = NetBody.GetNetBodyFromId(thrownClientId);

            if (carrier == null || thrown == null || thrown.body == null)
                return;

            ThrowCarriedPlayer(carrier, thrown, straightUp, strength, false);
        }

        private static bool HandlerDictionaryContains(string fieldName, ushort messageId)
        {
            try
            {
                FieldInfo field = typeof(Net).GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                    return false;

                object dictionaryObject = field.GetValue(null);
                Dictionary<ushort, KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate> dictionary =
                    dictionaryObject as Dictionary<ushort, KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate>;

                return dictionary != null && dictionary.ContainsKey(messageId);
            }
            catch
            {
                return false;
            }
        }

        private static void LegacyRegisterNetReceiver(
            string methodName,
            ushort messageId,
            KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate handler)
        {
            try
            {
                MethodInfo method = typeof(Net).GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null)
                    return;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2)
                    return;

                Delegate convertedHandler = Delegate.CreateDelegate(parameters[1].ParameterType, handler.Target, handler.Method);
                object[] args = parameters.Length >= 3
                    ? new object[] { messageId, convertedHandler, false }
                    : new object[] { messageId, convertedHandler };

                method.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Could not register " + methodName + " for piggyback throw msg " + messageId + ": " + ex.Message);
            }
        }
    }
}
