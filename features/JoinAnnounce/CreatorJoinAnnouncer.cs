using Together;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class CreatorJoinAnnouncer : MonoBehaviour
    {
        private const ulong CreatorSteamId = 76561198303047174UL;
        private const string JoinMessage = "Mr CatPatch has joined.";
        private const double JoinAnnouncementDelaySeconds = 1.0;
        private const double MissingPlayerResetDelaySeconds = 2.0;
        private const double PollIntervalSeconds = 0.25;
        private const double RegistrationVerifyIntervalSeconds = 2.0;

        private static CreatorJoinAnnouncer _instance;
        private bool _sessionStarted;
        private bool _monitoringHost;
        private bool _pendingLocalAlert;
        private bool _waitingForHudLogged;
        private bool _identityLookupFailed;
        private bool _displayFailed;
        private int _lastPlayerCount = -1;
        private int _lastSteamIdentityCount = -1;
        private double _nextBroadcastAttemptAt;

        private bool _creatorPresent;
        private bool _announcementSent;
        private double _creatorDetectedAt;
        private double _creatorMissingSince;
        private double _nextPollAt;
        private double _nextRegistrationVerifyAt;

        private void Awake()
        {
            _instance = this;
            EnsureClientReceiverRegistered();
            Plugin.Logger.LogInfo("[CreatorJoin] initialized; creatorSteamId=" + CreatorSteamId);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Update()
        {
            if (!KrokoshaScavMultiplayer.network_system_is_running)
            {
                ResetState();
                return;
            }

            EnsureClientReceiverRegistered();
            _sessionStarted = true;
            TryDisplayPendingAlert();

            if (!KrokoshaScavMultiplayer.is_server)
            {
                _monitoringHost = false;
                return;
            }

            if (!_monitoringHost)
            {
                _monitoringHost = true;
                Plugin.Logger.LogInfo("[CreatorJoin] host monitoring started; steam=" + Net.IsRunningSteam);
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextPollAt)
                return;

            _nextPollAt = now + PollIntervalSeconds;
            bool creatorFound;
            string identitySource;
            try
            {
                creatorFound = IsCreatorConnected(out identitySource);
                _identityLookupFailed = false;
            }
            catch (Exception ex)
            {
                if (!_identityLookupFailed)
                    Plugin.Logger.LogWarning("[CreatorJoin] identity lookup failed: " + ex);
                _identityLookupFailed = true;
                return; // Lookup failure does not prove the creator disconnected.
            }

            if (creatorFound)
            {
                _creatorMissingSince = 0.0;
                if (!_creatorPresent)
                {
                    _creatorPresent = true;
                    _creatorDetectedAt = now;
                    _announcementSent = false;
                    Plugin.Logger.LogInfo("[CreatorJoin] creator detected; source=" + identitySource);
                }

                if (!_announcementSent && now >= _creatorDetectedAt + JoinAnnouncementDelaySeconds && now >= _nextBroadcastAttemptAt)
                {
                    _nextBroadcastAttemptAt = now + RegistrationVerifyIntervalSeconds;
                    _announcementSent = BroadcastJoinAnnouncement();
                }

                return;
            }

            if (!_creatorPresent)
                return;

            if (_creatorMissingSince <= 0.0)
                _creatorMissingSince = now;

            if (now >= _creatorMissingSince + MissingPlayerResetDelaySeconds)
            {
                Plugin.Logger.LogInfo("[CreatorJoin] creator left; rejoin announcement rearmed");
                ResetPresenceState();
            }
        }

        private void EnsureClientReceiverRegistered()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextRegistrationVerifyAt)
                return;

            _nextRegistrationVerifyAt = now + RegistrationVerifyIntervalSeconds;
            if (ClientReceiverIsRegistered())
                return;

            CatPatchNetwork.RegisterClient(NetworkMessageIds.CreatorJoinAlert, OnClientJoinAlertReceived);
            Plugin.Logger.LogInfo("[CreatorJoin] client receiver registered");
        }

        private bool IsCreatorConnected(out string identitySource)
        {
            identitySource = "";
            // These keys come from Steam's accepted connection identity, not
            // names or v5's small session-local knetid values.
            TransportSteamworks steam;
            ScavPlayer creator;
            if (Net.TryGetSteamTransport(out steam) && steam != null &&
                steam.SteamIDToNetPlayerDict.TryGetValue(CreatorSteamId, out creator) && creator != null)
            {
                identitySource = "steam_transport";
                return true;
            }

            int identityCount = 0;
            foreach (ScavPlayer player in ScavPlayer.ClientIdToPlayerDict.Values)
            {
                if (player == null)
                    continue;
                if (player.SteamId != 0UL)
                    identityCount++;
                if (player.SteamId == CreatorSteamId)
                {
                    identitySource = "player.SteamId";
                    return true;
                }
            }

            int playerCount = ScavPlayer.ClientIdToPlayerDict.Count;
            if (playerCount != _lastPlayerCount || identityCount != _lastSteamIdentityCount)
            {
                _lastPlayerCount = playerCount;
                _lastSteamIdentityCount = identityCount;
                Plugin.Logger.LogInfo("[CreatorJoin] creator not detected; players=" + playerCount + " nonzeroSteamIds=" + identityCount);
            }

            return false;
        }

        private bool BroadcastJoinAnnouncement()
        {
            try
            {
                IReadOnlyList<knetid> recipients = ServerMain.AllClientIdsExceptHost;
                if (recipients != null && recipients.Count > 0)
                {
                    NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.CreatorJoinAlert);
                    writer.Put(JoinMessage, true);
                    Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, recipients);
                }
                QueueLocalAlert();
                Plugin.Logger.LogInfo("[CreatorJoin] broadcast sent; remoteRecipients=" + (recipients?.Count ?? 0));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[CreatorJoin] broadcast failed; retrying in 2s: " + ex);
                return false;
            }
        }

        private static void OnClientJoinAlertReceived(knetid senderClientId, ref NetDataReader reader)
        {
            try
            {
                string message;
                reader.Get(out message, true);
                if (message != JoinMessage || _instance == null)
                    return;
                Plugin.Logger.LogInfo("[CreatorJoin] host announcement received");
                _instance.QueueLocalAlert();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[CreatorJoin] could not read announcement: " + ex.Message);
            }
        }

        private static bool ClientReceiverIsRegistered()
        {
            return CatPatchNetwork.IsRegistered(false, NetworkMessageIds.CreatorJoinAlert);
        }

        private void QueueLocalAlert()
        {
            _pendingLocalAlert = true;
            _waitingForHudLogged = false;
            _displayFailed = false;
            TryDisplayPendingAlert();
        }

        private void TryDisplayPendingAlert()
        {
            if (!_pendingLocalAlert)
                return;
            PlayerCamera camera = PlayerCamera.main;
            if (camera == null || !camera.isActiveAndEnabled || camera.alertText == null || camera.alertBack == null)
            {
                if (!_waitingForHudLogged)
                    Plugin.Logger.LogInfo("[CreatorJoin] announcement queued; waiting for player HUD");
                _waitingForHudLogged = true;
                return;
            }
            try
            {
                camera.DoAlert(JoinMessage, false);
                _pendingLocalAlert = false;
                Plugin.Logger.LogInfo("[CreatorJoin] announcement displayed");
            }
            catch (Exception ex)
            {
                if (!_displayFailed)
                    Plugin.Logger.LogWarning("[CreatorJoin] HUD display failed; retaining announcement: " + ex.Message);
                _displayFailed = true;
            }
        }

        private void ResetState()
        {
            if (_sessionStarted || _pendingLocalAlert)
                Plugin.Logger.LogInfo("[CreatorJoin] session ended; pending announcement cleared");
            _sessionStarted = false;
            _monitoringHost = false;
            _pendingLocalAlert = false;
            _waitingForHudLogged = false;
            _identityLookupFailed = false;
            _displayFailed = false;
            _lastPlayerCount = -1;
            _lastSteamIdentityCount = -1;
            ResetPresenceState();
            _nextPollAt = 0.0;
            _nextRegistrationVerifyAt = 0.0;
        }

        private void ResetPresenceState()
        {
            _creatorPresent = false;
            _announcementSent = false;
            _creatorDetectedAt = 0.0;
            _creatorMissingSince = 0.0;
            _nextBroadcastAttemptAt = 0.0;
        }
    }
}
