using Together;
using LiteNetLib.Utils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class HugNetworkController
    {
        private const double RegistrationVerifyIntervalSeconds = 2.0;

        private static HugNetworkController _instance;
        private static bool _handlersRegistered;
        private static double _nextRegistrationVerifyAt;
        private const string LegacyUpdateMessage = "This server uses a newer CatPatch version. Please update CatPatch to use hugs here.";
        private readonly HugPositiveEffects _positiveEffects = new HugPositiveEffects();
        private readonly HugPromptState _promptState;
        private readonly HugVisualController _visualController;
        private readonly TalkerCatPatch _talkerCatPatch = new TalkerCatPatch();
        private readonly Dictionary<knetid, PendingServerRequest> _pendingRequestsByTarget = new Dictionary<knetid, PendingServerRequest>();
        private PendingClientRequest _pendingClientRequest;

        public HugNetworkController(HugPromptState promptState, HugVisualController visualController)
        {
            _promptState = promptState;
            _visualController = visualController;
        }

        public static void SetInstance(HugNetworkController instance)
        {
            _instance = instance;
        }

        public void ResetRegistration()
        {
            _handlersRegistered = false;
            _nextRegistrationVerifyAt = 0.0;
            ClearPendingClientRequest();
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

            CatPatchNetwork.RegisterServer(HugSettings.HugRequestMsg, OnServerRequestReceived);
            CatPatchNetwork.RegisterServer(HugSettings.HugResponseMsg, OnServerResponseReceived);
            CatPatchNetwork.RegisterServer(HugSettings.HugPromptAckMsg, OnServerPromptAckReceived);
            TryRegisterLegacyWarningHandler();

            CatPatchNetwork.RegisterClient(HugSettings.HugPromptMsg, OnClientPromptReceived);
            CatPatchNetwork.RegisterClient(HugSettings.HugStartMsg, OnClientStartReceived);
            CatPatchNetwork.RegisterClient(HugSettings.HugAlertMsg, OnClientAlertReceived);
        }

        private static bool NetworkHandlersStillRegistered()
        {
            return CatPatchNetwork.IsRegistered(true, HugSettings.HugRequestMsg)
                && CatPatchNetwork.IsRegistered(true, HugSettings.HugResponseMsg)
                && CatPatchNetwork.IsRegistered(true, HugSettings.HugPromptAckMsg)
                && CatPatchNetwork.IsRegistered(false, HugSettings.HugPromptMsg)
                && CatPatchNetwork.IsRegistered(false, HugSettings.HugStartMsg)
                && CatPatchNetwork.IsRegistered(false, HugSettings.HugAlertMsg);
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

            if (!CatPatchHostCompatibility.CanSendHostFeature("Hugs"))
                return;

            ScavPlayer targetPlayer = ScavPlayer.GetPlayerFromClientId(targetClientId);
            _pendingClientRequest = new PendingClientRequest
            {
                TargetClientId = targetClientId,
                TargetName = SafePlayerName(targetPlayer),
                ExpiresAt = Time.realtimeSinceStartupAsDouble + HugSettings.RequestAckTimeoutSeconds
            };

            NetDataWriter writer = CatPatchNetwork.CreateWriter(HugSettings.HugRequestMsg);
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

            if (!CatPatchHostCompatibility.CanSendHostFeature("Hugs"))
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(HugSettings.HugResponseMsg);
            writer.Put(requesterClientId);
            writer.Put(accepted);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
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
                Plugin.Logger.LogWarning("Could not register " + methodName + " for msg " + messageId + ": " + ex.Message);
            }
        }

        private static void TryRegisterLegacyWarningHandler()
        {
            if (CatPatchNetwork.IsRegistered(true, NetworkMessageIds.LegacyHugRequest))
                return;

            CatPatchNetwork.RegisterServer(
                NetworkMessageIds.LegacyHugRequest,
                OnServerLegacyHugRequestReceived);
        }

        private static void OnServerRequestReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid targetClientId;
            reader.Get(out targetClientId);

            if (_instance != null)
                _instance.HandleServerRequest(senderClientId, targetClientId);
        }

        private static void OnServerLegacyHugRequestReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance != null)
                _instance.SendLegacyUpdateAlertToClient(senderClientId);
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

        private static void OnServerPromptAckReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid requesterClientId;
            reader.Get(out requesterClientId);

            if (_instance != null)
                _instance.HandleServerPromptAck(senderClientId, requesterClientId);
        }

        private static void OnClientPromptReceived(knetid senderClientId, ref NetDataReader reader)
        {
            try
            {
                knetid requesterClientId;
                string requesterName;
                reader.Get(out requesterClientId);
                reader.Get(out requesterName, true);

                if (_instance != null)
                    _instance.ReceivePromptLocal(requesterClientId, requesterName);
                else
                    Plugin.Logger.LogWarning("Received hug prompt, but HugNetworkController instance is null.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("OnClientPromptReceived failed: " + ex);
            }
        }

        private static void OnClientStartReceived(knetid senderClientId, ref NetDataReader reader)
        {
            try
            {
                knetid clientA;
                knetid clientB;
                float duration;
                string hugLine;
                reader.Get(out clientA);
                reader.Get(out clientB);
                reader.Get(out duration);
                reader.Get(out hugLine, true);

                if (_instance != null)
                    _instance.ReceiveStartLocal(clientA, clientB, duration, hugLine);
                else
                    Plugin.Logger.LogWarning("Received hug start, but HugNetworkController instance is null.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("OnClientStartReceived failed: " + ex);
            }
        }

        private static void OnClientAlertReceived(knetid senderClientId, ref NetDataReader reader)
        {
            try
            {
                string message;
                reader.Get(out message, true);
                if (_instance != null)
                    _instance.ClearPendingClientRequest();
                HugController.ShowLocalMessage(message);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("OnClientAlertReceived failed: " + ex);
            }
        }

        private void HandleServerRequest(knetid requesterClientId, knetid targetClientId)
        {
            CleanupExpiredServerRequests();
            ClearPendingClientRequest();

            ScavPlayer requester;
            Body requesterBody;
            ScavPlayer target;
            Body targetBody;
            if (!HugValidation.CanStartHugWithinDistance(
                    requesterClientId,
                    targetClientId,
                    HugSettings.MaxAcceptedHugDistance,
                    out requester,
                    out requesterBody,
                    out target,
                    out targetBody))
            {
                SendAlertToClient(requesterClientId, "Cannot hug that player right now. Move closer and try again.");
                return;
            }

            _pendingRequestsByTarget[targetClientId] = new PendingServerRequest
            {
                RequesterClientId = requesterClientId,
                TargetClientId = targetClientId,
                RequesterName = string.IsNullOrWhiteSpace(requester.playerName) ? ("Player " + requesterClientId) : requester.playerName,
                ExpiresAt = Time.realtimeSinceStartupAsDouble + HugSettings.PromptLifetimeSeconds,
                PromptAckExpiresAt = Time.realtimeSinceStartupAsDouble + HugSettings.PromptAckTimeoutSeconds,
                TargetName = SafePlayerName(target)
            };

            SendPromptToClient(targetClientId, requesterClientId, requester.playerName);
        }

        private void HandleServerResponse(knetid targetClientId, knetid requesterClientId, bool accepted)
        {
            CleanupExpiredServerRequests();
            ClearPendingClientRequest();

            PendingServerRequest pending;
            if (!_pendingRequestsByTarget.TryGetValue(targetClientId, out pending))
            {
                SendAlertToClient(targetClientId, "That hug request expired.");
                return;
            }

            if (pending.RequesterClientId != requesterClientId)
                return;

            _pendingRequestsByTarget.Remove(targetClientId);

            if (!accepted)
            {
                SendAlertToClient(requesterClientId, SafePlayerName(ScavPlayer.GetPlayerFromClientId(targetClientId)) + " rejected your hug.");
                return;
            }

            StartHugOnServer(requesterClientId, targetClientId);
        }

        private void StartHugOnServer(knetid clientA, knetid clientB)
        {
            ScavPlayer playerA;
            Body bodyA;
            ScavPlayer playerB;
            Body bodyB;

            if (!HugValidation.CanStartHugWithinDistance(clientA,clientB,HugSettings.MaxAcceptedHugDistance,out playerA,out bodyA,out playerB,out bodyB))
            {
                SendAlertToClient(clientA, "Cannot start hug. You are too far apart.");
                SendAlertToClient(clientB, "Cannot start hug. You are too far apart.");
                return;
            }

            _positiveEffects.TryApplyHugMoodReward(clientA, bodyA, clientB, bodyB);
            string hugLine = _talkerCatPatch.ChooseHugAcceptedLine(bodyA, bodyB);
            SendStartToAllClients(clientA, clientB, HugSettings.DurationSeconds, hugLine);
        }

        private void SendPromptToClient(knetid destinationClientId, knetid requesterClientId, string requesterName)
        {
            if (IsLocalClient(destinationClientId))
            {
                ReceivePromptLocal(requesterClientId, requesterName);
                HandleServerPromptAck(destinationClientId, requesterClientId);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(HugSettings.HugPromptMsg);
            writer.Put(requesterClientId);
            writer.Put(requesterName ?? "", true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, new List<knetid> { destinationClientId });
        }

        private void SendStartToAllClients(knetid clientA, knetid clientB, float duration, string hugLine)
        {
            ReceiveStartLocal(clientA, clientB, duration, hugLine);

            NetDataWriter writer = CatPatchNetwork.CreateWriter(HugSettings.HugStartMsg);
            writer.Put(clientA);
            writer.Put(clientB);
            writer.Put(duration);
            writer.Put(hugLine ?? "", true);

            IReadOnlyList<knetid> destinationClientIds = GetClientIdsExceptHost();
            if (destinationClientIds == null || destinationClientIds.Count == 0)
                return;

            Net.Server_SendToClients(
                DeliveryMethod.ReliableUnordered,
                writer,
                destinationClientIds);
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

            NetDataWriter writer = CatPatchNetwork.CreateWriter(HugSettings.HugAlertMsg);
            writer.Put(message, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, new List<knetid> { destinationClientId });
        }

        private void SendLegacyUpdateAlertToClient(knetid destinationClientId)
        {
            if (IsLocalClient(destinationClientId))
            {
                HugController.ShowLocalMessage(LegacyUpdateMessage);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.LegacyHugAlert);
            writer.Put(LegacyUpdateMessage, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, new List<knetid> { destinationClientId });
        }

        private void ReceivePromptLocal(knetid requesterClientId, string requesterName)
        {
            CatPatchHostCompatibility.MarkHostResponded();
            _promptState.Set(requesterClientId, requesterName);
            SendPromptAck(requesterClientId);
            HugController.ShowLocalMessage(
                _promptState.RequesterName
                + " wants to hug. Press "
                + CatPatchKeybinds.GetBindName(CatPatchKeybinds.HugAction, HugSettings.FallbackHugKey)
                + " to accept or "
                + CatPatchKeybinds.GetBindName(CatPatchKeybinds.HugRejectAction, CatPatchKeybinds.FallbackHugRejectKey)
                + " to reject.");
        }

        private void ReceiveStartLocal(knetid clientA, knetid clientB, float duration, string hugLine)
        {
            CatPatchHostCompatibility.MarkHostResponded();
            ClearPendingClientRequest();
            _promptState.Clear();
            _visualController.Start(clientA, clientB, duration);
            _talkerCatPatch.PlayHugAcceptedLine(clientA, hugLine);
        }

        public void Tick()
        {
            CleanupExpiredServerRequests();
            CleanupPendingClientRequest();
        }

        private void SendPromptAck(knetid requesterClientId)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null || KrokoshaScavMultiplayer.is_server)
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(HugSettings.HugPromptAckMsg);
            writer.Put(requesterClientId);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        private void HandleServerPromptAck(knetid targetClientId, knetid requesterClientId)
        {
            PendingServerRequest pending;
            if (!_pendingRequestsByTarget.TryGetValue(targetClientId, out pending))
                return;

            if (pending.RequesterClientId != requesterClientId || pending.PromptAcknowledged)
                return;

            pending.PromptAcknowledged = true;
            SendAlertToClient(requesterClientId, "Hug request delivered to " + pending.TargetName + ".");
        }

        private void ClearPendingClientRequest()
        {
            _pendingClientRequest = null;
        }

        private void CleanupPendingClientRequest()
        {
            if (_pendingClientRequest == null)
                return;

            if (Time.realtimeSinceStartupAsDouble < _pendingClientRequest.ExpiresAt)
                return;

            string targetName = _pendingClientRequest.TargetName;
            _pendingClientRequest = null;
            CatPatchHostCompatibility.MarkHostMissing("Hugs");
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

        private void CleanupExpiredServerRequests()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            List<knetid> expiredTargets = new List<knetid>();

            foreach (KeyValuePair<knetid, PendingServerRequest> pair in _pendingRequestsByTarget)
            {
                PendingServerRequest pending = pair.Value;
                if (pending == null)
                {
                    expiredTargets.Add(pair.Key);
                    continue;
                }

                if (!pending.PromptAcknowledged && now >= pending.PromptAckExpiresAt)
                {
                    SendAlertToClient(pending.RequesterClientId, "No hug response from " + pending.TargetName + ". Encourage them to get Catpatch :)");
                    expiredTargets.Add(pair.Key);
                    continue;
                }

                if (now >= pending.ExpiresAt)
                    expiredTargets.Add(pair.Key);
            }

            for (int i = 0; i < expiredTargets.Count; i++)
                _pendingRequestsByTarget.Remove(expiredTargets[i]);
        }

        private sealed class PendingServerRequest
        {
            public knetid RequesterClientId;
            public knetid TargetClientId;
            public string RequesterName;
            public string TargetName;
            public double ExpiresAt;
            public double PromptAckExpiresAt;
            public bool PromptAcknowledged;
        }

        private sealed class PendingClientRequest
        {
            public knetid TargetClientId;
            public string TargetName;
            public double ExpiresAt;
        }
    }
}
