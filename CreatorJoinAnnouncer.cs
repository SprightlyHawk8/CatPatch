using Together;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
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

        private bool _creatorPresent;
        private bool _announcementSent;
        private double _creatorDetectedAt;
        private double _creatorMissingSince;
        private double _nextPollAt;
        private double _nextRegistrationVerifyAt;

        private void Update()
        {
            if (!KrokoshaScavMultiplayer.network_system_is_running)
            {
                ResetState();
                return;
            }

            EnsureClientReceiverRegistered();

            if (!KrokoshaScavMultiplayer.is_server)
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextPollAt)
                return;

            _nextPollAt = now + PollIntervalSeconds;
            bool creatorFound = IsCreatorConnected();

            if (creatorFound)
            {
                _creatorMissingSince = 0.0;
                if (!_creatorPresent)
                {
                    _creatorPresent = true;
                    _creatorDetectedAt = now;
                    _announcementSent = false;
                }

                if (!_announcementSent && now >= _creatorDetectedAt + JoinAnnouncementDelaySeconds)
                {
                    _announcementSent = true;
                    BroadcastJoinAnnouncement();
                }

                return;
            }

            if (!_creatorPresent)
                return;

            if (_creatorMissingSince <= 0.0)
                _creatorMissingSince = now;

            if (now >= _creatorMissingSince + MissingPlayerResetDelaySeconds)
                ResetPresenceState();
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
        }

        private static bool IsCreatorConnected()
        {
            try
            {
                foreach (ScavPlayer player in ScavPlayer.ClientIdToPlayerDict.Values)
                {
                    if (player != null && player.SteamId == CreatorSteamId)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void BroadcastJoinAnnouncement()
        {
            HugController.ShowLocalMessage(JoinMessage);

            IReadOnlyList<knetid> recipients;
            try
            {
                recipients = ServerMain.AllClientIdsExceptHost;
            }
            catch
            {
                recipients = null;
            }

            if (recipients == null || recipients.Count == 0)
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.CreatorJoinAlert);
            writer.Put(JoinMessage, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, recipients);
        }

        private static void OnClientJoinAlertReceived(knetid senderClientId, ref NetDataReader reader)
        {
            try
            {
                string message;
                reader.Get(out message, true);
                if (!string.IsNullOrWhiteSpace(message))
                    HugController.ShowLocalMessage(message);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Could not display creator join announcement: " + ex.Message);
            }
        }

        private static bool ClientReceiverIsRegistered()
        {
            return CatPatchNetwork.IsRegistered(false, NetworkMessageIds.CreatorJoinAlert);
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

                Delegate convertedHandler = Delegate.CreateDelegate(parameters[1].ParameterType, handler.Target, handler.Method);
                object[] args = parameters.Length >= 3
                    ? new object[] { messageId, convertedHandler, false }
                    : new object[] { messageId, convertedHandler };
                method.Invoke(null, args);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    "Could not register creator join receiver " + methodName + ": " + ex.Message);
            }
        }

        private void ResetState()
        {
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
        }
    }
}
