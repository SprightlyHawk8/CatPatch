using System;
using System.Collections.Generic;
using System.Reflection;
using Together;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class EmoteNetworkController
    {
        private const ushort EmoteRequestMsg = NetworkMessageIds.EmoteRequest;
        private const ushort EmoteStartMsg = NetworkMessageIds.EmoteStart;
        private const int MaxEmoteIdLength = 32;
        private const double RequestAckTimeoutSeconds = 3.0;
        private const double RegistrationVerifyIntervalSeconds = 2.0;

        private static EmoteNetworkController _instance;
        private static bool _handlersRegistered;
        private static double _nextRegistrationVerifyAt;

        private readonly EmoteVisualController _visualController;
        private PendingClientEmoteRequest _pendingClientRequest;

        public EmoteNetworkController(EmoteVisualController visualController)
        {
            _visualController = visualController;
            _instance = this;
        }

        public void RegisterHandlersOnce()
        {
            if (_handlersRegistered)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (now < _nextRegistrationVerifyAt)
                    return;

                _nextRegistrationVerifyAt = now + RegistrationVerifyIntervalSeconds;
                if (NetworkHandlersStillRegistered())
                    return;
            }

            _handlersRegistered = true;
            _nextRegistrationVerifyAt = Time.realtimeSinceStartupAsDouble + RegistrationVerifyIntervalSeconds;

            CatPatchNetwork.RegisterServer(EmoteRequestMsg, OnServerEmoteRequestReceived);

            CatPatchNetwork.RegisterClient(EmoteStartMsg, OnClientEmoteStartReceived);
        }

        public void ResetRegistration()
        {
            _handlersRegistered = false;
            _nextRegistrationVerifyAt = 0.0;
            _pendingClientRequest = null;
            CatPatchHostCompatibility.Reset();
        }

        public void Tick()
        {
            CleanupPendingClientRequest();
        }

        public void SendOrStartEmote(string emoteId)
        {
            EmoteDefinition emote;
            if (!EmoteCatalog.TryGet(emoteId, out emote))
                return;

            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null)
                return;

            if (!KrokoshaScavMultiplayer.network_system_is_running)
            {
                ReceiveStartLocal(localPlayer.playerId, emote.Id);
                return;
            }

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerEmoteRequest(localPlayer.playerId, emote.Id);
                return;
            }

            if (!CatPatchHostCompatibility.CanSendHostFeature("Emotes"))
                return;

            _pendingClientRequest = new PendingClientEmoteRequest
            {
                EmoteId = emote.Id,
                ExpiresAt = Time.realtimeSinceStartupAsDouble + RequestAckTimeoutSeconds
            };

            NetDataWriter writer = CatPatchNetwork.CreateWriter(EmoteRequestMsg);
            writer.Put(emote.Id ?? "", true);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        private static void OnServerEmoteRequestReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            string emoteId;
            reader.Get(out emoteId, true);
            _instance.HandleServerEmoteRequest(senderClientId, emoteId);
        }

        private static void OnClientEmoteStartReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid emoteClientId;
            string emoteId;
            reader.Get(out emoteClientId);
            reader.Get(out emoteId, true);

            _instance.ReceiveStartLocal(emoteClientId, emoteId);
        }

        private void HandleServerEmoteRequest(knetid senderClientId, string emoteId)
        {
            emoteId = NormalizeEmoteId(emoteId);
            if (!IsValidEmoteId(emoteId))
                return;

            EmoteDefinition emote;
            if (!EmoteCatalog.TryGet(emoteId, out emote))
                return;

            Body body;
            if (!TryGetBody(senderClientId, out body) || !CanStartEmote(body))
                return;

            BroadcastEmoteStart(senderClientId, emote.Id);
        }

        private void BroadcastEmoteStart(knetid emoteClientId, string emoteId)
        {
            ReceiveStartLocal(emoteClientId, emoteId);

            IReadOnlyList<knetid> destinationClientIds = GetClientIdsExceptHost();
            if (destinationClientIds == null || destinationClientIds.Count == 0)
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(EmoteStartMsg);
            writer.Put(emoteClientId);
            writer.Put(emoteId ?? "", true);

            Net.Server_SendToClients(
                DeliveryMethod.ReliableUnordered,
                writer,
                destinationClientIds);
        }

        private void ReceiveStartLocal(knetid emoteClientId, string emoteId)
        {
            if (ScavPlayer.LocalPlayer != null && emoteClientId == ScavPlayer.LocalPlayer.playerId)
            {
                _pendingClientRequest = null;
                CatPatchHostCompatibility.MarkHostResponded();
            }

            EmoteDefinition emote;
            if (!EmoteCatalog.TryGet(emoteId, out emote))
                return;

            _visualController.Start(emoteClientId, emote);
        }

        private void CleanupPendingClientRequest()
        {
            if (_pendingClientRequest == null)
                return;

            if (Time.realtimeSinceStartupAsDouble < _pendingClientRequest.ExpiresAt)
                return;

            _pendingClientRequest = null;
            CatPatchHostCompatibility.MarkHostMissing("Emotes");
        }

        private static bool CanStartEmote(Body body)
        {
            return body != null
                && body.alive
                && body.conscious
                && !body.sleeping
                && body.standing
                && body.currentClimbable == null
                && body.attackCooldown <= 0f;
        }

        private static bool TryGetBody(knetid clientId, out Body body)
        {
            body = null;

            ScavPlayer player;
            return ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out player, out body)
                && body != null;
        }

        private static string NormalizeEmoteId(string emoteId)
        {
            return (emoteId ?? "").Trim().ToLowerInvariant();
        }

        private static bool IsValidEmoteId(string emoteId)
        {
            if (string.IsNullOrWhiteSpace(emoteId) || emoteId.Length > MaxEmoteIdLength)
                return false;

            for (int i = 0; i < emoteId.Length; i++)
            {
                char c = emoteId[i];
                bool valid = (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '_'
                    || c == '-';

                if (!valid)
                    return false;
            }

            return true;
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

        private static bool NetworkHandlersStillRegistered()
        {
            return CatPatchNetwork.IsRegistered(true, EmoteRequestMsg)
                && CatPatchNetwork.IsRegistered(false, EmoteStartMsg);
        }

        private static bool HandlerDictionaryContains(string fieldName, ushort messageId)
        {
            try
            {
                FieldInfo field = typeof(Net).GetField(
                    fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

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
                MethodInfo method = typeof(Net).GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (method == null)
                    return;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2)
                    return;

                Delegate convertedHandler = Delegate.CreateDelegate(
                    parameters[1].ParameterType,
                    handler.Target,
                    handler.Method);

                object[] args = parameters.Length >= 3
                    ? new object[] { messageId, convertedHandler, false }
                    : new object[] { messageId, convertedHandler };

                method.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    "Could not register " + methodName + " for emote msg " + messageId + ": " + ex.Message);
            }
        }

        private sealed class PendingClientEmoteRequest
        {
            public string EmoteId;
            public double ExpiresAt;
        }
    }
}
