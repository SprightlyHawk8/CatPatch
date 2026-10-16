using System;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Together;
using UnityEngine;

namespace CatPatchspace
{
    /// <summary>
    /// First-stage Together v5 host migration probe. This deliberately validates
    /// the offer flow and a compact in-world migration snapshot before any Steam
    /// ownership or transport state is changed.
    /// </summary>
    internal sealed class HostTransferController : MonoBehaviour
    {
        private const string LogPrefix = "[HostTransferV5] ";
        private const int ProtocolVersion = 1;
        private const int MaxPlayers = 224;
        private const double OfferTimeoutSeconds = 30.0;
        private const double SnapshotTimeoutSeconds = 30.0;

        private static HostTransferController _instance;
        private static string _outgoingTransferId = "";
        private static knetid _outgoingTargetId = CatPatchNetId.None;
        private static ulong _outgoingTargetSteamId;
        private static string _outgoingTargetName = "";
        private static double _outgoingExpiresAt;

        private static string _incomingTransferId = "";
        private static string _incomingHostName = "";
        private static ulong _incomingLobbyId;
        private static ulong _incomingHostSteamId;
        private static bool _incomingAccepted;
        private static double _incomingExpiresAt;

        private static string _statusText = "";
        private static bool _statusImportant;
        private static double _statusUntil;

        private void Awake()
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);
            RegisterHandlers();
            LogInfo("controller initialized protocol=" + ProtocolVersion +
                    " mode=diagnostic-snapshot noAuthoritySwitch=true");
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Update()
        {
            RegisterHandlers();

            double now = Time.realtimeSinceStartupAsDouble;
            if (!string.IsNullOrEmpty(_incomingTransferId) && now >= _incomingExpiresAt)
            {
                bool wasAccepted = _incomingAccepted;
                LogWarning("incoming offer timed out id=" + _incomingTransferId);
                ClearIncomingOffer();
                SetStatus(wasAccepted
                    ? "Host transfer snapshot timed out."
                    : "Host transfer request expired.", true);
            }

            if (!string.IsNullOrEmpty(_outgoingTransferId) && now >= _outgoingExpiresAt)
            {
                LogWarning("outgoing transfer timed out id=" + _outgoingTransferId);
                ClearOutgoingTransfer();
                SetStatus("Host transfer probe timed out.", true);
            }
        }

        private void OnGUI()
        {
            if (!string.IsNullOrEmpty(_incomingTransferId) && !_incomingAccepted)
                DrawOfferWindow();

            if (!string.IsNullOrWhiteSpace(_statusText) &&
                Time.realtimeSinceStartupAsDouble < _statusUntil)
                DrawStatusOverlay();
        }

        internal static bool TryOfferTransfer(ulong clientId, string playerName, out string message)
        {
            message = "";
            LogInfo("offer requested targetClient=" + clientId + " targetName=" + (playerName ?? ""));

            if (_instance == null)
            {
                message = "Host transfer controller is not ready.";
                return RejectOffer(message);
            }

            if (!Net.IsRunning || !Net.IsHost)
            {
                message = "Host transfer requires an active player-hosted session.";
                return RejectOffer(message);
            }

            TransportSteamworks transport;
            if (!Net.TryGetSteamTransport(out transport) || transport == null || !KSteam.IS_IN_LOBBY)
            {
                message = "Host transfer currently requires a Steam lobby.";
                return RejectOffer(message);
            }

            if (!Util.IsInWorld() || !Util.IsWorldGenerated())
            {
                message = "Wait for the world to finish loading before transferring host.";
                return RejectOffer(message);
            }

            if (clientId == 0UL || clientId >= ushort.MaxValue)
            {
                message = "Select a connected remote player.";
                return RejectOffer(message);
            }

            if (!string.IsNullOrEmpty(_outgoingTransferId) || !string.IsNullOrEmpty(_incomingTransferId))
            {
                message = "Another host transfer probe is already active.";
                return RejectOffer(message);
            }

            knetid targetId = new knetid((ushort)clientId);
            ScavPlayer target;
            if (!ScavPlayer.TryGetPlayerFromClientId(targetId, out target) || target == null ||
                target.SteamId == 0UL)
            {
                message = "Could not resolve the selected player's Steam identity.";
                return RejectOffer(message);
            }

            _outgoingTransferId = Guid.NewGuid().ToString("N");
            _outgoingTargetId = targetId;
            _outgoingTargetSteamId = target.SteamId;
            _outgoingTargetName = string.IsNullOrWhiteSpace(playerName) ? target.playerName : playerName.Trim();
            _outgoingExpiresAt = Time.realtimeSinceStartupAsDouble + OfferTimeoutSeconds;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferOffer);
            writer.Put(ProtocolVersion);
            writer.Put(_outgoingTransferId, true);
            writer.Put(GetLocalPlayerName(), true);
            writer.Put(KSteam.LobbyId.m_SteamID);
            writer.Put(_outgoingTargetSteamId);
            Net.Server_SendToClients(
                DeliveryMethod.ReliableOrdered,
                writer,
                new List<knetid> { targetId });

            LogInfo("offer sent id=" + _outgoingTransferId +
                    " targetClient=" + targetId +
                    " targetSteam=" + _outgoingTargetSteamId +
                    " lobby=" + KSteam.LobbyId.m_SteamID);
            message = "Host transfer probe sent to " + _outgoingTargetName + ".";
            SetStatus(message, false);
            return true;
        }

        internal static bool TryOfferTransfer(knetid clientId, string playerName, out string message)
        {
            return TryOfferTransfer(CatPatchNetId.ToUlong(clientId), playerName, out message);
        }

        private static bool RejectOffer(string message)
        {
            LogWarning("offer rejected: " + message);
            return false;
        }

        private static void RegisterHandlers()
        {
            CatPatchNetwork.RegisterServer(NetworkMessageIds.HostTransferResponse, OnServerResponse);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.HostTransferOffer, OnClientOffer);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.HostTransferSnapshotBegin, OnClientSnapshot);
            CatPatchNetwork.RegisterServer(NetworkMessageIds.HostTransferSnapshotAck, OnServerSnapshotAck);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.HostTransferAbort, OnClientAbort);
        }

        private static void OnClientOffer(knetid sender, ref NetDataReader reader)
        {
            try
            {
                int protocol = reader.GetInt();
                string transferId = ReadString(ref reader);
                string hostName = ReadString(ref reader);
                ulong lobbyId = reader.GetULong();
                ulong intendedSteamId = reader.GetULong();
                ulong localSteamId = KSteam.GetLocalUserSteamID().m_SteamID;
                ulong currentLobbyId = KSteam.LobbyId.m_SteamID;
                ulong currentOwnerSteamId = KSteam.CURRENT_LOBBY.ownerID.m_SteamID;

                if (protocol != ProtocolVersion || string.IsNullOrWhiteSpace(transferId) || lobbyId == 0UL)
                {
                    LogWarning("malformed/incompatible offer protocol=" + protocol + " id=" + transferId);
                    return;
                }

                if (intendedSteamId != localSteamId)
                {
                    LogWarning("offer target mismatch id=" + transferId +
                               " intendedSteam=" + intendedSteamId + " localSteam=" + localSteamId);
                    return;
                }

                if (lobbyId != currentLobbyId || currentOwnerSteamId == 0UL)
                {
                    LogWarning("offer lobby/owner mismatch id=" + transferId +
                               " offeredLobby=" + lobbyId + " currentLobby=" + currentLobbyId +
                               " currentOwnerSteam=" + currentOwnerSteamId);
                    return;
                }

                _incomingTransferId = transferId;
                _incomingHostName = string.IsNullOrWhiteSpace(hostName) ? "The host" : hostName;
                _incomingLobbyId = lobbyId;
                _incomingHostSteamId = currentOwnerSteamId;
                _incomingAccepted = false;
                _incomingExpiresAt = Time.realtimeSinceStartupAsDouble + OfferTimeoutSeconds;
                SetStatus("Host transfer request received.", false);
                LogInfo("offer received id=" + transferId + " host=" + _incomingHostName +
                        " lobby=" + lobbyId + " targetSteam=" + intendedSteamId);
            }
            catch (Exception ex)
            {
                LogError("offer decode failed", ex);
            }
        }

        private static void SendOfferResponse(bool accepted)
        {
            if (string.IsNullOrEmpty(_incomingTransferId))
                return;

            string transferId = _incomingTransferId;
            if (accepted)
            {
                _incomingAccepted = true;
                _incomingExpiresAt = Time.realtimeSinceStartupAsDouble + SnapshotTimeoutSeconds;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferResponse);
            writer.Put(ProtocolVersion);
            writer.Put(transferId, true);
            writer.Put(accepted);
            writer.Put(KSteam.GetLocalUserSteamID().m_SteamID);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);

            LogInfo("offer response sent id=" + transferId + " accepted=" + accepted);
            SetStatus(accepted
                ? "Accepted. Validating the v5 migration snapshot..."
                : "Host transfer declined.", false);
            if (!accepted)
                ClearIncomingOffer();
        }

        private static void OnServerResponse(knetid sender, ref NetDataReader reader)
        {
            try
            {
                int protocol = reader.GetInt();
                string transferId = ReadString(ref reader);
                bool accepted = reader.GetBool();
                ulong responderSteamId = reader.GetULong();

                if (protocol != ProtocolVersion || transferId != _outgoingTransferId ||
                    !CatPatchNetId.Same(sender, _outgoingTargetId) ||
                    responderSteamId != _outgoingTargetSteamId)
                {
                    LogWarning("ignored unmatched response id=" + transferId + " sender=" + sender +
                               " steam=" + responderSteamId + " protocol=" + protocol);
                    return;
                }

                LogInfo("offer response received id=" + transferId + " sender=" + sender +
                        " accepted=" + accepted);
                if (!accepted)
                {
                    SetStatus(_outgoingTargetName + " declined the host transfer.", false);
                    ClearOutgoingTransfer();
                    return;
                }

                _outgoingExpiresAt = Time.realtimeSinceStartupAsDouble + SnapshotTimeoutSeconds;
                SendDiagnosticSnapshot(transferId, sender);
            }
            catch (Exception ex)
            {
                LogError("response decode failed", ex);
                AbortOutgoing("Could not process the transfer response.");
            }
        }

        private static void SendDiagnosticSnapshot(string transferId, knetid targetId)
        {
            try
            {
                LastBeforeGenerationState generationState =
                    WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams;
                string runSettings = WorldMP.CompileRunSettings() ?? "";
                List<ScavPlayer> players = CapturePlayers();

                bool hasQolSeed;
                int qolSeed;
                string qolSeedInput;
                CaptureOptionalQolSeed(out hasQolSeed, out qolSeed, out qolSeedInput);

                NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferSnapshotBegin);
                writer.Put(ProtocolVersion);
                writer.Put(transferId, true);
                writer.Put(KSteam.LobbyId.m_SteamID);
                writer.Put(KSteam.GetLocalUserSteamID().m_SteamID);
                writer.Put(_outgoingTargetSteamId);
                writer.Put(generationState);
                writer.Put(runSettings, true);
                writer.Put(hasQolSeed);
                writer.Put(qolSeed);
                writer.Put(qolSeedInput ?? "", true);
                writer.Put(players.Count);

                for (int i = 0; i < players.Count; i++)
                {
                    ScavPlayer player = players[i];
                    Vector2 position = player.body != null ? player.body.transform.position : player.pos;
                    writer.Put(player.playerId.id);
                    writer.Put(player.SteamId);
                    writer.Put(player.playerName ?? "", true);
                    writer.Put(position.x);
                    writer.Put(position.y);
                    writer.Put(player.body != null);
                }

                Net.Server_SendToClients(
                    DeliveryMethod.ReliableOrdered,
                    writer,
                    new List<knetid> { targetId });

                string fingerprint = DescribeSnapshot(generationState, runSettings, players.Count,
                    hasQolSeed, qolSeed);
                LogInfo("diagnostic snapshot sent id=" + transferId + " " + fingerprint);
                for (int i = 0; i < players.Count; i++)
                {
                    ScavPlayer player = players[i];
                    Vector2 position = player.body != null ? player.body.transform.position : player.pos;
                    LogInfo("snapshot player id=" + player.playerId + " steam=" + player.SteamId +
                            " name=" + (player.playerName ?? "") + " pos=" + position);
                }
                SetStatus("Migration snapshot sent; waiting for validation...", false);
            }
            catch (Exception ex)
            {
                LogError("diagnostic snapshot capture/send failed id=" + transferId, ex);
                AbortOutgoing("Could not capture the v5 migration snapshot: " + ex.Message);
            }
        }

        private static void OnClientSnapshot(knetid sender, ref NetDataReader reader)
        {
            string transferId = "";
            try
            {
                int protocol = reader.GetInt();
                transferId = ReadString(ref reader);
                ulong lobbyId = reader.GetULong();
                ulong sourceSteamId = reader.GetULong();
                ulong targetSteamId = reader.GetULong();

                if (protocol != ProtocolVersion)
                    throw new InvalidOperationException("Unsupported protocol " + protocol + ".");
                if (!_incomingAccepted || string.IsNullOrWhiteSpace(_incomingTransferId))
                    throw new InvalidOperationException("No accepted host transfer is awaiting a snapshot.");
                if (string.IsNullOrWhiteSpace(transferId) || transferId != _incomingTransferId)
                    throw new InvalidOperationException("Transfer identity mismatch.");
                if (lobbyId == 0UL || lobbyId != _incomingLobbyId || lobbyId != KSteam.LobbyId.m_SteamID)
                    throw new InvalidOperationException("Lobby identity mismatch.");
                if (sourceSteamId == 0UL || sourceSteamId != _incomingHostSteamId ||
                    sourceSteamId != KSteam.CURRENT_LOBBY.ownerID.m_SteamID)
                    throw new InvalidOperationException("Host Steam identity mismatch.");
                if (targetSteamId != KSteam.GetLocalUserSteamID().m_SteamID)
                    throw new InvalidOperationException("Snapshot was intended for another Steam user.");

                LastBeforeGenerationState generationState;
                reader.Get(out generationState);
                string runSettings = ReadString(ref reader);
                bool hasQolSeed = reader.GetBool();
                int qolSeed = reader.GetInt();
                string qolSeedInput = ReadString(ref reader);
                int playerCount = reader.GetInt();

                if (playerCount <= 0 || playerCount > MaxPlayers)
                    throw new InvalidOperationException("Invalid player count " + playerCount + ".");

                HashSet<ushort> playerIds = new HashSet<ushort>();
                HashSet<ulong> steamIds = new HashSet<ulong>();
                bool targetFound = false;
                for (int i = 0; i < playerCount; i++)
                {
                    ushort playerId = reader.GetUShort();
                    ulong steamId = reader.GetULong();
                    string playerName = ReadString(ref reader);
                    float x = reader.GetFloat();
                    float y = reader.GetFloat();
                    bool hasBody = reader.GetBool();

                    if (!playerIds.Add(playerId))
                        throw new InvalidOperationException("Duplicate player id " + playerId + ".");
                    if (steamId != 0UL && !steamIds.Add(steamId))
                        throw new InvalidOperationException("Duplicate Steam id " + steamId + ".");
                    if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
                        throw new InvalidOperationException("Invalid position for player " + playerId + ".");

                    targetFound |= steamId == targetSteamId;
                    LogInfo("snapshot received player id=" + playerId + " steam=" + steamId +
                            " name=" + playerName + " pos=(" + x + ", " + y + ") body=" + hasBody);
                }

                if (!targetFound)
                    throw new InvalidOperationException("Receiving player was absent from the snapshot.");

                string fingerprint = DescribeSnapshot(generationState, runSettings, playerCount,
                    hasQolSeed, qolSeed);
                LogInfo("diagnostic snapshot validated id=" + transferId + " sourceSteam=" +
                        sourceSteamId + " targetSteam=" + targetSteamId + " " + fingerprint +
                        " qolInput=" + qolSeedInput);
                SendSnapshotAck(transferId, true, fingerprint);
                ClearIncomingOffer();
                SetStatus("Snapshot validated. Prototype stopped before authority switch.", false, 12.0);
            }
            catch (Exception ex)
            {
                LogError("diagnostic snapshot rejected id=" + transferId, ex);
                SendSnapshotAck(transferId, false, ex.Message);
                if (!string.IsNullOrEmpty(transferId) && transferId == _incomingTransferId)
                    ClearIncomingOffer();
                SetStatus("Migration snapshot rejected: " + ex.Message, true, 12.0);
            }
        }

        private static void SendSnapshotAck(string transferId, bool success, string detail)
        {
            if (!Net.IsRunning || Net.IsServer)
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferSnapshotAck);
            writer.Put(ProtocolVersion);
            writer.Put(transferId ?? "", true);
            writer.Put(success);
            writer.Put(detail ?? "", true);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            LogInfo("snapshot ack sent id=" + transferId + " success=" + success + " detail=" + detail);
        }

        private static void OnServerSnapshotAck(knetid sender, ref NetDataReader reader)
        {
            try
            {
                int protocol = reader.GetInt();
                string transferId = ReadString(ref reader);
                bool success = reader.GetBool();
                string detail = ReadString(ref reader);
                if (protocol != ProtocolVersion || transferId != _outgoingTransferId ||
                    !CatPatchNetId.Same(sender, _outgoingTargetId))
                {
                    LogWarning("ignored unmatched snapshot ack id=" + transferId + " sender=" + sender);
                    return;
                }

                LogInfo("snapshot ack received id=" + transferId + " success=" + success +
                        " detail=" + detail);
                if (success)
                {
                    SetStatus("V5 migration snapshot validated by " + _outgoingTargetName +
                              ". Authority was not switched in this prototype.", false, 14.0);
                }
                else
                {
                    SetStatus("V5 migration snapshot was rejected: " + detail, true, 14.0);
                }
                ClearOutgoingTransfer();
            }
            catch (Exception ex)
            {
                LogError("snapshot ack decode failed", ex);
                AbortOutgoing("Could not read the snapshot acknowledgement.");
            }
        }

        private static void AbortOutgoing(string reason)
        {
            string transferId = _outgoingTransferId;
            knetid targetId = _outgoingTargetId;
            if (!string.IsNullOrEmpty(transferId) && !CatPatchNetId.IsNone(targetId) && Net.IsServer)
            {
                try
                {
                    NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferAbort);
                    writer.Put(ProtocolVersion);
                    writer.Put(transferId, true);
                    writer.Put(reason ?? "Host transfer probe aborted.", true);
                    Net.Server_SendToClients(
                        DeliveryMethod.ReliableOrdered,
                        writer,
                        new List<knetid> { targetId });
                }
                catch (Exception ex)
                {
                    LogError("abort notification failed id=" + transferId, ex);
                }
            }

            LogWarning("probe aborted id=" + transferId + " reason=" + reason);
            ClearOutgoingTransfer();
            SetStatus(reason, true, 12.0);
        }

        private static void OnClientAbort(knetid sender, ref NetDataReader reader)
        {
            try
            {
                int protocol = reader.GetInt();
                string transferId = ReadString(ref reader);
                string reason = ReadString(ref reader);
                LogWarning("abort received protocol=" + protocol + " id=" + transferId +
                           " reason=" + reason);
                if (transferId == _incomingTransferId)
                    ClearIncomingOffer();
                SetStatus("Host transfer cancelled: " + reason, true, 12.0);
            }
            catch (Exception ex)
            {
                LogError("abort decode failed", ex);
            }
        }

        private static List<ScavPlayer> CapturePlayers()
        {
            List<ScavPlayer> players = new List<ScavPlayer>();
            foreach (KeyValuePair<knetid, ScavPlayer> pair in ScavPlayer.ClientIdToPlayerDict)
            {
                if (pair.Value != null && !players.Contains(pair.Value))
                    players.Add(pair.Value);
            }

            players.Sort(delegate(ScavPlayer left, ScavPlayer right)
            {
                return left.playerId.id.CompareTo(right.playerId.id);
            });
            if (players.Count <= 0 || players.Count > MaxPlayers)
                throw new InvalidOperationException("Cannot snapshot " + players.Count + " players.");
            return players;
        }

        private static void CaptureOptionalQolSeed(out bool hasSeed, out int seed, out string input)
        {
            hasSeed = false;
            seed = 0;
            input = "";
            try
            {
                Type type = AccessType("QoL_Unknown.SeedManager");
                if (type == null)
                {
                    LogInfo("optional QoL seed metadata: mod/type not present");
                    return;
                }

                FieldInfo isSeededField = type.GetField("IsSeeded", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo seedField = type.GetField("CurrentSeed", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo inputField = type.GetField("InputString", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                hasSeed = isSeededField != null && (bool)isSeededField.GetValue(null);
                if (hasSeed && seedField != null)
                    seed = (int)seedField.GetValue(null);
                if (hasSeed && inputField != null)
                    input = inputField.GetValue(null) as string ?? "";
                LogInfo("optional QoL seed metadata captured active=" + hasSeed + " seed=" + seed +
                        " inputLength=" + input.Length);
            }
            catch (Exception ex)
            {
                hasSeed = false;
                seed = 0;
                input = "";
                LogWarning("optional QoL seed metadata capture failed: " + ex.Message);
            }
        }

        private static Type AccessType(string fullName)
        {
            Type type = Type.GetType(fullName, false);
            if (type != null)
                return type;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(fullName, false);
                if (type != null)
                    return type;
            }
            return null;
        }

        private static string DescribeSnapshot(LastBeforeGenerationState state, string runSettings,
            int playerCount, bool hasQolSeed, int qolSeed)
        {
            return "players=" + playerCount +
                   " biome=" + state.biomeOverride +
                   " depth=" + state.biomeDepth +
                   " traveled=" + state.totalTraveled +
                   " runSettingsChars=" + (runSettings == null ? 0 : runSettings.Length) +
                   " worldStateBytes=" + LastBeforeGenerationState.GetByteSize() +
                   " qolSeedActive=" + hasQolSeed +
                   (hasQolSeed ? " qolSeed=" + qolSeed : "");
        }

        private static string GetLocalPlayerName()
        {
            ScavPlayer player = ScavPlayer.LocalPlayer;
            if (player != null && !string.IsNullOrWhiteSpace(player.playerName))
                return player.playerName;
            if (!string.IsNullOrWhiteSpace(UIMainMenu.USERINPUT_NAME))
                return UIMainMenu.USERINPUT_NAME;
            return "Host";
        }

        private static string ReadString(ref NetDataReader reader)
        {
            string value;
            reader.Get(out value, true);
            return value ?? "";
        }

        private static void ClearOutgoingTransfer()
        {
            _outgoingTransferId = "";
            _outgoingTargetId = CatPatchNetId.None;
            _outgoingTargetSteamId = 0UL;
            _outgoingTargetName = "";
            _outgoingExpiresAt = 0.0;
        }

        private static void ClearIncomingOffer()
        {
            _incomingTransferId = "";
            _incomingHostName = "";
            _incomingLobbyId = 0UL;
            _incomingHostSteamId = 0UL;
            _incomingAccepted = false;
            _incomingExpiresAt = 0.0;
        }

        private static void DrawOfferWindow()
        {
            float width = 560f;
            float height = 240f;
            Rect rect = new Rect((Screen.width - width) * 0.5f,
                (Screen.height - height) * 0.5f, width, height);
            GUI.depth = -32000;
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 18f, rect.y + 16f, rect.width - 36f, 28f),
                "Host transfer request (v5 prototype)");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 52f, rect.width - 36f, 105f),
                _incomingHostName + " wants to test transferring this session to you.\n\n" +
                "This build validates the world seed/settings and player locations. " +
                "It will not disconnect anyone or change the host yet.");
            if (GUI.Button(new Rect(rect.x + 18f, rect.yMax - 60f, 248f, 42f),
                "Accept test snapshot"))
                SendOfferResponse(true);
            if (GUI.Button(new Rect(rect.xMax - 266f, rect.yMax - 60f, 248f, 42f), "Decline"))
                SendOfferResponse(false);
        }

        private static void DrawStatusOverlay()
        {
            Rect rect = new Rect((Screen.width - 680f) * 0.5f, 24f, 680f, 54f);
            Color old = GUI.color;
            GUI.depth = -31990;
            GUI.color = _statusImportant
                ? new Color(0.45f, 0.08f, 0.08f, 0.96f)
                : new Color(0.04f, 0.14f, 0.11f, 0.96f);
            GUI.Box(rect, GUIContent.none);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 12f, rect.y + 15f, rect.width - 24f, 24f), _statusText);
            GUI.color = old;
        }

        private static void SetStatus(string message, bool important, double seconds = 8.0)
        {
            _statusText = message ?? "";
            _statusImportant = important;
            _statusUntil = Time.realtimeSinceStartupAsDouble + seconds;
        }

        private static void LogInfo(string message)
        {
            if (Plugin.Logger != null)
                Plugin.Logger.LogInfo(LogPrefix + message);
        }

        private static void LogWarning(string message)
        {
            if (Plugin.Logger != null)
                Plugin.Logger.LogWarning(LogPrefix + message);
        }

        private static void LogError(string message, Exception ex)
        {
            if (Plugin.Logger != null)
                Plugin.Logger.LogError(LogPrefix + message + ": " + ex);
        }
    }
}
