using Together;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class KissNetworkController
    {
        private const double RegistrationVerifyIntervalSeconds = 2.0;

        private static KissNetworkController _instance;
        private static bool _handlersRegistered;
        private static double _nextRegistrationVerifyAt;

        private readonly KissPromptState _promptState;
        private readonly KissVisualController _visualController;
        private readonly KissDialogue _dialogue = new KissDialogue();
        private readonly Dictionary<knetid, PendingKissRequest> _pendingRequestsByTarget =
            new Dictionary<knetid, PendingKissRequest>();
        private PendingClientKissRequest _pendingClientRequest;

        public KissNetworkController(KissPromptState promptState, KissVisualController visualController)
        {
            _promptState = promptState;
            _visualController = visualController;
        }

        public static void SetInstance(KissNetworkController instance)
        {
            _instance = instance;
        }

        public void ResetRegistration()
        {
            _handlersRegistered = false;
            _nextRegistrationVerifyAt = 0.0;
            _pendingClientRequest = null;
            CatPatchHostCompatibility.Reset();
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

            CatPatchNetwork.RegisterServer(KissSettings.RequestMsg, OnServerRequestReceived);
            CatPatchNetwork.RegisterServer(KissSettings.ResponseMsg, OnServerResponseReceived);
            CatPatchNetwork.RegisterServer(KissSettings.UnavailableMsg, OnServerUnavailableReceived);

            CatPatchNetwork.RegisterClient(KissSettings.PromptMsg, OnClientPromptReceived);
            CatPatchNetwork.RegisterClient(KissSettings.StartMsg, OnClientStartReceived);
            CatPatchNetwork.RegisterClient(KissSettings.AlertMsg, OnClientAlertReceived);
        }

        public void Tick()
        {
            CleanupExpiredRequests();
            CleanupPendingClientRequest();
        }

        public void SendRequest(knetid targetClientId)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null)
                return;

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerRequest(localPlayer.playerId, targetClientId);
                return;
            }

            if (!CatPatchHostCompatibility.CanSendHostFeature("Kisses"))
                return;

            ScavPlayer targetPlayer = ScavPlayer.GetPlayerFromClientId(targetClientId);
            _pendingClientRequest = new PendingClientKissRequest
            {
                TargetClientId = targetClientId,
                TargetName = SafePlayerName(targetPlayer),
                ExpiresAt = Time.realtimeSinceStartupAsDouble + KissSettings.RequestAckTimeoutSeconds
            };

            NetDataWriter writer = CatPatchNetwork.CreateWriter(KissSettings.RequestMsg);
            writer.Put(targetClientId);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        public void SendResponse(knetid requesterClientId, bool accepted)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null)
                return;

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerResponse(localPlayer.playerId, requesterClientId, accepted);
                return;
            }

            if (!CatPatchHostCompatibility.CanSendHostFeature("Kisses"))
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(KissSettings.ResponseMsg);
            writer.Put(requesterClientId);
            writer.Put(accepted);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        private static void OnServerRequestReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid targetClientId;
            reader.Get(out targetClientId);

            if (_instance != null)
                _instance.HandleServerRequest(senderClientId, targetClientId);
        }

        private static void OnServerResponseReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid requesterClientId;
            bool accepted;
            reader.Get(out requesterClientId);
            reader.Get(out accepted);

            if (_instance != null)
                _instance.HandleServerResponse(senderClientId, requesterClientId, accepted);
        }

        private static void OnServerUnavailableReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid requesterClientId;
            reader.Get(out requesterClientId);

            if (_instance != null)
                _instance.HandleServerUnavailable(senderClientId, requesterClientId);
        }

        private static void OnClientPromptReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid requesterClientId;
            string requesterName;
            reader.Get(out requesterClientId);
            reader.Get(out requesterName, true);

            if (_instance != null)
                _instance.ReceivePromptLocal(requesterClientId, requesterName);
        }

        private static void OnClientStartReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid clientA;
            knetid clientB;
            float duration;
            string kissLine;
            reader.Get(out clientA);
            reader.Get(out clientB);
            reader.Get(out duration);
            reader.Get(out kissLine, true);

            if (_instance != null)
                _instance.ReceiveStartLocal(clientA, clientB, duration, kissLine);
        }

        private static void OnClientAlertReceived(knetid senderClientId, ref NetDataReader reader)
        {
            string message;
            reader.Get(out message, true);
            HugController.ShowLocalMessage(message);
        }

        private void HandleServerRequest(knetid requesterClientId, knetid targetClientId)
        {
            CleanupExpiredRequests();

            ScavPlayer requester;
            Body requesterBody;
            ScavPlayer target;
            Body targetBody;
            if (!HugValidation.CanStartHugWithinDistance(
                    requesterClientId,
                    targetClientId,
                    KissSettings.MaxAcceptedDistance,
                    out requester,
                    out requesterBody,
                    out target,
                    out targetBody))
            {
                return;
            }

            _pendingRequestsByTarget[targetClientId] = new PendingKissRequest
            {
                RequesterClientId = requesterClientId,
                TargetClientId = targetClientId,
                RequesterName = SafePlayerName(requester),
                TargetName = SafePlayerName(target),
                ExpiresAt = Time.realtimeSinceStartupAsDouble + KissSettings.PromptLifetimeSeconds
            };

            SendPromptToClient(targetClientId, requesterClientId, SafePlayerName(requester));
        }

        private void HandleServerResponse(knetid targetClientId, knetid requesterClientId, bool accepted)
        {
            CleanupExpiredRequests();

            PendingKissRequest pending;
            if (!_pendingRequestsByTarget.TryGetValue(targetClientId, out pending))
                return;

            if (pending.RequesterClientId != requesterClientId)
                return;

            _pendingRequestsByTarget.Remove(targetClientId);

            if (!accepted)
            {
                SendAlertToClient(requesterClientId, pending.TargetName + " declined the bit.");
                return;
            }

            StartKissOnServer(requesterClientId, targetClientId);
        }

        private void HandleServerUnavailable(knetid targetClientId, knetid requesterClientId)
        {
            CleanupExpiredRequests();

            PendingKissRequest pending;
            if (!_pendingRequestsByTarget.TryGetValue(targetClientId, out pending))
                return;

            if (pending.RequesterClientId != requesterClientId)
                return;

            _pendingRequestsByTarget.Remove(targetClientId);
            SendAlertToClient(
                requesterClientId,
                pending.TargetName + " does not have a kiss key bound.");
        }

        private void StartKissOnServer(knetid clientA, knetid clientB)
        {
            ScavPlayer playerA;
            Body bodyA;
            ScavPlayer playerB;
            Body bodyB;
            if (!HugValidation.CanStartHugWithinDistance(
                    clientA,
                    clientB,
                    KissSettings.MaxAcceptedDistance,
                    out playerA,
                    out bodyA,
                    out playerB,
                    out bodyB))
            {
                return;
            }

            string kissLine = _dialogue.ChooseLine();
            SendStartToAllClients(clientA, clientB, KissSettings.DurationSeconds, kissLine);
        }

        private void SendPromptToClient(knetid destinationClientId, knetid requesterClientId, string requesterName)
        {
            if (IsLocalClient(destinationClientId))
            {
                ReceivePromptLocal(requesterClientId, requesterName);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(KissSettings.PromptMsg);
            writer.Put(requesterClientId);
            writer.Put(requesterName ?? "", true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, new List<knetid> { destinationClientId });
        }

        private void SendStartToAllClients(knetid clientA, knetid clientB, float duration, string kissLine)
        {
            ReceiveStartLocal(clientA, clientB, duration, kissLine);

            IReadOnlyList<knetid> destinationClientIds = GetClientIdsExceptHost();
            if (destinationClientIds == null || destinationClientIds.Count == 0)
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(KissSettings.StartMsg);
            writer.Put(clientA);
            writer.Put(clientB);
            writer.Put(duration);
            writer.Put(kissLine ?? "", true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, destinationClientIds);
        }

        private void SendAlertToClient(knetid destinationClientId, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (IsLocalClient(destinationClientId))
            {
                HugController.ShowLocalMessage(message);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(KissSettings.AlertMsg);
            writer.Put(message, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, new List<knetid> { destinationClientId });
        }

        private void ReceivePromptLocal(knetid requesterClientId, string requesterName)
        {
            CatPatchHostCompatibility.MarkHostResponded();
            _pendingClientRequest = null;

            if (!CatPatchKeybinds.IsBound(CatPatchKeybinds.KissAction))
            {
                SendKissUnavailable(requesterClientId);
                return;
            }

            _promptState.Set(requesterClientId, requesterName);
            HugController.ShowLocalMessage(
                _promptState.RequesterName
                + " wants to kiss. Press "
                + CatPatchKeybinds.GetBindName(CatPatchKeybinds.KissAction, KeyCode.None)
                + " to accept or "
                + CatPatchKeybinds.GetBindName(CatPatchKeybinds.HugRejectAction, CatPatchKeybinds.FallbackHugRejectKey)
                + " to reject.");
        }

        private void SendKissUnavailable(knetid requesterClientId)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null)
                return;

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerUnavailable(localPlayer.playerId, requesterClientId);
                return;
            }

            if (!CatPatchHostCompatibility.CanSendHostFeature("Kisses"))
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(KissSettings.UnavailableMsg);
            writer.Put(requesterClientId);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        private void ReceiveStartLocal(knetid clientA, knetid clientB, float duration, string kissLine)
        {
            CatPatchHostCompatibility.MarkHostResponded();
            _pendingClientRequest = null;
            _promptState.Clear();
            _visualController.Start(clientA, clientB, duration);
            _dialogue.PlayLineDelayed(clientA, kissLine, 1.0f);
        }

        private void CleanupExpiredRequests()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            List<knetid> expiredTargets = new List<knetid>();

            foreach (KeyValuePair<knetid, PendingKissRequest> pair in _pendingRequestsByTarget)
            {
                if (pair.Value == null || now >= pair.Value.ExpiresAt)
                    expiredTargets.Add(pair.Key);
            }

            for (int i = 0; i < expiredTargets.Count; i++)
                _pendingRequestsByTarget.Remove(expiredTargets[i]);
        }

        private void CleanupPendingClientRequest()
        {
            if (_pendingClientRequest == null)
                return;

            if (Time.realtimeSinceStartupAsDouble < _pendingClientRequest.ExpiresAt)
                return;

            _pendingClientRequest = null;
            CatPatchHostCompatibility.MarkHostMissing("Kisses");
        }

        private static bool IsLocalClient(knetid clientId)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            return localPlayer != null && localPlayer.playerId == clientId;
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

        private static string SafePlayerName(ScavPlayer player)
        {
            if (player == null || string.IsNullOrWhiteSpace(player.playerName))
                return "Player";

            return player.playerName;
        }

        private static bool NetworkHandlersStillRegistered()
        {
            return CatPatchNetwork.IsRegistered(true, KissSettings.RequestMsg)
                && CatPatchNetwork.IsRegistered(true, KissSettings.ResponseMsg)
                && CatPatchNetwork.IsRegistered(true, KissSettings.UnavailableMsg)
                && CatPatchNetwork.IsRegistered(false, KissSettings.PromptMsg)
                && CatPatchNetwork.IsRegistered(false, KissSettings.StartMsg)
                && CatPatchNetwork.IsRegistered(false, KissSettings.AlertMsg);
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

        private static void LegacyRegisterNetReceiver(string methodName, ushort messageId, KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate handler)
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
                object[] args = parameters.Length >= 3 ? new object[] { messageId, convertedHandler, false } : new object[] { messageId, convertedHandler };
                method.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Could not register " + methodName + " for kiss msg " + messageId + ": " + ex.Message);
            }
        }

        private sealed class PendingKissRequest
        {
            public knetid RequesterClientId;
            public knetid TargetClientId;
            public string RequesterName;
            public string TargetName;
            public double ExpiresAt;
        }

        private sealed class PendingClientKissRequest
        {
            public knetid TargetClientId;
            public string TargetName;
            public double ExpiresAt;
        }
    }
}
