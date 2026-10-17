using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json.Linq;
using Steamworks;
using UnityEngine;

namespace CatPatchspace
{
    /// <summary>
    /// Experimental voluntary Steam host handoff for Together v5.
    ///
    /// The protocol transfers a verified copy of Together's multiplayer save
    /// before changing lobby ownership and rebuilding the v5 transport roles.
    /// </summary>
    internal sealed class HostTransferController : MonoBehaviour
    {
        private const string LogPrefix = "[HostTransfer] ";
        // The latest Together playtest changed the embedded start-game packet layout.
        private const int ProtocolVersion = 5;
        private const int SnapshotChunkBytes = 12 * 1024;
        private const int MaxSnapshotBytes = 64 * 1024 * 1024;
        private const string TransferPositionsFileName = "catpatch_transfer_positions.json";
        private const double OfferTimeoutSeconds = 30.0;
        private const double SnapshotTimeoutSeconds = 120.0;
        private const double NewHostStartDelaySeconds = 1.5;
        private const double ClientReconnectDelaySeconds = 0.5;
        private const double FormerHostReconnectDelaySeconds = 0.5;
        private const double ClientReconnectTimeoutSeconds = 30.0;
        private const double MainMenuTransitionTimeoutSeconds = 20.0;
        private const double PeerGatherTimeoutSeconds = 45.0;
        private const double PositionRestoreTimeoutSeconds = 45.0;
        private const double LateSpawnSettleSeconds = 0.75;
        private const double PostRestoreVerificationSeconds = 0.5;

        private static HostTransferController _instance;
        private static bool _handlersRegistered;
        private static double _nextHandlerRegistrationAttemptAt;

        private static string _hostTransferId = "";
        private static knetid _hostTargetClientId = CatPatchNetId.None;
        private static string _hostTargetName = "";
        private static ulong _hostTargetSteamId;
        private static double _hostExpiresAt;
        private static bool _hostSendingSnapshot;
        private static bool _hostCommitPrepared;
        private static readonly HashSet<ulong> HostCommitReadyClients = new HashSet<ulong>();
        private static int _hostExpectedReadyClients;
        private static ulong _hostCommitLobbyId;
        private static string _hostCommitLobbyPassword = "";
        private static bool _hostCommitHasWorldgenState;
        private static ServerMain.StartGameAnnouncementPacket _hostCommitWorldgenState;
        private static string _hostCommitRunSettings = "";
        private static QoLSeedState _hostCommitQoLSeedState = new QoLSeedState();
        private static string _hostCommitRetainedWorldProbeJson = "";
        private static int _hostRetainedProbeReports;
        private static int _hostRetainedTerrainMatches;
        private static int _hostRetainedPlayerMatches;
        private static int _hostRetainedSyncObjectMatches;

        private static string _incomingOfferId = "";
        private static string _incomingHostName = "";
        private static ulong _incomingLobbyId;
        private static ulong _incomingHostSteamId;
        private static ulong _incomingTargetSteamId;
        private static bool _incomingOfferAccepted;
        private static double _incomingOfferExpiresAt;
        private static string _verifiedIncomingTransferId = "";

        private static IncomingSnapshot _incomingSnapshot;
        private static string _statusText = "";
        private static bool _statusImportant;
        private static double _statusUntil;
        private static double _switchAt;
        private static ulong _switchLobbyId;
        private static ulong _switchOwnerSteamId;
        private static int _switchExpectedRemoteClients;
        private static string _switchLobbyPassword = "";
        private static bool _switchHasWorldgenState;
        private static ServerMain.StartGameAnnouncementPacket _switchWorldgenState;
        private static string _switchRunSettings = "";
        private static QoLSeedState _switchQoLSeedState = new QoLSeedState();
        private static string _switchRetainedWorldProbeJson = "";
        private static string _switchTransferId = "";
        private static bool _switchStarted;

        private void Awake()
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);
            RegisterHandlersOnce();
            LogInfo("controller initialized; protocol=" + ProtocolVersion +
                    " mode=full-transfer chunkBytes=" + SnapshotChunkBytes);
            LogInfo("v5 bridge compatibility: " + V5TransportBridge.DescribeCompatibility());
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Update()
        {
            RegisterHandlersOnce();

            double now = Time.realtimeSinceStartupAsDouble;
            if (!string.IsNullOrEmpty(_incomingOfferId) && now >= _incomingOfferExpiresAt)
            {
                LogWarning("incoming offer timed out id=" + _incomingOfferId);
                ClearIncomingOffer();
            }

            if (!string.IsNullOrEmpty(_hostTransferId) && now >= _hostExpiresAt && !_hostSendingSnapshot)
                AbortHostTransfer("Transfer timed out before completion.", true);

            if (!_switchStarted && _switchAt <= 0.0 && !string.IsNullOrEmpty(_switchTransferId) &&
                _switchLobbyId != 0UL && _switchOwnerSteamId != 0UL)
            {
                ulong actualOwner = SteamMatchmaking.GetLobbyOwner(new CSteamID(_switchLobbyId)).m_SteamID;
                if (actualOwner == _switchOwnerSteamId)
                {
                    KSteam.CURRENT_LOBBY.ownerID = new CSteamID(actualOwner);
                    bool becomeHost = KSteam.GetLocalUserSteamID().m_SteamID == actualOwner;
                    double delay = becomeHost ? NewHostStartDelaySeconds : ClientReconnectDelaySeconds;
                    _switchAt = now + delay;
                    LogInfo("Steam ownership confirmed id=" + _switchTransferId +
                            " owner=" + actualOwner + " switchIn=" + delay.ToString("0.0") + "s");
                }
            }

            if (_incomingSnapshot != null && now >= _incomingSnapshot.ExpiresAt)
            {
                string timedOutId = _incomingSnapshot.TransferId;
                _incomingSnapshot = null;
                SetStatus("Host transfer snapshot timed out.", true);
                LogWarning("snapshot receive timed out id=" + timedOutId);
            }

            if (!_switchStarted && _switchAt > 0.0 && now >= _switchAt)
            {
                _switchStarted = true;
                StartCoroutine(SwitchTransportCoroutine());
            }
        }

        private void OnGUI()
        {
            if (!string.IsNullOrEmpty(_incomingOfferId) && !_incomingOfferAccepted)
                DrawOfferWindow();

            if ((!string.IsNullOrWhiteSpace(_statusText) && Time.realtimeSinceStartupAsDouble < _statusUntil) ||
                _incomingSnapshot != null || _switchAt > 0.0)
            {
                DrawStatusOverlay();
            }
        }

        internal static bool TryOfferTransfer(ulong clientId, string playerName, out string message)
        {
            message = "";
            LogInfo("offer requested targetClient=" + clientId + " targetName=" + (playerName ?? ""));
            if (_instance == null)
            {
                message = "Host transfer controller is not ready.";
                LogWarning("offer rejected: " + message);
                return false;
            }

            if (!Net.IsRunning || !KrokoshaScavMultiplayer.IsNetworkActiveAndIsHost())
            {
                message = "Host transfer requires an active player-hosted lobby.";
                LogWarning("offer rejected: " + message);
                return false;
            }

            TransportSteamworks steamTransport;
            if (!Net.TryGetSteamTransport(out steamTransport) || steamTransport == null || !KSteam.IS_IN_LOBBY)
            {
                message = "Host transfer currently requires a Steam lobby.";
                LogWarning("offer rejected: " + message);
                return false;
            }

            if (clientId == 0UL || clientId > ushort.MaxValue)
            {
                message = "Select a connected remote player.";
                LogWarning("offer rejected: " + message);
                return false;
            }

            if (!string.IsNullOrEmpty(_hostTransferId) || _incomingSnapshot != null || _switchAt > 0.0)
            {
                message = "Another host transfer is already active.";
                LogWarning("offer rejected: " + message);
                return false;
            }

            knetid targetId = new knetid((ushort)clientId);
            ScavPlayer target;
            if (!ScavPlayer.TryGetPlayerFromClientId(targetId, out target) || target == null || target.SteamId == 0UL)
            {
                message = "Could not resolve the selected player's Steam identity.";
                LogWarning("offer rejected: " + message);
                return false;
            }

            _hostTransferId = Guid.NewGuid().ToString("N");
            _hostTargetClientId = targetId;
            _hostTargetName = string.IsNullOrWhiteSpace(playerName) ? target.playerName : playerName.Trim();
            _hostTargetSteamId = target.SteamId;
            _hostExpiresAt = Time.realtimeSinceStartupAsDouble + OfferTimeoutSeconds;
            _hostSendingSnapshot = false;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferOffer);
            writer.Put(ProtocolVersion);
            writer.Put(_hostTransferId, true);
            writer.Put(KrokoshaScavMultiplayer.INPUT_USERNAME ?? "Host", true);
            writer.Put(KSteam.LobbyId.m_SteamID);
            writer.Put(_hostTargetSteamId);
            writer.Put(KSteam.GetLocalUserSteamID().m_SteamID);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, new List<knetid> { targetId });

            LogInfo("offer sent id=" + _hostTransferId + " targetClient=" + targetId +
                    " targetSteam=" + _hostTargetSteamId + " lobby=" + KSteam.LobbyId.m_SteamID);
            message = "Host transfer offer sent to " + _hostTargetName + ".";
            SetStatus(message, false);
            return true;
        }

        private static void RegisterHandlersOnce()
        {
            if (_handlersRegistered && HandlersStillRegistered())
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextHandlerRegistrationAttemptAt)
                return;
            _nextHandlerRegistrationAttemptAt = now + 5.0;

            _handlersRegistered = true;
            CatPatchNetwork.RegisterServer(NetworkMessageIds.HostTransferResponse, OnServerResponse);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.HostTransferOffer, OnClientOffer);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.HostTransferSnapshotBegin, OnClientSnapshotBegin);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.HostTransferSnapshotChunk, OnClientSnapshotChunk);
            CatPatchNetwork.RegisterServer(NetworkMessageIds.HostTransferSnapshotAck, OnServerSnapshotAck);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.HostTransferCommit, OnClientCommit);
            CatPatchNetwork.RegisterServer(NetworkMessageIds.HostTransferCommitReady, OnServerCommitReady);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.HostTransferAbort, OnClientAbort);
            LogInfo("network handlers registered");
        }

        private static void OnClientOffer(knetid sender, ref NetDataReader reader)
        {
            int protocol = reader.GetInt();
            string transferId = ReadString(ref reader);
            string hostName = ReadString(ref reader);
            ulong lobbyId = reader.GetULong();
            ulong targetSteamId = reader.GetULong();
            ulong hostSteamId = reader.GetULong();
            ulong localSteamId = KSteam.GetLocalUserSteamID().m_SteamID;
            if (protocol != ProtocolVersion || string.IsNullOrWhiteSpace(transferId) || lobbyId == 0UL ||
                lobbyId != KSteam.LobbyId.m_SteamID || targetSteamId != localSteamId ||
                hostSteamId == 0UL || hostSteamId != KSteam.CURRENT_LOBBY.ownerID.m_SteamID)
            {
                LogWarning("ignored malformed or mismatched offer protocol=" + protocol + " id=" + transferId +
                           " lobby=" + lobbyId + " currentLobby=" + KSteam.LobbyId.m_SteamID +
                           " targetSteam=" + targetSteamId + " localSteam=" + localSteamId +
                           " hostSteam=" + hostSteamId +
                           " currentOwner=" + KSteam.CURRENT_LOBBY.ownerID.m_SteamID);
                return;
            }

            _incomingOfferId = transferId;
            _incomingHostName = string.IsNullOrWhiteSpace(hostName) ? "The host" : hostName;
            _incomingLobbyId = lobbyId;
            _incomingHostSteamId = hostSteamId;
            _incomingTargetSteamId = targetSteamId;
            _incomingOfferAccepted = false;
            _incomingOfferExpiresAt = Time.realtimeSinceStartupAsDouble + OfferTimeoutSeconds;
            SetStatus("Host transfer offer received.", false);
            LogInfo("offer received id=" + transferId + " host=" + _incomingHostName + " lobby=" + lobbyId);
        }

        private static void SendOfferResponse(bool accepted)
        {
            if (string.IsNullOrEmpty(_incomingOfferId))
                return;

            string transferId = _incomingOfferId;
            if (accepted)
            {
                _incomingOfferAccepted = true;
                _incomingOfferExpiresAt = Time.realtimeSinceStartupAsDouble + SnapshotTimeoutSeconds;
            }
            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferResponse);
            writer.Put(ProtocolVersion);
            writer.Put(transferId, true);
            writer.Put(accepted);
            writer.Put(KSteam.GetLocalUserSteamID().m_SteamID);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            LogInfo("offer response sent id=" + transferId + " accepted=" + accepted);
            SetStatus(accepted ? "Preparing to receive the host snapshot..." : "Host transfer declined.", false);
            if (!accepted)
                ClearIncomingOffer();
        }

        private static void OnServerResponse(knetid sender, ref NetDataReader reader)
        {
            int protocol = reader.GetInt();
            string transferId = ReadString(ref reader);
            bool accepted = reader.GetBool();
            ulong responderSteamId = reader.GetULong();
            if (protocol != ProtocolVersion || string.IsNullOrEmpty(_hostTransferId) || transferId != _hostTransferId ||
                !CatPatchNetId.Same(sender, _hostTargetClientId) || responderSteamId != _hostTargetSteamId)
            {
                LogWarning("ignored unmatched response protocol=" + protocol + " id=" + transferId + " sender=" + sender +
                           " responderSteam=" + responderSteamId);
                return;
            }

            LogInfo("offer response received id=" + transferId + " sender=" + sender + " accepted=" + accepted);
            if (!accepted)
            {
                AbortHostTransfer(_hostTargetName + " declined the host transfer.", false);
                return;
            }

            _hostExpiresAt = Time.realtimeSinceStartupAsDouble + SnapshotTimeoutSeconds;
            _hostSendingSnapshot = true;
            if (_instance != null)
                _instance.StartCoroutine(_instance.BuildAndSendSnapshotCoroutine(transferId, sender));
        }

        private IEnumerator BuildAndSendSnapshotCoroutine(string transferId, knetid targetId)
        {
            byte[] archive;
            string hash;
            try
            {
                SetStatus("Saving multiplayer run for transfer...", false);
                LogInfo("snapshot save begin id=" + transferId + " folder=" + SavesystemPatch.mpsavefolder);
                int currentLayer = WorldGeneration.world.biomeDepth;
                Dictionary<ulong, Vector2> livePositions = CaptureLiveAuthoritativePlayerPositions(transferId);
                if (livePositions.Count == 0)
                    throw new InvalidOperationException("No live authoritative player positions were available.");
                SaveSystem.SaveGame();
                archive = BuildSaveArchive(SavesystemPatch.mpsavefolder, currentLayer, livePositions);
                if (archive.Length <= 0 || archive.Length > MaxSnapshotBytes)
                    throw new InvalidOperationException("Snapshot size " + archive.Length + " is outside the supported range.");
                hash = ComputeSha256(archive);
            }
            catch (Exception ex)
            {
                LogError("snapshot build failed id=" + transferId, ex);
                AbortHostTransfer("Could not prepare transfer snapshot: " + ex.Message, true);
                yield break;
            }

            int chunkCount = (archive.Length + SnapshotChunkBytes - 1) / SnapshotChunkBytes;
            NetDataWriter begin = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferSnapshotBegin);
            begin.Put(ProtocolVersion);
            begin.Put(transferId, true);
            begin.Put(KSteam.LobbyId.m_SteamID);
            begin.Put(KSteam.GetLocalUserSteamID().m_SteamID);
            begin.Put(_hostTargetSteamId);
            begin.Put(archive.Length);
            begin.Put(SnapshotChunkBytes);
            begin.Put(chunkCount);
            begin.Put(hash, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, begin, new List<knetid> { targetId });
            LogInfo("snapshot begin sent id=" + transferId + " bytes=" + archive.Length +
                    " chunks=" + chunkCount + " sha256=" + hash);

            int lastLoggedPercent = -10;
            for (int index = 0; index < chunkCount; index++)
            {
                if (_hostTransferId != transferId)
                    yield break;

                int offset = index * SnapshotChunkBytes;
                int length = Math.Min(SnapshotChunkBytes, archive.Length - offset);
                byte[] chunk = new byte[length];
                Buffer.BlockCopy(archive, offset, chunk, 0, length);

                NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferSnapshotChunk, length + 128, true);
                writer.Put(ProtocolVersion);
                writer.Put(transferId, true);
                writer.Put(index);
                writer.PutBytesWithLength(chunk);
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, new List<knetid> { targetId });

                int percent = Mathf.FloorToInt(((index + 1f) / chunkCount) * 100f);
                SetStatus("Sending host snapshot: " + percent + "%", false);
                if (percent >= lastLoggedPercent + 10 || index == chunkCount - 1)
                {
                    lastLoggedPercent = percent;
                    LogInfo("snapshot send progress id=" + transferId + " " + percent + "% (" +
                            (index + 1) + "/" + chunkCount + ")");
                }

                if ((index & 3) == 3)
                    yield return null;
            }

            _hostSendingSnapshot = false;
            _hostExpiresAt = Time.realtimeSinceStartupAsDouble + SnapshotTimeoutSeconds;
            SetStatus("Snapshot sent; waiting for verification...", false);
            LogInfo("snapshot send complete id=" + transferId);
        }

        private static void OnClientSnapshotBegin(knetid sender, ref NetDataReader reader)
        {
            int protocol = reader.GetInt();
            string transferId = ReadString(ref reader);
            ulong lobbyId = reader.GetULong();
            ulong hostSteamId = reader.GetULong();
            ulong targetSteamId = reader.GetULong();
            int totalBytes = reader.GetInt();
            int chunkBytes = reader.GetInt();
            int chunkCount = reader.GetInt();
            string hash = ReadString(ref reader);

            if (protocol != ProtocolVersion || !_incomingOfferAccepted || string.IsNullOrWhiteSpace(_incomingOfferId) ||
                transferId != _incomingOfferId || lobbyId != _incomingLobbyId ||
                lobbyId != KSteam.LobbyId.m_SteamID || hostSteamId != _incomingHostSteamId ||
                hostSteamId != KSteam.CURRENT_LOBBY.ownerID.m_SteamID ||
                targetSteamId != _incomingTargetSteamId ||
                targetSteamId != KSteam.GetLocalUserSteamID().m_SteamID ||
                totalBytes <= 0 || totalBytes > MaxSnapshotBytes ||
                chunkBytes <= 0 || chunkBytes > 64 * 1024 || chunkCount <= 0 ||
                chunkCount != (totalBytes + chunkBytes - 1) / chunkBytes || string.IsNullOrWhiteSpace(hash))
            {
                LogWarning("rejected malformed snapshot header protocol=" + protocol + " id=" + transferId + " bytes=" + totalBytes +
                           " chunkBytes=" + chunkBytes + " chunks=" + chunkCount);
                SendSnapshotAck(transferId, false, "Invalid snapshot header.");
                return;
            }

            _incomingSnapshot = new IncomingSnapshot
            {
                TransferId = transferId,
                ExpectedBytes = totalBytes,
                ChunkBytes = chunkBytes,
                ExpectedChunks = chunkCount,
                ExpectedHash = hash,
                Chunks = new byte[chunkCount][],
                ExpiresAt = Time.realtimeSinceStartupAsDouble + SnapshotTimeoutSeconds
            };
            SetStatus("Receiving host snapshot: 0%", false);
            LogInfo("snapshot receive begin id=" + transferId + " bytes=" + totalBytes +
                    " chunks=" + chunkCount + " sha256=" + hash);
            ClearIncomingOffer();
        }

        private static void OnClientSnapshotChunk(knetid sender, ref NetDataReader reader)
        {
            int protocol = reader.GetInt();
            string transferId = ReadString(ref reader);
            int index = reader.GetInt();
            byte[] chunk = reader.GetBytesWithLength();
            IncomingSnapshot snapshot = _incomingSnapshot;
            if (protocol != ProtocolVersion || snapshot == null || transferId != snapshot.TransferId || index < 0 ||
                index >= snapshot.ExpectedChunks || chunk == null || chunk.Length == 0 ||
                chunk.Length > snapshot.ChunkBytes)
            {
                LogWarning("ignored invalid snapshot chunk protocol=" + protocol + " id=" + transferId + " index=" + index +
                           " bytes=" + (chunk == null ? -1 : chunk.Length));
                return;
            }

            if (snapshot.Chunks[index] == null)
            {
                snapshot.Chunks[index] = chunk;
                snapshot.ReceivedChunks++;
                snapshot.ReceivedBytes += chunk.Length;
            }
            snapshot.ExpiresAt = Time.realtimeSinceStartupAsDouble + SnapshotTimeoutSeconds;

            int percent = Mathf.FloorToInt((snapshot.ReceivedChunks / (float)snapshot.ExpectedChunks) * 100f);
            SetStatus("Receiving host snapshot: " + percent + "%", false);
            if (percent >= snapshot.LastLoggedPercent + 10 || snapshot.ReceivedChunks == snapshot.ExpectedChunks)
            {
                snapshot.LastLoggedPercent = percent;
                LogInfo("snapshot receive progress id=" + transferId + " " + percent + "% (" +
                        snapshot.ReceivedChunks + "/" + snapshot.ExpectedChunks + ")");
            }

            if (snapshot.ReceivedChunks != snapshot.ExpectedChunks)
                return;

            try
            {
                byte[] archive = Reassemble(snapshot);
                string actualHash = ComputeSha256(archive);
                if (!string.Equals(actualHash, snapshot.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 mismatch; expected " + snapshot.ExpectedHash + " got " + actualHash);

                InstallTransferredSave(archive, transferId);
                LogInfo("snapshot verified and installed id=" + transferId + " bytes=" + archive.Length +
                        " folder=" + SavesystemPatch.mpsavefolder);
                _verifiedIncomingTransferId = transferId;
                SetStatus("Snapshot verified; waiting for Steam ownership transfer...", false);
                SendSnapshotAck(transferId, true, "ok");
            }
            catch (Exception ex)
            {
                LogError("snapshot verification/install failed id=" + transferId, ex);
                SetStatus("Host snapshot failed verification: " + ex.Message, true);
                SendSnapshotAck(transferId, false, ex.Message);
            }
            finally
            {
                _incomingSnapshot = null;
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
            int protocol = reader.GetInt();
            string transferId = ReadString(ref reader);
            bool success = reader.GetBool();
            string detail = ReadString(ref reader);
            if (protocol != ProtocolVersion || transferId != _hostTransferId ||
                !CatPatchNetId.Same(sender, _hostTargetClientId))
            {
                LogWarning("ignored unmatched snapshot ack protocol=" + protocol +
                           " id=" + transferId + " sender=" + sender);
                return;
            }

            LogInfo("snapshot ack received id=" + transferId + " success=" + success + " detail=" + detail);
            if (!success)
            {
                AbortHostTransfer("Target rejected the snapshot: " + detail, true);
                return;
            }

            if (!TryCommitSteamOwnership(transferId, out detail))
            {
                AbortHostTransfer("Steam ownership transfer failed: " + detail, true);
                return;
            }
        }

        private static bool TryCommitSteamOwnership(string transferId, out string error)
        {
            error = "";
            try
            {
                CSteamID lobby = KSteam.LobbyId;
                CSteamID target = new CSteamID(_hostTargetSteamId);
                if (!lobby.IsValid() || !target.IsValid())
                {
                    error = "Invalid lobby or target Steam ID.";
                    return false;
                }

                NetDataWriter commit = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferCommit);
                commit.Put(ProtocolVersion);
                commit.Put(transferId, true);
                commit.Put(lobby.m_SteamID);
                commit.Put(target.m_SteamID);
                IReadOnlyList<knetid> clients = ServerMain.AllClientIdsExceptHost;
                commit.Put(clients.Count);
                string lobbyPassword = KrokoshaScavMultiplayer.INPUT_PASSWORD ?? "";
                commit.Put(lobbyPassword, true);
                ServerMain.StartGameAnnouncementPacket worldgenState;
                bool hasWorldgenState = V5TransportBridge.TryCaptureOriginalWorldgenState(out worldgenState);
                commit.Put(hasWorldgenState);
                if (hasWorldgenState)
                    commit.Put(worldgenState);
                string runSettings = WorldgenPatches.CompileRunSettings() ?? "";
                commit.Put(runSettings, true);
                QoLSeedState qoLSeedState = QoLHostTransferCompatibility.CaptureSourceSeedState();
                commit.Put(qoLSeedState.QoLPresent);
                commit.Put(qoLSeedState.IsSeeded);
                commit.Put(qoLSeedState.Seed);
                commit.Put(qoLSeedState.Input ?? "", true);
                RetainedWorldSnapshotProbe hostProbe = RetainedWorldSnapshotProbe.Capture();
                string retainedWorldProbeJson = hostProbe.ToJson();
                commit.Put(retainedWorldProbeJson, true);

                _hostCommitPrepared = true;
                HostCommitReadyClients.Clear();
                _hostExpectedReadyClients = clients.Count;
                _hostCommitLobbyId = lobby.m_SteamID;
                _hostCommitLobbyPassword = lobbyPassword;
                _hostCommitHasWorldgenState = hasWorldgenState;
                _hostCommitWorldgenState = worldgenState;
                _hostCommitRunSettings = runSettings;
                _hostCommitQoLSeedState = qoLSeedState;
                _hostCommitRetainedWorldProbeJson = retainedWorldProbeJson;
                _hostRetainedProbeReports = 0;
                _hostRetainedTerrainMatches = 0;
                _hostRetainedPlayerMatches = 0;
                _hostRetainedSyncObjectMatches = 0;
                _hostExpiresAt = Time.realtimeSinceStartupAsDouble + SnapshotTimeoutSeconds;
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, commit, clients);

                LogInfo("retained-world host probe id=" + transferId + " " + hostProbe.Describe());
                LogInfo("commit preparation broadcast id=" + transferId + " recipients=" + clients.Count +
                        " passwordForwarded=" + !string.IsNullOrEmpty(lobbyPassword) +
                        " worldgenStateForwarded=" + hasWorldgenState +
                        " qolSeed={" + qoLSeedState.Describe() + "}" +
                        " runSettingsChars=" + runSettings.Length +
                        " retainedProbeChars=" + retainedWorldProbeJson.Length);
                SetStatus("Preparing every player for the host switch...", false, 30.0);
                return true;
            }
            catch (Exception ex)
            {
                LogError("Steam ownership commit failed id=" + transferId, ex);
                error = ex.Message;
                return false;
            }
        }

        private static void OnServerCommitReady(knetid sender, ref NetDataReader reader)
        {
            int protocol = reader.GetInt();
            string transferId = ReadString(ref reader);
            ulong responderSteamId = reader.GetULong();
            bool retainedProbeCaptured = protocol == ProtocolVersion && reader.GetBool();
            bool retainedWorldShapeMatches = protocol == ProtocolVersion && reader.GetBool();
            bool retainedBlocksMatch = protocol == ProtocolVersion && reader.GetBool();
            bool retainedFluidsMatch = protocol == ProtocolVersion && reader.GetBool();
            bool retainedPlayersMatch = protocol == ProtocolVersion && reader.GetBool();
            bool retainedSyncObjectsMatch = protocol == ProtocolVersion && reader.GetBool();
            string retainedProbeDetail = protocol == ProtocolVersion ? ReadString(ref reader) : "";
            ScavPlayer responder;
            if (protocol != ProtocolVersion || !_hostCommitPrepared || transferId != _hostTransferId ||
                !ScavPlayer.TryGetPlayerFromClientId(sender, out responder) || responder == null ||
                responder.SteamId == 0UL || responder.SteamId != responderSteamId)
            {
                LogWarning("ignored unmatched commit-ready protocol=" + protocol + " id=" + transferId +
                           " sender=" + sender + " responderSteam=" + responderSteamId);
                return;
            }

            if (HostCommitReadyClients.Add(CatPatchNetId.ToUlong(sender)))
            {
                _hostRetainedProbeReports++;
                if (retainedProbeCaptured && retainedWorldShapeMatches &&
                    retainedBlocksMatch && retainedFluidsMatch)
                    _hostRetainedTerrainMatches++;
                if (retainedPlayersMatch)
                    _hostRetainedPlayerMatches++;
                if (retainedSyncObjectsMatch)
                    _hostRetainedSyncObjectMatches++;
                LogInfo("commit-ready received id=" + transferId + " sender=" + sender +
                        " steam=" + responderSteamId +
                        " ready=" + HostCommitReadyClients.Count + "/" + _hostExpectedReadyClients +
                        " retainedProbe={" + retainedProbeDetail + "}");
            }
            if (HostCommitReadyClients.Count < _hostExpectedReadyClients)
                return;

            string error;
            if (!FinalizeSteamOwnership(transferId, out error))
                AbortHostTransfer("Steam ownership transfer failed: " + error, true);
        }

        private static bool FinalizeSteamOwnership(string transferId, out string error)
        {
            error = "";
            try
            {
                CSteamID lobby = new CSteamID(_hostCommitLobbyId);
                CSteamID target = new CSteamID(_hostTargetSteamId);
                LogInfo("retained-world readiness id=" + transferId +
                        " reports=" + _hostRetainedProbeReports + "/" + _hostExpectedReadyClients +
                        " terrain=" + _hostRetainedTerrainMatches + "/" + _hostExpectedReadyClients +
                        " players=" + _hostRetainedPlayerMatches + "/" + _hostExpectedReadyClients +
                        " syncObjects=" + _hostRetainedSyncObjectMatches + "/" + _hostExpectedReadyClients +
                        " mode=diagnostic fallback=PreGen");
                LogInfo("all clients ready; calling SteamMatchmaking.SetLobbyOwner id=" + transferId +
                        " lobby=" + lobby.m_SteamID + " newOwner=" + target.m_SteamID);
                if (!SteamMatchmaking.SetLobbyOwner(lobby, target))
                {
                    error = "SetLobbyOwner returned false.";
                    return false;
                }

                KSteam.CURRENT_LOBBY.ownerID = target;
                SetStatus("Steam ownership transferred; switching network host...", false);
                ScheduleTransportSwitch(transferId, lobby.m_SteamID, target.m_SteamID,
                    _hostExpectedReadyClients, _hostCommitLobbyPassword,
                    _hostCommitHasWorldgenState, _hostCommitWorldgenState, _hostCommitRunSettings,
                    _hostCommitQoLSeedState, _hostCommitRetainedWorldProbeJson,
                    FormerHostReconnectDelaySeconds);
                ClearHostTransfer();
                return true;
            }
            catch (Exception ex)
            {
                LogError("Steam ownership finalize failed id=" + transferId, ex);
                error = ex.Message;
                return false;
            }
        }

        private static void OnClientCommit(knetid sender, ref NetDataReader reader)
        {
            int protocol = reader.GetInt();
            string transferId = ReadString(ref reader);
            ulong lobbyId = reader.GetULong();
            ulong newOwnerSteamId = reader.GetULong();
            int expectedRemoteClients = reader.GetInt();
            string lobbyPassword = ReadString(ref reader);
            bool hasWorldgenState = reader.GetBool();
            ServerMain.StartGameAnnouncementPacket worldgenState = default(ServerMain.StartGameAnnouncementPacket);
            if (hasWorldgenState)
                reader.Get(out worldgenState);
            string runSettings = ReadString(ref reader);
            QoLSeedState qoLSeedState = new QoLSeedState
            {
                QoLPresent = reader.GetBool(),
                IsSeeded = reader.GetBool(),
                Seed = reader.GetInt(),
                Input = ReadString(ref reader)
            };
            string retainedWorldProbeJson = protocol == ProtocolVersion ? ReadString(ref reader) : "";
            if (protocol != ProtocolVersion || string.IsNullOrWhiteSpace(transferId) ||
                lobbyId == 0UL || newOwnerSteamId == 0UL ||
                expectedRemoteClients < 1 || expectedRemoteClients > 200)
            {
                LogWarning("ignored malformed commit protocol=" + protocol);
                return;
            }

            LogInfo("commit received id=" + transferId + " lobby=" + lobbyId + " newOwner=" + newOwnerSteamId +
                    " expectedRemoteClients=" + expectedRemoteClients +
                    " passwordForwarded=" + !string.IsNullOrEmpty(lobbyPassword) +
                    " worldgenStateForwarded=" + hasWorldgenState +
                    " qolSeed={" + qoLSeedState.Describe() + "}" +
                    " runSettingsChars=" + runSettings.Length +
                    " retainedProbeChars=" + retainedWorldProbeJson.Length);
            bool becomeHost = KSteam.GetLocalUserSteamID().m_SteamID == newOwnerSteamId;
            if (lobbyId != KSteam.LobbyId.m_SteamID ||
                (becomeHost && transferId != _verifiedIncomingTransferId))
            {
                LogWarning("ignored unverified commit id=" + transferId + " lobby=" + lobbyId +
                           " currentLobby=" + KSteam.LobbyId.m_SteamID +
                           " verified=" + _verifiedIncomingTransferId + " becomeHost=" + becomeHost);
                return;
            }
            SetStatus("Ready for host switch; waiting for Steam ownership...", false, 30.0);
            ScheduleTransportSwitch(transferId, lobbyId, newOwnerSteamId, expectedRemoteClients, lobbyPassword,
                hasWorldgenState, worldgenState, runSettings, qoLSeedState, retainedWorldProbeJson, -1.0);

            RetainedWorldSnapshotProbe hostProbe;
            string retainedProbeParseError;
            RetainedWorldSnapshotProbe localProbe = RetainedWorldSnapshotProbe.Capture();
            RetainedWorldProbeComparison retainedComparison;
            if (RetainedWorldSnapshotProbe.TryParse(
                    retainedWorldProbeJson, out hostProbe, out retainedProbeParseError))
            {
                retainedComparison = RetainedWorldSnapshotProbe.Compare(hostProbe, localProbe);
            }
            else
            {
                retainedComparison = new RetainedWorldProbeComparison
                {
                    LocalCaptured = localProbe.Captured,
                    Summary = "host probe invalid: " + retainedProbeParseError +
                              "; local=" + localProbe.Describe()
                };
            }
            LogInfo("retained-world peer probe id=" + transferId +
                    " steam=" + KSteam.GetLocalUserSteamID().m_SteamID +
                    " {" + retainedComparison.Summary + "}");

            NetDataWriter ready = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferCommitReady);
            ready.Put(ProtocolVersion);
            ready.Put(transferId, true);
            ready.Put(KSteam.GetLocalUserSteamID().m_SteamID);
            ready.Put(retainedComparison.LocalCaptured);
            ready.Put(retainedComparison.WorldShapeMatches);
            ready.Put(retainedComparison.BlocksMatch);
            ready.Put(retainedComparison.FluidsMatch);
            ready.Put(retainedComparison.PlayersMatch);
            ready.Put(retainedComparison.SyncObjectsMatch);
            ready.Put(retainedComparison.Summary ?? "", true);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, ready);
            LogInfo("commit-ready sent id=" + transferId + " becomeHost=" + becomeHost);
            if (becomeHost)
                _verifiedIncomingTransferId = "";
        }

        private static void ScheduleTransportSwitch(string transferId, ulong lobbyId, ulong ownerSteamId,
            int expectedRemoteClients, string lobbyPassword, bool hasWorldgenState,
            ServerMain.StartGameAnnouncementPacket worldgenState, string runSettings,
            QoLSeedState qoLSeedState, string retainedWorldProbeJson, double delay)
        {
            _switchTransferId = transferId;
            _switchLobbyId = lobbyId;
            _switchOwnerSteamId = ownerSteamId;
            _switchExpectedRemoteClients = expectedRemoteClients;
            _switchLobbyPassword = lobbyPassword ?? "";
            _switchHasWorldgenState = hasWorldgenState;
            _switchWorldgenState = worldgenState;
            _switchRunSettings = runSettings ?? "";
            _switchQoLSeedState = qoLSeedState ?? new QoLSeedState();
            _switchRetainedWorldProbeJson = retainedWorldProbeJson ?? "";
            _switchAt = delay < 0.0
                ? 0.0
                : Time.realtimeSinceStartupAsDouble + Math.Max(0.5, delay);
            _switchStarted = false;
            LogInfo(delay < 0.0
                ? "transport switch prepared id=" + transferId + " awaiting Steam ownership"
                : "transport switch scheduled id=" + transferId + " in=" + delay.ToString("0.0") + "s");
        }

        private IEnumerator SwitchTransportCoroutine()
        {
            string transferId = _switchTransferId;
            ulong lobbyId = _switchLobbyId;
            ulong ownerSteamId = _switchOwnerSteamId;
            int expectedRemoteClients = _switchExpectedRemoteClients;
            string lobbyPassword = _switchLobbyPassword;
            bool hasWorldgenState = _switchHasWorldgenState;
            ServerMain.StartGameAnnouncementPacket worldgenState = _switchWorldgenState;
            string runSettings = _switchRunSettings;
            QoLSeedState qoLSeedState = _switchQoLSeedState;
            string retainedWorldProbeJson = _switchRetainedWorldProbeJson;
            bool becomeHost = KSteam.GetLocalUserSteamID().m_SteamID == ownerSteamId;
            LogInfo("transport switch begin id=" + transferId + " role=" + (becomeHost ? "host" : "client") +
                    " lobby=" + lobbyId + " owner=" + ownerSteamId);
            SetStatus(becomeHost ? "Starting as the new host..." : "Reconnecting to the new host...", false);

            yield return null;
            string error;
            if (!V5TransportBridge.TryDetachKeepingSteamLobby(out error))
            {
                TransferSwitchFailed("Could not detach old transport: " + error);
                yield break;
            }

            yield return null;
            KSteam.CURRENT_LOBBY.lobby_steamID = new CSteamID(lobbyId);
            KSteam.CURRENT_LOBBY.ownerID = new CSteamID(ownerSteamId);
            KrokoshaScavMultiplayer.INPUT_PASSWORD = lobbyPassword;
            LogInfo("reconnect credentials prepared id=" + transferId +
                    " passwordForwarded=" + !string.IsNullOrEmpty(lobbyPassword));

            SetStatus("Preparing a clean world reload without leaving the Steam lobby...", false, 30.0);
            yield return StartCoroutine(V5TransportBridge.TransitionToMainMenuKeepingLobbyCoroutine());
            if (!Util.IsInMainMenu() || PreRunScript.instance == null)
            {
                TransferSwitchFailed("Could not reach the multiplayer loading scene while retaining the lobby.");
                yield break;
            }

            KSteam.CURRENT_LOBBY.lobby_steamID = new CSteamID(lobbyId);
            KSteam.CURRENT_LOBBY.ownerID = new CSteamID(ownerSteamId);

            if (!becomeHost)
                yield return new WaitForSecondsRealtime(1.5f);

            QoLHostTransferCompatibility.BeginTransferredMultiplayerLoad(transferId, qoLSeedState);
            bool started = becomeHost
                ? V5TransportBridge.TryStartHostInCurrentLobby(out error)
                : V5TransportBridge.TryStartClientInCurrentLobby(ownerSteamId, out error);
            if (!started)
            {
                TransferSwitchFailed("Could not start new " + (becomeHost ? "host" : "client") + " transport: " + error);
                yield break;
            }

            RegisterHandlersOnce();

            if (becomeHost)
            {
                double hostReadyDeadline = Time.realtimeSinceStartupAsDouble + 10.0;
                while (ScavPlayer.LocalPlayer == null && Time.realtimeSinceStartupAsDouble < hostReadyDeadline)
                    yield return null;
                if (ScavPlayer.LocalPlayer == null)
                {
                    TransferSwitchFailed("Together did not finish creating the new host player.");
                    yield break;
                }

                SetStatus("New host ready; waiting for " + expectedRemoteClients + " peer(s) to reconnect...", false, 60.0);
                double gatherDeadline = Time.realtimeSinceStartupAsDouble + PeerGatherTimeoutSeconds;
                int connected = ServerMain.AllClientIdsExceptHost.Count;
                while (connected < expectedRemoteClients && Time.realtimeSinceStartupAsDouble < gatherDeadline)
                {
                    connected = ServerMain.AllClientIdsExceptHost.Count;
                    yield return null;
                }

                connected = ServerMain.AllClientIdsExceptHost.Count;
                LogInfo("peer gather complete id=" + transferId + " connected=" + connected +
                        " expected=" + expectedRemoteClients);
                if (connected < expectedRemoteClients)
                    LogWarning("peer gather timed out; loading transferred save with " + connected + "/" +
                               expectedRemoteClients + " remote peers connected");

                if (hasWorldgenState)
                    V5TransportBridge.ApplyTransferredWorldgenState(worldgenState, transferId);
                else
                    LogWarning("transferred worldgen state is unavailable id=" + transferId +
                               "; the new host may generate a different seed");

                V5TransportBridge.ApplyTransferredRunSettings(runSettings, transferId);

                if (!V5TransportBridge.TryLoadTransferredMultiplayerSave(out error))
                {
                    QoLHostTransferCompatibility.EndTransferredMultiplayerLoad(transferId);
                    TransferSwitchFailed("Could not load the transferred multiplayer save: " + error);
                    yield break;
                }
                SetStatus("Loading the transferred multiplayer run for everyone...", false, 30.0);
                yield return StartCoroutine(V5TransportBridge.RestoreTransferredPlayerPositionsCoroutine(transferId));
                LogPostReloadTerrainVerification(transferId, retainedWorldProbeJson, "host");
                QoLHostTransferCompatibility.EndTransferredMultiplayerLoad(transferId);
            }
            else
            {
                double connectionDeadline = Time.realtimeSinceStartupAsDouble + ClientReconnectTimeoutSeconds;
                while (ScavPlayer.LocalPlayer == null && Time.realtimeSinceStartupAsDouble < connectionDeadline)
                    yield return null;

                if (ScavPlayer.LocalPlayer == null)
                {
                    TransferSwitchFailed("The new host transport started, but the player handshake did not finish within " +
                                         ClientReconnectTimeoutSeconds.ToString("0") + " seconds.");
                    yield break;
                }

                LogInfo("client handshake complete id=" + transferId + " clientId=" +
                        ScavPlayer.LocalPlayer.playerId + " hostSteam=" + ownerSteamId);
                SetStatus("Reconnected; waiting for the new host to load the transferred run...", false, 30.0);

                double worldReloadDeadline = Time.realtimeSinceStartupAsDouble + PositionRestoreTimeoutSeconds;
                while ((!Util.IsInWorld() || !Util.IsWorldGenerated()) &&
                       Time.realtimeSinceStartupAsDouble < worldReloadDeadline)
                    yield return null;
                if (Util.IsInWorld() && Util.IsWorldGenerated())
                {
                    yield return new WaitForSecondsRealtime((float)LateSpawnSettleSeconds);
                    LogPostReloadTerrainVerification(transferId, retainedWorldProbeJson, "client");
                }
                else
                {
                    LogWarning("client post-reload verification timed out id=" + transferId);
                }
                QoLHostTransferCompatibility.EndTransferredMultiplayerLoad(transferId);
            }

            _switchAt = 0.0;
            _switchTransferId = "";
            _switchLobbyId = 0UL;
            _switchOwnerSteamId = 0UL;
            _switchExpectedRemoteClients = 0;
            _switchLobbyPassword = "";
            _switchHasWorldgenState = false;
            _switchWorldgenState = default(ServerMain.StartGameAnnouncementPacket);
            _switchRunSettings = "";
            _switchQoLSeedState = new QoLSeedState();
            _switchRetainedWorldProbeJson = "";
            SetStatus(becomeHost
                ? "Host transfer complete. Loading as the new host."
                : "Host transfer complete. Reconnected to the new host.", false, 12.0);
            LogInfo("transport switch complete id=" + transferId + " role=" + (becomeHost ? "host" : "client"));
        }

        private static void LogPostReloadTerrainVerification(
            string transferId, string sourceProbeJson, string role)
        {
            RetainedWorldSnapshotProbe sourceProbe;
            string parseError;
            RetainedWorldSnapshotProbe reloadedProbe = RetainedWorldSnapshotProbe.Capture();
            if (!RetainedWorldSnapshotProbe.TryParse(sourceProbeJson, out sourceProbe, out parseError))
            {
                LogWarning("post-reload terrain verification unavailable id=" + transferId +
                           " role=" + role + " sourceProbeError=" + parseError +
                           " reloaded={" + reloadedProbe.Describe() + "}");
                return;
            }

            RetainedWorldProbeComparison comparison =
                RetainedWorldSnapshotProbe.Compare(sourceProbe, reloadedProbe);
            bool terrainMatches = comparison.LocalCaptured &&
                                  comparison.WorldShapeMatches &&
                                  comparison.BlocksMatch &&
                                  comparison.FluidsMatch;
            string message = "post-reload terrain verification id=" + transferId +
                             " role=" + role +
                             " terrain=" + (terrainMatches ? "match" : "DIFF") +
                             " {" + comparison.Summary + "}";
            if (terrainMatches)
                LogInfo(message);
            else
                LogWarning(message);
        }

        private static void TransferSwitchFailed(string message)
        {
            QoLHostTransferCompatibility.EndTransferredMultiplayerLoad(_switchTransferId);
            _switchAt = 0.0;
            _switchTransferId = "";
            _switchLobbyId = 0UL;
            _switchOwnerSteamId = 0UL;
            _switchExpectedRemoteClients = 0;
            _switchLobbyPassword = "";
            _switchHasWorldgenState = false;
            _switchWorldgenState = default(ServerMain.StartGameAnnouncementPacket);
            _switchRunSettings = "";
            _switchQoLSeedState = new QoLSeedState();
            _switchRetainedWorldProbeJson = "";
            SetStatus(message + " The transferred multiplayer save was preserved for recovery.", true, 20.0);
            LogWarning("transport switch failed: " + message);
        }

        private static void OnClientAbort(knetid sender, ref NetDataReader reader)
        {
            int protocol = reader.GetInt();
            string transferId = ReadString(ref reader);
            string reason = ReadString(ref reader);
            if (protocol != ProtocolVersion)
            {
                LogWarning("ignored incompatible abort protocol=" + protocol + " id=" + transferId);
                return;
            }
            if (_incomingSnapshot != null && _incomingSnapshot.TransferId == transferId)
                _incomingSnapshot = null;
            if (_incomingOfferId == transferId)
                ClearIncomingOffer();
            if (_verifiedIncomingTransferId == transferId)
                _verifiedIncomingTransferId = "";
            if (_switchTransferId == transferId && !_switchStarted)
            {
                _switchAt = 0.0;
                _switchTransferId = "";
                _switchLobbyId = 0UL;
                _switchOwnerSteamId = 0UL;
                _switchExpectedRemoteClients = 0;
                _switchLobbyPassword = "";
                _switchHasWorldgenState = false;
                _switchWorldgenState = default(ServerMain.StartGameAnnouncementPacket);
                _switchRunSettings = "";
                _switchQoLSeedState = new QoLSeedState();
                _switchRetainedWorldProbeJson = "";
            }
            SetStatus("Host transfer cancelled: " + reason, true);
            LogWarning("abort received id=" + transferId + " reason=" + reason);
        }

        private static void AbortHostTransfer(string reason, bool important)
        {
            string transferId = _hostTransferId;
            knetid target = _hostTargetClientId;
            if (!string.IsNullOrEmpty(transferId) && !CatPatchNetId.IsNone(target) && Net.IsRunning && Net.IsServer)
            {
                try
                {
                    NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.HostTransferAbort);
                    writer.Put(ProtocolVersion);
                    writer.Put(transferId, true);
                    writer.Put(reason ?? "Cancelled", true);
                    IReadOnlyList<knetid> recipients = _hostCommitPrepared
                        ? ServerMain.AllClientIdsExceptHost
                        : new List<knetid> { target };
                    Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, recipients);
                }
                catch (Exception ex)
                {
                    LogError("failed sending abort id=" + transferId, ex);
                }
            }
            LogWarning("host transfer aborted id=" + transferId + " reason=" + reason);
            SetStatus(reason, important);
            ClearHostTransfer();
        }

        private static void ClearHostTransfer()
        {
            _hostTransferId = "";
            _hostTargetClientId = CatPatchNetId.None;
            _hostTargetName = "";
            _hostTargetSteamId = 0UL;
            _hostExpiresAt = 0.0;
            _hostSendingSnapshot = false;
            _hostCommitPrepared = false;
            HostCommitReadyClients.Clear();
            _hostExpectedReadyClients = 0;
            _hostCommitLobbyId = 0UL;
            _hostCommitLobbyPassword = "";
            _hostCommitHasWorldgenState = false;
            _hostCommitWorldgenState = default(ServerMain.StartGameAnnouncementPacket);
            _hostCommitRunSettings = "";
            _hostCommitQoLSeedState = new QoLSeedState();
            _hostCommitRetainedWorldProbeJson = "";
            _hostRetainedProbeReports = 0;
            _hostRetainedTerrainMatches = 0;
            _hostRetainedPlayerMatches = 0;
            _hostRetainedSyncObjectMatches = 0;
        }

        private static void ClearIncomingOffer()
        {
            _incomingOfferId = "";
            _incomingHostName = "";
            _incomingLobbyId = 0UL;
            _incomingHostSteamId = 0UL;
            _incomingTargetSteamId = 0UL;
            _incomingOfferAccepted = false;
            _incomingOfferExpiresAt = 0.0;
        }

        private static Dictionary<ulong, Vector2> CaptureLiveAuthoritativePlayerPositions(string transferId)
        {
            Dictionary<ulong, Vector2> positions = new Dictionary<ulong, Vector2>();
            foreach (ScavPlayer player in new List<ScavPlayer>(ScavPlayer.ClientIdToPlayerDict.Values))
            {
                if (player == null || player.SteamId == 0UL || player.body == null)
                    continue;

                Vector3 live = player.body.transform.position;
                if (float.IsNaN(live.x) || float.IsNaN(live.y) ||
                    float.IsInfinity(live.x) || float.IsInfinity(live.y))
                    continue;

                Vector2 position = new Vector2(live.x, live.y);
                positions[player.SteamId] = position;
                LogInfo("live position captured id=" + transferId +
                        " steam=" + player.SteamId +
                        " client=" + player.playerId +
                        " position=(" + position.x.ToString("0.00") +
                        ", " + position.y.ToString("0.00") + ")");
            }
            LogInfo("live authoritative position snapshot id=" + transferId +
                    " players=" + positions.Count);
            return positions;
        }

        private static byte[] BuildSaveArchive(string root, int currentLayer,
            Dictionary<ulong, Vector2> livePositions)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new DirectoryNotFoundException("Multiplayer save folder was not created: " + root);

            using (MemoryStream stream = new MemoryStream())
            {
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                    for (int i = 0; i < files.Length; i++)
                    {
                        string file = Path.GetFullPath(files[i]);
                        if (!file.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                            continue;
                        string relative = file.Substring(rootFull.Length).Replace(Path.DirectorySeparatorChar, '/');
                        if (string.Equals(relative, TransferPositionsFileName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        ZipArchiveEntry entry = archive.CreateEntry(relative, System.IO.Compression.CompressionLevel.Fastest);
                        using (Stream output = entry.Open())
                        {
                            if (string.Equals(Path.GetFileName(file), "save.sv", StringComparison.OrdinalIgnoreCase))
                            {
                                byte[] migrationSave = PinMigrationSaveToCurrentLayer(file, currentLayer);
                                output.Write(migrationSave, 0, migrationSave.Length);
                                LogInfo("snapshot layer pinned currentLayer=" + currentLayer + " entry=" + relative);
                            }
                            else
                            {
                                using (Stream input = File.OpenRead(file))
                                    input.CopyTo(output);
                            }
                        }
                    }

                    JObject serializedPositions = new JObject();
                    foreach (KeyValuePair<ulong, Vector2> entry in livePositions)
                    {
                        serializedPositions["STEAM_" + entry.Key] = new JObject
                        {
                            ["x"] = entry.Value.x,
                            ["y"] = entry.Value.y
                        };
                    }
                    JObject metadata = new JObject
                    {
                        ["schema"] = 1,
                        ["positions"] = serializedPositions
                    };
                    ZipArchiveEntry positionEntry = archive.CreateEntry(
                        TransferPositionsFileName, System.IO.Compression.CompressionLevel.Fastest);
                    using (Stream output = positionEntry.Open())
                    using (StreamWriter writer = new StreamWriter(output))
                        writer.Write(Newtonsoft.Json.JsonConvert.SerializeObject(metadata));
                }
                return stream.ToArray();
            }
        }

        private static byte[] PinMigrationSaveToCurrentLayer(string saveFile, int currentLayer)
        {
            byte[] compressed = File.ReadAllBytes(saveFile);
            JObject save = JObject.Parse(SaveSystem.Unzip(compressed));
            JToken biome = save["biome"];
            if (biome == null)
                throw new InvalidDataException("Migration save is missing its biome field: " + saveFile);

            biome.Replace(currentLayer);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(save);
            return SaveSystem.Zip(json);
        }

        private static byte[] Reassemble(IncomingSnapshot snapshot)
        {
            if (snapshot.ReceivedBytes != snapshot.ExpectedBytes)
                throw new InvalidDataException("Snapshot byte count mismatch.");
            byte[] result = new byte[snapshot.ExpectedBytes];
            int offset = 0;
            for (int i = 0; i < snapshot.Chunks.Length; i++)
            {
                byte[] chunk = snapshot.Chunks[i];
                if (chunk == null)
                    throw new InvalidDataException("Missing snapshot chunk " + i + ".");
                Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            if (offset != result.Length)
                throw new InvalidDataException("Snapshot reassembly length mismatch.");
            return result;
        }

        private static void InstallTransferredSave(byte[] archiveBytes, string transferId)
        {
            string destination = Path.GetFullPath(SavesystemPatch.mpsavefolder);
            string parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(parent))
                throw new InvalidOperationException("Could not resolve multiplayer save parent folder.");

            Directory.CreateDirectory(parent);
            string staging = destination + ".catpatch-staging-" + transferId;
            string backup = destination + ".catpatch-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            try
            {
                using (MemoryStream stream = new MemoryStream(archiveBytes, false))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    string stagingRoot = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string outputPath = Path.GetFullPath(Path.Combine(staging, relative));
                        if (!outputPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Unsafe archive path: " + entry.FullName);
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(outputPath);
                            continue;
                        }
                        string outputDir = Path.GetDirectoryName(outputPath);
                        if (!string.IsNullOrEmpty(outputDir))
                            Directory.CreateDirectory(outputDir);
                        using (Stream input = entry.Open())
                        using (FileStream output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            input.CopyTo(output);
                    }
                }

                if (!File.Exists(Path.Combine(staging, SavesystemPatch.mpsaveheaderfile)))
                    throw new InvalidDataException("Transferred snapshot is missing " + SavesystemPatch.mpsaveheaderfile + ".");

                if (Directory.Exists(destination))
                    Directory.Move(destination, backup);
                Directory.Move(staging, destination);
                LogInfo("save install committed destination=" + destination +
                        (Directory.Exists(backup) ? " backup=" + backup : ""));
            }
            catch
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
                if (!Directory.Exists(destination) && Directory.Exists(backup))
                    Directory.Move(backup, destination);
                throw;
            }
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string ReadString(ref NetDataReader reader)
        {
            string value;
            reader.Get(out value, true);
            return value;
        }

        private static void DrawOfferWindow()
        {
            float width = 520f;
            float height = 210f;
            Rect rect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
            GUI.depth = -32000;
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 18f, rect.y + 16f, rect.width - 36f, 28f), "Host transfer request");
            GUI.Label(new Rect(rect.x + 18f, rect.y + 52f, rect.width - 36f, 70f),
                _incomingHostName + " wants to transfer this Steam lobby and the current multiplayer save to you.\n\n" +
                "Experimental: the game will briefly reconnect after verification.");
            if (GUI.Button(new Rect(rect.x + 18f, rect.yMax - 60f, 228f, 42f), "Accept and become host"))
                SendOfferResponse(true);
            if (GUI.Button(new Rect(rect.xMax - 246f, rect.yMax - 60f, 228f, 42f), "Decline"))
                SendOfferResponse(false);
        }

        private static void DrawStatusOverlay()
        {
            string text = _statusText;
            if (_incomingSnapshot != null)
            {
                int percent = Mathf.FloorToInt((_incomingSnapshot.ReceivedChunks /
                    (float)Math.Max(1, _incomingSnapshot.ExpectedChunks)) * 100f);
                text = "Receiving host snapshot: " + percent + "%";
            }
            if (string.IsNullOrWhiteSpace(text))
                return;

            Rect rect = new Rect((Screen.width - 620f) * 0.5f, 24f, 620f, 50f);
            Color old = GUI.color;
            GUI.depth = -31990;
            GUI.color = _statusImportant ? new Color(0.45f, 0.08f, 0.08f, 0.96f) : new Color(0.04f, 0.14f, 0.11f, 0.96f);
            GUI.Box(rect, GUIContent.none);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 12f, rect.y + 14f, rect.width - 24f, 24f), text);
            GUI.color = old;
        }

        private static void SetStatus(string message, bool important, double seconds = 8.0)
        {
            _statusText = message ?? "";
            _statusImportant = important;
            _statusUntil = Time.realtimeSinceStartupAsDouble + seconds;
        }

        private static bool HandlersStillRegistered()
        {
            return CatPatchNetwork.IsRegistered(true, NetworkMessageIds.HostTransferResponse) &&
                   CatPatchNetwork.IsRegistered(true, NetworkMessageIds.HostTransferSnapshotAck) &&
                   CatPatchNetwork.IsRegistered(true, NetworkMessageIds.HostTransferCommitReady) &&
                   CatPatchNetwork.IsRegistered(false, NetworkMessageIds.HostTransferOffer) &&
                   CatPatchNetwork.IsRegistered(false, NetworkMessageIds.HostTransferCommit);
        }

        private static bool HandlerDictionaryContains(string fieldName, ushort messageId)
        {
            try
            {
                FieldInfo field = typeof(Net).GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object value = field == null ? null : field.GetValue(null);
                IDictionary dictionary = value as IDictionary;
                return dictionary != null && dictionary.Contains(messageId);
            }
            catch
            {
                return false;
            }
        }

        private static void LegacyRegisterNetReceiver(string methodName, ushort messageId,
            KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate handler)
        {
            try
            {
                MethodInfo method = typeof(Net).GetMethod(methodName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                    throw new MissingMethodException(typeof(Net).FullName, methodName);
                ParameterInfo[] parameters = method.GetParameters();
                Delegate converted = Delegate.CreateDelegate(parameters[1].ParameterType, handler.Target, handler.Method);
                object[] args = parameters.Length >= 3
                    ? new object[] { messageId, converted, false }
                    : new object[] { messageId, converted };
                method.Invoke(null, args);
            }
            catch (TargetInvocationException ex)
            {
                // Duplicate registration during startup is harmless; every actual
                // failure is still visible in the log with its inner exception.
                LogWarning("handler registration " + methodName + " msg=" + messageId + " failed: " +
                           (ex.InnerException == null ? ex.Message : ex.InnerException.Message));
            }
            catch (Exception ex)
            {
                LogWarning("handler registration " + methodName + " msg=" + messageId + " failed: " + ex.Message);
            }
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

        private sealed class IncomingSnapshot
        {
            public string TransferId;
            public int ExpectedBytes;
            public int ChunkBytes;
            public int ExpectedChunks;
            public string ExpectedHash;
            public byte[][] Chunks;
            public int ReceivedChunks;
            public int ReceivedBytes;
            public int LastLoggedPercent = -10;
            public double ExpiresAt;
        }

        private static class V5TransportBridge
        {
            private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private static Body _handoffLocalBody;
            private static readonly List<Body> HandoffRemoteBodies = new List<Body>();
            private static Dictionary<ulong, Vector2> _transferredPlayerPositions = new Dictionary<ulong, Vector2>();

            public static string DescribeCompatibility()
            {
                try
                {
                    bool shutdown = typeof(TransportSteamworks).GetMethod(
                        "Shutdown", InstanceFlags, null, new[] { typeof(bool) }, null) != null;
                    bool createServer = typeof(TransportSteamworks).GetMethod(
                        "CreateServerSocket", InstanceFlags, null, Type.EmptyTypes, null) != null;
                    bool createClient = typeof(TransportSteamworks).GetMethod(
                        "CreateClientSocket", InstanceFlags, null, Type.EmptyTypes, null) != null;
                    bool transportCreated = typeof(Net).GetMethod("TransportCreated", StaticFlags) != null;
                    bool onTransportConnected = typeof(Net).GetMethod("OnTransportConnected", StaticFlags) != null;
                    bool transportProperty = typeof(Net).GetProperty("Transport", StaticFlags) != null;
                    return "Shutdown(bool)=" + shutdown +
                           " CreateServerSocket=" + createServer +
                           " CreateClientSocket=" + createClient +
                           " TransportCreated=" + transportCreated +
                           " OnTransportConnected=" + onTransportConnected +
                           " Transport=" + transportProperty;
                }
                catch (Exception ex)
                {
                    return "check failed: " + Unwrap(ex).Message;
                }
            }

            public static bool TryCaptureOriginalWorldgenState(
                out ServerMain.StartGameAnnouncementPacket worldgenState)
            {
                worldgenState = default(ServerMain.StartGameAnnouncementPacket);
                try
                {
                    FieldInfo field = typeof(ServerMain).GetField(
                        "LastStartGameAnnouncementPacket", StaticFlags);
                    if (field == null)
                    {
                        LogWarning("worldgen state capture failed because LastStartGameAnnouncementPacket was not found");
                        return false;
                    }

                    object value = field.GetValue(null);
                    if (!(value is ServerMain.StartGameAnnouncementPacket))
                    {
                        LogWarning("worldgen state capture failed because the start packet had an unexpected type");
                        return false;
                    }

                    worldgenState = (ServerMain.StartGameAnnouncementPacket)value;
                    // Together removed the packet's validity flag. Only capture a
                    // completed host world, and reject an uninitialized RNG state.
                    if (!Net.IsServer || !SharedMain.LocalWorldIsGenerated ||
                        SharedMain.LocalWorldIsGenerating ||
                        worldgenState.randomstate.Equals(default(UnityEngine.Random.State)))
                    {
                        LogWarning("worldgen state capture skipped because no real start-game packet exists");
                        return false;
                    }

                    LogInfo("original worldgen state captured from v5 start-game packet fingerprint=" +
                            GetWorldgenStateFingerprint(worldgenState));
                    return true;
                }
                catch (Exception ex)
                {
                    LogWarning("worldgen state capture failed: " + Unwrap(ex).Message);
                    return false;
                }
            }

            public static void ApplyTransferredWorldgenState(
                ServerMain.StartGameAnnouncementPacket worldgenState, string transferId)
            {
                try
                {
                    UnityEngine.Random.state = worldgenState.randomstate;
                    KrokoshaScavMultiplayer.rules = worldgenState.rules;
                    worldgenState.prefs.ApplyPrefs(true, true);
                    WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.randomstate =
                        worldgenState.randomstate;
                    FieldInfo field = typeof(ServerMain).GetField(
                        "LastStartGameAnnouncementPacket", StaticFlags);
                    if (field != null)
                        field.SetValue(null, worldgenState);
                    LogInfo("original worldgen state applied id=" + transferId +
                            " fingerprint=" + GetWorldgenStateFingerprint(worldgenState) +
                            " before transferred save load");
                }
                catch (Exception ex)
                {
                    LogWarning("transferred worldgen state apply failed id=" + transferId + ": " +
                               Unwrap(ex).Message);
                }
            }

            public static void ApplyTransferredRunSettings(string runSettings, string transferId)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(runSettings))
                    {
                        LogWarning("transferred run settings are empty id=" + transferId);
                        return;
                    }
                    WorldgenPatches.ApplyRunSettings(runSettings);
                    LogInfo("transferred run settings applied id=" + transferId +
                            " chars=" + runSettings.Length);
                }
                catch (Exception ex)
                {
                    LogWarning("transferred run settings apply failed id=" + transferId + ": " +
                               Unwrap(ex).Message);
                }
            }

            private static string GetWorldgenStateFingerprint(
                ServerMain.StartGameAnnouncementPacket worldgenState)
            {
                try
                {
                    NetDataWriter writer = new NetDataWriter();
                    writer.Put(worldgenState);
                    byte[] bytes = new byte[writer.Length];
                    Buffer.BlockCopy(writer.Data, 0, bytes, 0, writer.Length);
                    string hash = ComputeSha256(bytes);
                    return hash.Substring(0, Math.Min(16, hash.Length));
                }
                catch
                {
                    return "unavailable";
                }
            }

            public static void PrepareWorldForHostHandoff()
            {
                _handoffLocalBody = Util.GetLocalBody();
                HandoffRemoteBodies.Clear();
                int captured = 0;
                int failed = 0;
                ulong localSteamId = KSteam.GetLocalUserSteamID().m_SteamID;
                HashSet<ScavPlayer> players = new HashSet<ScavPlayer>(ScavPlayer.ClientIdToPlayerDict.Values);

                foreach (ScavPlayer player in players)
                {
                    if (player == null || player.body == null || player.SteamId == 0UL)
                        continue;

                    try
                    {
                        string persistentId = "STEAM_" + player.SteamId;
                        ServerMain.Server_LastPlayerStates[persistentId] = new PlayerSavedState(player.body);
                        captured++;
                        if (player.SteamId == localSteamId)
                            _handoffLocalBody = player.body;
                        else if (!HandoffRemoteBodies.Contains(player.body))
                            HandoffRemoteBodies.Add(player.body);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LogWarning("player-state capture failed steam=" + player.SteamId + ": " +
                                   Unwrap(ex).Message);
                    }
                }

                LogInfo("world handoff state captured players=" + captured + " failed=" + failed +
                        " remoteBodies=" + HandoffRemoteBodies.Count +
                        " localBody=" + (_handoffLocalBody != null));
            }

            public static bool TryDetachKeepingSteamLobby(out string error)
            {
                error = "";
                try
                {
                    TransportSteamworks oldTransport;
                    if (!Net.TryGetSteamTransport(out oldTransport) || oldTransport == null)
                    {
                        error = "Active transport is not Steamworks.";
                        return false;
                    }

                    MethodInfo shutdownKeepingLobby = typeof(TransportSteamworks).GetMethod(
                        "Shutdown", InstanceFlags, null, new[] { typeof(bool) }, null);
                    if (shutdownKeepingLobby == null)
                    {
                        error = "TransportSteamworks.Shutdown(bool) was not found.";
                        return false;
                    }

                    shutdownKeepingLobby.Invoke(oldTransport, new object[] { false });
                    SetNetTransport(null);
                    MultiplayerGameRules previousRules = KrokoshaScavMultiplayer.rules;
                    try
                    {
                        MultiplayerGameRules shutdownRules = previousRules;
                        shutdownRules.SavePlayerState = false;
                        shutdownRules.SavePlayerInventory = false;
                        shutdownRules.SavePlayerPosition = false;
                        KrokoshaScavMultiplayer.rules = shutdownRules;
                        Net.ShutdownReset();
                    }
                    finally
                    {
                        KrokoshaScavMultiplayer.rules = previousRules;
                    }
                    DestroyHandoffRemoteBodies();
                    LogInfo("old transport detached while retaining Steam lobby " + KSteam.CURRENT_LOBBY.lobby_steamID.m_SteamID);
                    return true;
                }
                catch (Exception ex)
                {
                    error = Unwrap(ex).Message;
                    LogError("detach bridge failed", Unwrap(ex));
                    return false;
                }
            }

            public static IEnumerator TransitionToMainMenuKeepingLobbyCoroutine()
            {
                if (Util.IsInMainMenu() && PreRunScript.instance != null)
                {
                    LogInfo("clean reload scene already ready");
                    yield break;
                }

                bool transitionStarted = false;
                try
                {
                    KrokoshaScavMultiplayer.showMultiplayerMenu = true;
                    UIInGame.StopSpectatorMode();
                    if (PlayerCamera.main == null)
                    {
                        LogWarning("clean reload scene transition could not start because PlayerCamera.main is null");
                    }
                    else
                    {
                        LogInfo("transitioning to main-menu loading scene while Steam lobby remains joined");
                        PlayerCamera.main.ToMainMenu();
                        transitionStarted = true;
                    }
                }
                catch (Exception ex)
                {
                    LogError("clean reload scene transition failed", Unwrap(ex));
                }

                if (!transitionStarted)
                    yield break;

                double deadline = Time.realtimeSinceStartupAsDouble + MainMenuTransitionTimeoutSeconds;
                while (Time.realtimeSinceStartupAsDouble < deadline)
                {
                    if (Util.IsInMainMenu() && PreRunScript.instance != null)
                    {
                        LogInfo("main-menu loading scene ready; Steam lobby=" +
                                KSteam.CURRENT_LOBBY.lobby_steamID.m_SteamID);
                        yield break;
                    }
                    yield return null;
                }

                LogWarning("main-menu loading scene transition timed out after " +
                           MainMenuTransitionTimeoutSeconds.ToString("0") + " seconds");
            }

            public static bool TryLoadTransferredMultiplayerSave(out string error)
            {
                error = "";
                try
                {
                    if (!Net.IsRunning || !Net.IsServer)
                        throw new InvalidOperationException("The new host transport is not running.");
                    string saveFile = Path.Combine(SavesystemPatch.mpsavefolder, "save.sv");
                    if (!File.Exists(saveFile))
                        throw new FileNotFoundException("The transferred multiplayer save is missing.", saveFile);

                    _transferredPlayerPositions = ReadTransferredPlayerPositions(SavesystemPatch.mpsavefolder);

                    PreRunScript preRun = PreRunScript.instance;
                    if (preRun == null)
                        throw new InvalidOperationException("PreRunScript was not found in the loading scene.");
                    MethodInfo loadRun = typeof(PreRunScript).GetMethod("LoadRun", InstanceFlags,
                        null, Type.EmptyTypes, null);
                    if (loadRun == null)
                        throw new MissingMethodException(typeof(PreRunScript).FullName, "LoadRun");

                    LogInfo("starting normal v5 multiplayer save load folder=" + SavesystemPatch.mpsavefolder +
                            " connectedPeers=" + ServerMain.AllClientIdsExceptHost.Count);
                    loadRun.Invoke(preRun, null);
                    return true;
                }
                catch (Exception ex)
                {
                    Exception actual = Unwrap(ex);
                    error = actual.Message;
                    LogError("transferred multiplayer save load failed", actual);
                    return false;
                }
            }

            public static IEnumerator RestoreTransferredPlayerPositionsCoroutine(string transferId)
            {
                Dictionary<ulong, Vector2> positions = _transferredPlayerPositions;
                if (positions == null || positions.Count == 0)
                {
                    LogWarning("position restore skipped id=" + transferId + " because the snapshot had no player positions");
                    yield break;
                }

                SetStatus("Restoring transferred player locations...", false, 60.0);
                double deadline = Time.realtimeSinceStartupAsDouble + PositionRestoreTimeoutSeconds;
                int readyBodies = 0;
                int readyRemoteSpawns = 0;
                int expectedRemoteSpawns = 0;
                while (Time.realtimeSinceStartupAsDouble < deadline)
                {
                    if (Util.IsInWorld() && Util.IsWorldGenerated())
                    {
                        readyBodies = 0;
                        readyRemoteSpawns = 0;
                        expectedRemoteSpawns = 0;
                        foreach (ScavPlayer player in ScavPlayer.ClientIdToPlayerDict.Values)
                        {
                            if (player != null && player.SteamId != 0UL && player.body != null &&
                                positions.ContainsKey(player.SteamId))
                            {
                                readyBodies++;
                                if (!player.IsLocal)
                                {
                                    expectedRemoteSpawns++;
                                    if (player.ServerPlayerState != null &&
                                        player.ServerPlayerState.did_give_spawn_location &&
                                        player.ServerPlayerState.finished_worldgen)
                                        readyRemoteSpawns++;
                                }
                            }
                        }
                        if (readyBodies >= positions.Count &&
                            readyRemoteSpawns >= expectedRemoteSpawns)
                            break;
                    }
                    yield return null;
                }

                bool lateSpawnReady = readyBodies >= positions.Count &&
                                      readyRemoteSpawns >= expectedRemoteSpawns;
                LogInfo("position restore readiness id=" + transferId +
                        " bodies=" + readyBodies + "/" + positions.Count +
                        " remoteWorldgen=" + readyRemoteSpawns + "/" + expectedRemoteSpawns +
                        " lateSpawnReady=" + lateSpawnReady);
                if (lateSpawnReady)
                    yield return new WaitForSecondsRealtime((float)LateSpawnSettleSeconds);

                int restored = 0;
                foreach (ScavPlayer player in new List<ScavPlayer>(ScavPlayer.ClientIdToPlayerDict.Values))
                {
                    Vector2 position;
                    if (player == null || player.SteamId == 0UL || player.body == null ||
                        !positions.TryGetValue(player.SteamId, out position))
                        continue;

                    try
                    {
                        player.Server_TeleportCharacter(position);
                        restored++;
                        LogInfo("position restored id=" + transferId + " steam=" + player.SteamId +
                                " client=" + player.playerId + " position=" + FormatPosition(position));
                    }
                    catch (Exception ex)
                    {
                        LogWarning("position restore failed id=" + transferId + " steam=" + player.SteamId +
                                   ": " + Unwrap(ex).Message);
                    }
                }

                yield return new WaitForSecondsRealtime((float)PostRestoreVerificationSeconds);
                int correctedAfterRestore = 0;
                foreach (ScavPlayer player in new List<ScavPlayer>(ScavPlayer.ClientIdToPlayerDict.Values))
                {
                    Vector2 expectedPosition;
                    if (player == null || player.SteamId == 0UL || player.body == null ||
                        !positions.TryGetValue(player.SteamId, out expectedPosition))
                        continue;

                    Vector3 actual = player.body.transform.position;
                    if (Vector2.Distance(new Vector2(actual.x, actual.y), expectedPosition) <= 0.5f)
                        continue;
                    try
                    {
                        player.Server_TeleportCharacter(expectedPosition);
                        correctedAfterRestore++;
                        LogWarning("position moved after restore; corrected id=" + transferId +
                                   " steam=" + player.SteamId +
                                   " actual=" + FormatPosition(new Vector2(actual.x, actual.y)) +
                                   " expected=" + FormatPosition(expectedPosition));
                    }
                    catch (Exception ex)
                    {
                        LogWarning("post-restore position correction failed id=" + transferId +
                                   " steam=" + player.SteamId + ": " + Unwrap(ex).Message);
                    }
                }

                LogInfo("position restore complete id=" + transferId + " restored=" + restored +
                        " expected=" + positions.Count +
                        " correctedAfterRestore=" + correctedAfterRestore +
                        " worldReady=" + Util.IsWorldGenerated());
                _transferredPlayerPositions = new Dictionary<ulong, Vector2>();
            }

            private static Dictionary<ulong, Vector2> ReadTransferredPlayerPositions(string saveFolder)
            {
                Dictionary<ulong, Vector2> result = new Dictionary<ulong, Vector2>();
                string catPatchPath = Path.Combine(saveFolder, TransferPositionsFileName);
                if (File.Exists(catPatchPath))
                {
                    try
                    {
                        JObject metadata = JObject.Parse(File.ReadAllText(catPatchPath));
                        JObject positions = metadata["positions"] as JObject;
                        if (positions == null)
                            throw new InvalidDataException("positions object is missing");
                        foreach (JProperty entry in positions.Properties())
                        {
                            const string prefix = "STEAM_";
                            ulong steamId;
                            JObject position = entry.Value as JObject;
                            if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                                !ulong.TryParse(entry.Name.Substring(prefix.Length), out steamId) ||
                                position == null || position["x"] == null || position["y"] == null)
                                continue;

                            Vector2 livePosition = new Vector2(
                                position["x"].Value<float>(),
                                position["y"].Value<float>());
                            result[steamId] = livePosition;
                            LogInfo("live position metadata loaded steam=" + steamId +
                                    " position=" + FormatPosition(livePosition));
                        }
                        if (result.Count > 0)
                        {
                            LogInfo("transferred live position table loaded players=" + result.Count);
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning("CatPatch live position metadata could not be read: " +
                                   Unwrap(ex).Message + "; falling back to Together PLRPOS");
                    }
                }

                string rulesPath = Path.Combine(saveFolder, "mp_rules.json");
                if (!File.Exists(rulesPath))
                {
                    LogWarning("transferred position table is missing: " + rulesPath);
                    return result;
                }

                try
                {
                    JObject rules = JObject.Parse(File.ReadAllText(rulesPath));
                    JToken positionsToken = rules["PLRPOS"];
                    Dictionary<string, ValueTuple<float, float>> serialized = positionsToken == null
                        ? null
                        : positionsToken.ToObject<Dictionary<string, ValueTuple<float, float>>>();
                    if (serialized != null)
                    {
                        foreach (KeyValuePair<string, ValueTuple<float, float>> entry in serialized)
                        {
                            const string prefix = "STEAM_";
                            ulong steamId;
                            if (entry.Key != null && entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                ulong.TryParse(entry.Key.Substring(prefix.Length), out steamId))
                            {
                                Vector2 position = new Vector2(entry.Value.Item1, entry.Value.Item2);
                                result[steamId] = position;
                                LogInfo("position captured steam=" + steamId + " position=" + FormatPosition(position));
                            }
                        }
                    }
                    LogInfo("transferred position table loaded players=" + result.Count);
                }
                catch (Exception ex)
                {
                    LogWarning("transferred position table could not be read: " + Unwrap(ex).Message);
                }
                return result;
            }

            private static string FormatPosition(Vector2 position)
            {
                return "(" + position.x.ToString("0.00") + ", " + position.y.ToString("0.00") + ")";
            }

            public static bool TryStartHostInCurrentLobby(out string error)
            {
                error = "";
                try
                {
                    if (Net.CurrentServerInfo != null)
                        Net.MyServerInfo = Net.CurrentServerInfo;
                    InvokeTransportCreated(Net.NetType.Host);
                    TransportSteamworks transport = new TransportSteamworks();
                    if (!transport.IsInitialized)
                        throw new InvalidOperationException("Steamworks transport is not initialized.");
                    SetNetTransport(transport);
                    TransportSteamworks activeTransport;
                    if (!Net.TryGetSteamTransport(out activeTransport) || activeTransport != transport)
                        throw new InvalidOperationException("Could not activate Steam mode for the new host transport.");
                    InvokeRequired(transport, "CreateServerSocket");

                    InvokeOnTransportConnected();
                    KSteam.Server_UpdateLobbyData();
                    return true;
                }
                catch (Exception ex)
                {
                    error = Unwrap(ex).Message;
                    LogError("start-host bridge failed", Unwrap(ex));
                    return false;
                }
            }

            public static bool TryStartClientInCurrentLobby(ulong ownerSteamId, out string error)
            {
                error = "";
                try
                {
                    InvokeTransportCreated(Net.NetType.Client);
                    TransportSteamworks transport = new TransportSteamworks();
                    if (!transport.IsInitialized)
                        throw new InvalidOperationException("Steamworks transport is not initialized.");
                    SetNetTransport(transport);
                    TransportSteamworks activeTransport;
                    if (!Net.TryGetSteamTransport(out activeTransport) || activeTransport != transport)
                        throw new InvalidOperationException("Could not activate Steam mode for the new client transport.");
                    InvokeRequired(transport, "CreateClientSocket");
                    InvokeOnTransportConnected();
                    NetDataWriter intro = InvokeClientConnectIntroductionPacket();
                    transport.SendThroughSteamSocket(ownerSteamId, new ArraySegment<byte>(intro.Data), 8);
                    return true;
                }
                catch (Exception ex)
                {
                    error = Unwrap(ex).Message;
                    LogError("start-client bridge failed", Unwrap(ex));
                    return false;
                }
            }

            private static void InvokeTransportCreated(Net.NetType type)
            {
                MethodInfo method = typeof(Net).GetMethod("TransportCreated", StaticFlags);
                if (method == null)
                    throw new MissingMethodException(typeof(Net).FullName, "TransportCreated");
                method.Invoke(null, new object[] { type, false });
            }

            private static void InvokeOnTransportConnected()
            {
                MethodInfo method = typeof(Net).GetMethod("OnTransportConnected", StaticFlags);
                if (method == null)
                    throw new MissingMethodException(typeof(Net).FullName, "OnTransportConnected");
                method.Invoke(null, null);
            }

            private static NetDataWriter InvokeClientConnectIntroductionPacket()
            {
                MethodInfo method = typeof(Multiplayer).GetMethod(
                    "GetClientConnectIntroductionPacket", StaticFlags,
                    null, new[] { typeof(NetDataWriter) }, null);
                if (method == null)
                    throw new MissingMethodException(typeof(Multiplayer).FullName,
                        "GetClientConnectIntroductionPacket");
                NetDataWriter writer = method.Invoke(null, new object[] { null }) as NetDataWriter;
                if (writer == null)
                    throw new InvalidOperationException("Together returned no client introduction packet.");
                return writer;
            }

            private static void SetNetTransport(TransportSteamworks transport)
            {
                PropertyInfo property = typeof(Net).GetProperty("Transport", StaticFlags);
                if (property == null)
                    throw new MissingMemberException(typeof(Net).FullName, "Transport");
                property.SetValue(null, transport, null);
            }

            private static void InvokeRequired(object target, string methodName)
            {
                MethodInfo method = target.GetType().GetMethod(methodName, InstanceFlags, null, Type.EmptyTypes, null);
                if (method == null)
                    throw new MissingMethodException(target.GetType().FullName, methodName);
                object result = method.Invoke(target, null);
                if (result is bool && !(bool)result)
                    throw new InvalidOperationException(methodName + " returned false.");
            }

            private static ScavPlayer InvokeCreatePlayer()
            {
                MethodInfo method = typeof(Net).GetMethod("CreatePlayer", StaticFlags);
                if (method == null)
                    throw new MissingMethodException(typeof(Net).FullName, "CreatePlayer");
                object player = method.Invoke(null, new object[]
                {
                    new knetid(0),
                    KSteam.GetLocalUsername(),
                    UIMainMenu.LAST_VALID_INPUT_COLOR
                });
                ScavPlayer result = player as ScavPlayer;
                if (result == null)
                    throw new InvalidOperationException("Net.CreatePlayer returned null.");
                return result;
            }

            private static void TryRebindLocalBody(ScavPlayer player)
            {
                try
                {
                    Body body = _handoffLocalBody != null ? _handoffLocalBody : Util.GetLocalBody();
                    if (body == null)
                    {
                        LogWarning("local body rebind skipped because no local body was found");
                        return;
                    }
                    NetBody netBody = body.GetComponent<NetBody>();
                    if (netBody == null)
                    {
                        LogWarning("local body rebind skipped because the body has no NetBody");
                        return;
                    }

                    PropertyInfo netIdProperty = typeof(NetBody).GetProperty("netId", InstanceFlags);
                    object oldNetId = netIdProperty == null ? null : netIdProperty.GetValue(netBody, null);
                    FieldInfo netBodyMapField = typeof(NetBody).GetField("_PRIV_NetIdToNetBody", StaticFlags);
                    IDictionary netBodyMap = netBodyMapField == null ? null : netBodyMapField.GetValue(null) as IDictionary;
                    if (netBodyMap != null && oldNetId != null)
                        netBodyMap.Remove(oldNetId);

                    PropertyInfo property = typeof(NetBody).GetProperty("plr", InstanceFlags);
                    if (property != null)
                        property.SetValue(netBody, player, null);
                    MethodInfo finish = typeof(NetBody).GetMethod("OnFoundScavPlayerInitFinish", InstanceFlags);
                    if (finish != null)
                        finish.Invoke(netBody, null);

                    string persistentId = "STEAM_" + player.SteamId;
                    PlayerSavedState state;
                    bool restored = ServerMain.Server_LastPlayerStates.TryGetValue(persistentId, out state);
                    if (restored)
                    {
                        state.Apply(body);
                        ServerMain.Server_LastPlayerStates.Remove(persistentId);
                    }
                    _handoffLocalBody = null;
                    LogInfo("rebound local body to new host player oldNetId=" + oldNetId +
                            " newNetId=" + player.playerId + " stateRestored=" + restored);
                }
                catch (Exception ex)
                {
                    LogWarning("local body rebind failed; late-spawn recovery may be needed: " + Unwrap(ex).Message);
                }
            }

            private static void DestroyHandoffRemoteBodies()
            {
                int destroyed = 0;
                for (int i = 0; i < HandoffRemoteBodies.Count; i++)
                {
                    Body body = HandoffRemoteBodies[i];
                    if (body == null || body == _handoffLocalBody)
                        continue;
                    try
                    {
                        NetBody.DestroyNPC(body);
                        destroyed++;
                    }
                    catch (Exception ex)
                    {
                        LogWarning("orphan player body cleanup failed: " + Unwrap(ex).Message);
                    }
                }
                HandoffRemoteBodies.Clear();
                if (destroyed > 0)
                    LogInfo("queued old remote player bodies for cleanup count=" + destroyed);
            }

            private static Exception Unwrap(Exception ex)
            {
                TargetInvocationException invocation = ex as TargetInvocationException;
                return invocation != null && invocation.InnerException != null ? invocation.InnerException : ex;
            }
        }
    }
}
