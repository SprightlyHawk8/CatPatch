using System;
using System.Collections.Generic;
using System.Reflection;
using Together;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class SprayNetworkController
    {
        private const ushort SpraySpawnMsg = NetworkMessageIds.SpraySpawn;
        private const ushort SprayServerAckMsg = NetworkMessageIds.SprayServerAck;
        private const ushort SprayClientAckMsg = NetworkMessageIds.SprayClientAck;
        private const ushort SprayAlertMsg = NetworkMessageIds.SprayAlert;
        private const ushort SprayRejectMsg = NetworkMessageIds.SprayReject;
        private const ushort SprayClearMsg = NetworkMessageIds.SprayClear;
        private const int MaxSprayIdLength = 128;
        private const double ServerAckTimeoutSeconds = 3.0;
        private const double ClientAckTimeoutSeconds = 3.0;
        private const double RegistrationVerifyIntervalSeconds = 2.0;

        private static SprayNetworkController _instance;
        private static bool _handlersRegistered;
        private static double _nextRegistrationVerifyAt;

        private readonly SprayVisualController _visualController;
        private readonly SprayDownloadCache _downloadCache = new SprayDownloadCache();
        private readonly HashSet<string> _localClientSprayKeys = new HashSet<string>();
        private readonly Dictionary<string, PendingServerSprayAck> _pendingServerAcks = new Dictionary<string, PendingServerSprayAck>();
        private readonly Dictionary<string, PendingClientSprayAcks> _pendingClientAcks = new Dictionary<string, PendingClientSprayAcks>();
        private readonly Dictionary<knetid, double> _nextAllowedServerSprayByClientId = new Dictionary<knetid, double>();
        private int _layerGeneration;

        public SprayNetworkController(SprayVisualController visualController)
        {
            _visualController = visualController;
            _instance = this;
        }

        public void ResetRegistration()
        {
            _handlersRegistered = false;
            _nextRegistrationVerifyAt = 0.0;
            CatPatchHostCompatibility.Reset();
        }

        public void Tick()
        {
            _downloadCache.Tick();
            CleanupPendingAcks();
        }

        public void ClearLayerState()
        {
            _layerGeneration++;
            _localClientSprayKeys.Clear();
            _pendingServerAcks.Clear();
            _pendingClientAcks.Clear();
            _nextAllowedServerSprayByClientId.Clear();
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

            CatPatchNetwork.RegisterServer(SpraySpawnMsg, OnServerSprayReceived);
            CatPatchNetwork.RegisterServer(SprayClientAckMsg, OnServerSprayClientAckReceived);

            CatPatchNetwork.RegisterClient(SpraySpawnMsg, OnClientSprayReceived);
            CatPatchNetwork.RegisterClient(SprayServerAckMsg, OnClientSprayServerAckReceived);
            CatPatchNetwork.RegisterClient(SprayAlertMsg, OnClientSprayAlertReceived);
            CatPatchNetwork.RegisterClient(SprayRejectMsg, OnClientSprayRejectReceived);
            CatPatchNetwork.RegisterClient(SprayClearMsg, OnClientSprayClearReceived);
        }

        public void SendOrSpawnSpray(string sprayId, byte[] localImageBytes, Vector2 position, string sourceFilePath = "")
        {
            sprayId = NormalizeSprayId(sprayId);
            if (!IsValidSprayId(sprayId))
            {
                Plugin.Logger.LogWarning("Spray rejected before send: invalid spray id.");
                return;
            }

            if (KrokoshaScavMultiplayer.is_server)
            {
                knetid ownerClientId = ScavPlayer.LocalPlayer != null ? ScavPlayer.LocalPlayer.playerId : new knetid(0);
                SpawnLocalSprayFromBestSource(sprayId, localImageBytes, position, ownerClientId, sourceFilePath);
                BroadcastSprayToClients(ownerClientId, sprayId, position);
                return;
            }

            if (!CatPatchHostCompatibility.CanSendHostFeature("Public sprays"))
                return;

            knetid localClientId = ScavPlayer.LocalPlayer != null ? ScavPlayer.LocalPlayer.playerId : new knetid(0);
            if (SpawnLocalSprayFromBestSource(sprayId, localImageBytes, position, localClientId, sourceFilePath))
                _localClientSprayKeys.Add(BuildSprayInstanceKey(sprayId, position));

            SendSprayToServer(sprayId, position);
        }

        private void SendSprayToServer(string sprayId, Vector2 position)
        {
            _pendingServerAcks[BuildSprayInstanceKey(sprayId, position)] = new PendingServerSprayAck
            {
                SprayId = sprayId,
                Position = position,
                ExpiresAt = Time.realtimeSinceStartupAsDouble + ServerAckTimeoutSeconds
            };

            NetDataWriter writer = CreateSprayWriter(sprayId, position, 0UL);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

            Plugin.Logger.LogInfo("Sent spray request to host: " + sprayId);
        }

        private static void OnServerSprayReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            string sprayId;
            Vector2 position;
            if (!TryReadSprayPacket(ref reader, out sprayId, out position))
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            double nextAllowedAt;
            if (_instance._nextAllowedServerSprayByClientId.TryGetValue(senderClientId, out nextAllowedAt) && now < nextAllowedAt)
            {
                int remainingSeconds = Mathf.CeilToInt((float)(nextAllowedAt - now));
                _instance.SendSprayReject(senderClientId, sprayId, position, "Host spray cooldown: " + remainingSeconds + "s remaining.");
                Plugin.Logger.LogInfo("Rejected spray from client " + senderClientId + " for host cooldown: " + remainingSeconds + "s remaining.");
                return;
            }

            int mutedSeconds = CatPatchHostModerationMenu.GetHostSprayMuteRemainingSeconds(senderClientId);
            if (mutedSeconds > 0)
            {
                _instance.SendSprayReject(senderClientId, sprayId, position, "Your sprays are muted by the host for " + mutedSeconds + "s.");
                Plugin.Logger.LogInfo("Rejected muted spray from client " + senderClientId + ": " + mutedSeconds + "s remaining.");
                return;
            }

            _instance._nextAllowedServerSprayByClientId[senderClientId] = now + CatPatchServerSettings.SprayCooldownSeconds;
            _instance.SpawnLocalSprayFromBackend(sprayId, position, senderClientId);
            _instance.SendSprayServerAck(senderClientId, sprayId, position);
            _instance.BroadcastSprayToClients(senderClientId, sprayId, position);

            Plugin.Logger.LogInfo("Host received and broadcast spray from client " + senderClientId + ": " + sprayId);
        }

        private void BroadcastSprayToClients(knetid ownerClientId, string sprayId, Vector2 position)
        {
            IReadOnlyList<knetid> destinationClientIds = GetClientIdsExceptHost();
            if (destinationClientIds == null || destinationClientIds.Count == 0)
            {
                Plugin.Logger.LogInfo("No remote clients available for spray broadcast.");
                return;
            }

            NetDataWriter writer = CreateSprayWriter(sprayId, position, ownerClientId);
            string sprayKey = BuildSprayInstanceKey(sprayId, position);
            PendingClientSprayAcks pending = new PendingClientSprayAcks
            {
                OwnerClientId = ownerClientId,
                SprayId = sprayId,
                ExpiresAt = Time.realtimeSinceStartupAsDouble + ClientAckTimeoutSeconds,
                ExpectedClientIds = new HashSet<knetid>(destinationClientIds)
            };

            Net.Server_SendToClients(
                DeliveryMethod.ReliableUnordered,
                writer,
                destinationClientIds);

            if (pending.ExpectedClientIds.Count > 0)
                _pendingClientAcks[sprayKey] = pending;

            Plugin.Logger.LogInfo(
                "Broadcast spray " + sprayId + " to " + destinationClientIds.Count + " client(s).");
        }

        private static void OnClientSprayReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            string sprayId;
            Vector2 position;
            ulong ownerClientId;
            if (!TryReadSprayPacket(ref reader, out sprayId, out position, out ownerClientId))
                return;

            _instance.SendSprayClientAck(sprayId, position);

            if (_instance.TryConsumeLocalClientSpray(sprayId, position))
                return;

            _instance.SpawnLocalSprayFromBackend(sprayId, position, ownerClientId);

            Plugin.Logger.LogInfo("Client received spray spawn: " + sprayId);
        }

        private static void OnServerSprayClientAckReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            string sprayId;
            Vector2 position;
            if (!TryReadSprayPacket(ref reader, out sprayId, out position))
                return;

            _instance.HandleSprayClientAck(senderClientId, sprayId, position);
        }

        private static void OnClientSprayServerAckReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            string sprayId;
            Vector2 position;
            if (!TryReadSprayPacket(ref reader, out sprayId, out position))
                return;

            _instance.HandleSprayServerAck(sprayId, position);
        }

        private static void OnClientSprayAlertReceived(knetid senderClientId, ref NetDataReader reader)
        {
            try
            {
                string message;
                reader.Get(out message, true);
                HugController.ShowLocalMessage(message);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Failed to read spray alert: " + ex.Message);
            }
        }

        private static void OnClientSprayRejectReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            try
            {
                string sprayId = NormalizeSprayId(reader.GetString());
                Vector2 position = new Vector2(reader.GetFloat(), reader.GetFloat());
                string message = reader.GetString();

                _instance.RemoveLocalSpray(sprayId, position);
                _instance.HandleSprayServerAck(sprayId, position);
                if (!string.IsNullOrWhiteSpace(message))
                    HugController.ShowLocalMessage(message);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Failed to read spray reject: " + ex.Message);
            }
        }

        private static void OnClientSprayClearReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            try
            {
                bool hasOwner = reader.GetBool();
                ulong ownerClientId = hasOwner ? reader.GetULong() : 0UL;
                int cleared = _instance._visualController.ClearActiveSpraysByOwner(hasOwner ? (ulong?)ownerClientId : null);
                Plugin.Logger.LogInfo("Cleared " + cleared + " spray(s) from host moderation clear.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Failed to read spray clear: " + ex.Message);
            }
        }

        private static NetDataWriter CreateSprayWriter(string sprayId, Vector2 position, ulong ownerClientId)
        {
            NetDataWriter writer = CatPatchNetwork.CreateWriter(SpraySpawnMsg);
            writer.Put(sprayId ?? "");
            writer.Put(position.x);
            writer.Put(position.y);
            writer.Put(ownerClientId);
            return writer;
        }

        private static NetDataWriter CreateSprayAckWriter(ushort messageId, string sprayId, Vector2 position)
        {
            NetDataWriter writer = CatPatchNetwork.CreateWriter(messageId);
            writer.Put(sprayId ?? "");
            writer.Put(position.x);
            writer.Put(position.y);
            return writer;
        }

        private static bool TryReadSprayPacket(ref NetDataReader reader, out string sprayId, out Vector2 position)
        {
            ulong ownerClientId;
            return TryReadSprayPacket(ref reader, out sprayId, out position, out ownerClientId);
        }

        private static bool TryReadSprayPacket(ref NetDataReader reader, out string sprayId, out Vector2 position, out ulong ownerClientId)
        {
            sprayId = "";
            position = Vector2.zero;
            ownerClientId = 0UL;

            try
            {
                sprayId = NormalizeSprayId(reader.GetString());
                if (!IsValidSprayId(sprayId))
                    return false;

                float x = reader.GetFloat();
                float y = reader.GetFloat();
                position = new Vector2(x, y);

                if (reader.AvailableBytes >= 8)
                    ownerClientId = reader.GetULong();

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Failed to read spray packet: " + ex.Message);
                return false;
            }
        }

        private bool SpawnLocalSprayFromBestSource(
    string sprayId,
    byte[] localImageBytes,
    Vector2 position,
    ulong ownerClientId,
    string sourceFilePath = "")
        {
            if (localImageBytes != null && localImageBytes.Length > 0)
            {
                return SpawnLocalSpray(sprayId, localImageBytes, position, ownerClientId, sourceFilePath);
            }

            SpawnLocalSprayFromBackend(sprayId, position, ownerClientId);
            return false;
        }

        private void SpawnLocalSprayFromBackend(string sprayId, Vector2 position, ulong ownerClientId)
        {
            if (!CatPatchClientSettings.ShowSprays)
                return;

            int layerGeneration = _layerGeneration;
            _downloadCache.GetOrRequest(
                sprayId,
                imageBytes =>
                {
                    if (layerGeneration != _layerGeneration)
                        return;

                    SpawnLocalSpray(sprayId, imageBytes, position, ownerClientId, "");
                });
        }

        private bool SpawnLocalSpray(string sprayId, byte[] imageBytes, Vector2 position, ulong ownerClientId, string sourceFilePath = "")
        {
            if (!CatPatchClientSettings.ShowSprays)
                return false;

            GameObject sprayObject;
            string error;

            if (!_visualController.TryCreateSpray(
                    sprayId,
                    imageBytes,
                    position,
                    ownerClientId,
                    out sprayObject,
                    out error))
            {
                Plugin.Logger.LogWarning("Failed to spawn spray: " + error);
                return false;
            }

            SprayInstance instance = sprayObject.GetComponent<SprayInstance>();
            if (instance != null)
            {
                instance.OwnerClientId = ownerClientId;
                instance.SourceFilePath = sourceFilePath ?? "";
            }

            return true;
        }

        internal static int TryHostClearSprays(ulong? ownerClientId, out string message)
        {
            message = "";

            if (_instance == null)
            {
                message = "Spray runtime is unavailable.";
                return -1;
            }

            if (!KrokoshaScavMultiplayer.is_server)
            {
                message = "Host only.";
                return -1;
            }

            int cleared = _instance._visualController.ClearActiveSpraysByOwner(ownerClientId);
            _instance.BroadcastSprayClear(ownerClientId);
            message = ownerClientId != null
                ? "Cleared sprays for client " + ownerClientId.Value + ": " + cleared
                : "Cleared sprays: " + cleared;

            return cleared;
        }

        private bool TryConsumeLocalClientSpray(string sprayId, Vector2 position)
        {
            string key = BuildSprayInstanceKey(sprayId, position);

            if (!_localClientSprayKeys.Contains(key))
                return false;

            _localClientSprayKeys.Remove(key);
            Plugin.Logger.LogInfo("Ignored echoed spray spawn already placed locally: " + sprayId);
            return true;
        }

        private bool RemoveLocalSpray(string sprayId, Vector2 position)
        {
            _localClientSprayKeys.Remove(BuildSprayInstanceKey(sprayId, position));
            return _visualController.ClearActiveSpray(sprayId, position);
        }

        private void SendSprayServerAck(knetid destinationClientId, string sprayId, Vector2 position)
        {
            if (IsLocalClient(destinationClientId))
            {
                HandleSprayServerAck(sprayId, position);
                return;
            }

            NetDataWriter writer = CreateSprayAckWriter(SprayServerAckMsg, sprayId, position);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, new List<knetid> { destinationClientId });
        }

        private void SendSprayReject(knetid destinationClientId, string sprayId, Vector2 position, string message)
        {
            if (IsLocalClient(destinationClientId))
            {
                RemoveLocalSpray(sprayId, position);
                HugController.ShowLocalMessage(message);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(SprayRejectMsg);
            writer.Put(sprayId ?? "");
            writer.Put(position.x);
            writer.Put(position.y);
            writer.Put(message ?? "");
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, new List<knetid> { destinationClientId });
        }

        private void BroadcastSprayClear(ulong? ownerClientId)
        {
            IReadOnlyList<knetid> destinationClientIds = GetClientIdsExceptHost();
            if (destinationClientIds == null || destinationClientIds.Count == 0)
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(SprayClearMsg);
            writer.Put(ownerClientId != null);
            if (ownerClientId != null)
                writer.Put(ownerClientId.Value);

            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, destinationClientIds);
        }

        private void SendSprayClientAck(string sprayId, Vector2 position)
        {
            if (KrokoshaScavMultiplayer.is_server)
                return;

            NetDataWriter writer = CreateSprayAckWriter(SprayClientAckMsg, sprayId, position);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        private void HandleSprayServerAck(string sprayId, Vector2 position)
        {
            CatPatchHostCompatibility.MarkHostResponded();
            _pendingServerAcks.Remove(BuildSprayInstanceKey(sprayId, position));
        }

        private void HandleSprayClientAck(knetid senderClientId, string sprayId, Vector2 position)
        {
            PendingClientSprayAcks pending;
            if (!_pendingClientAcks.TryGetValue(BuildSprayInstanceKey(sprayId, position), out pending))
                return;

            pending.ExpectedClientIds.Remove(senderClientId);
            if (pending.ExpectedClientIds.Count == 0)
                _pendingClientAcks.Remove(BuildSprayInstanceKey(sprayId, position));
        }

        private void SendSprayAlert(knetid destinationClientId, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (IsLocalClient(destinationClientId))
            {
                HugController.ShowLocalMessage(message);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(SprayAlertMsg);
            writer.Put(message, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, new List<knetid> { destinationClientId });
        }

        private void CleanupPendingAcks()
        {
            double now = Time.realtimeSinceStartupAsDouble;

            List<string> expiredServerAcks = new List<string>();
            foreach (KeyValuePair<string, PendingServerSprayAck> pair in _pendingServerAcks)
            {
                if (pair.Value == null || now >= pair.Value.ExpiresAt)
                    expiredServerAcks.Add(pair.Key);
            }

            for (int i = 0; i < expiredServerAcks.Count; i++)
            {
                _pendingServerAcks.Remove(expiredServerAcks[i]);
                CatPatchHostCompatibility.MarkHostMissing("Public sprays");
            }

            List<string> expiredClientAcks = new List<string>();
            foreach (KeyValuePair<string, PendingClientSprayAcks> pair in _pendingClientAcks)
            {
                PendingClientSprayAcks pending = pair.Value;
                if (pending == null || now < pending.ExpiresAt)
                    continue;

                expiredClientAcks.Add(pair.Key);
                if (pending.ExpectedClientIds != null && pending.ExpectedClientIds.Count > 0)
                    SendSprayAlert(pending.OwnerClientId, "Some clients did not acknowledge your spray. They may not have CatPatch installed or updated.");
            }

            for (int i = 0; i < expiredClientAcks.Count; i++)
                _pendingClientAcks.Remove(expiredClientAcks[i]);
        }

        private static string NormalizeSprayId(string sprayId)
        {
            return (sprayId ?? "").Trim().ToLowerInvariant();
        }

        private static bool IsValidSprayId(string sprayId)
        {
            if (string.IsNullOrWhiteSpace(sprayId))
                return false;

            if (sprayId.Length > MaxSprayIdLength)
                return false;

            for (int i = 0; i < sprayId.Length; i++)
            {
                char c = sprayId[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex)
                    return false;
            }

            return true;
        }

        private static string BuildSprayInstanceKey(string sprayId, Vector2 position)
        {
            return NormalizeSprayId(sprayId)
                + "|"
                + Mathf.RoundToInt(position.x * 100f)
                + "|"
                + Mathf.RoundToInt(position.y * 100f);
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

        private static bool IsLocalClient(knetid clientId)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            return localPlayer != null && localPlayer.playerId == clientId;
        }

        private static bool NetworkHandlersStillRegistered()
        {
            return CatPatchNetwork.IsRegistered(true, SpraySpawnMsg)
                && CatPatchNetwork.IsRegistered(true, SprayClientAckMsg)
                && CatPatchNetwork.IsRegistered(false, SpraySpawnMsg)
                && CatPatchNetwork.IsRegistered(false, SprayServerAckMsg)
                && CatPatchNetwork.IsRegistered(false, SprayAlertMsg)
                && CatPatchNetwork.IsRegistered(false, SprayRejectMsg)
                && CatPatchNetwork.IsRegistered(false, SprayClearMsg);
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
                    "Could not register " + methodName + " for spray msg " + messageId + ": " + ex.Message);
            }
        }

        private sealed class PendingServerSprayAck
        {
            public string SprayId;
            public Vector2 Position;
            public double ExpiresAt;
        }

        private sealed class PendingClientSprayAcks
        {
            public knetid OwnerClientId;
            public string SprayId;
            public double ExpiresAt;
            public HashSet<knetid> ExpectedClientIds;
        }
    }
}
