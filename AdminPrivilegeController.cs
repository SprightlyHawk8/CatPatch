using HarmonyLib;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using Together;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class AdminPrivilegeController : MonoBehaviour
    {
        private const ushort AdminGrantedMessage = NetworkMessageIds.AdminPrivilegeGranted;

        private static readonly FieldInfo ServerAdminsField =
            AccessTools.Field(typeof(Con), "server_admins");

        private static readonly FieldInfo RemovedServerAdminsField =
            AccessTools.Field(typeof(Con), "removed_server_admins");

        private void Update()
        {
            if (!CatPatchNetwork.IsRegistered(false, AdminGrantedMessage))
                CatPatchNetwork.RegisterClient(AdminGrantedMessage, OnAdminGrantedReceived);
        }

        internal static bool TrySetPrivilege(ScavPlayer player, bool enabled, out string error)
        {
            error = "";
            if (!Net.IsRunning || !Net.IsServer)
            {
                error = "Only the server can change admin privileges.";
                return false;
            }

            if (player == null || player.ServerPlayerState == null)
            {
                error = "Player state is not available.";
                return false;
            }

            HashSet<ScavPlayer> activeAdmins = GetSet<ScavPlayer>(ServerAdminsField);
            HashSet<string> removedAdmins = GetSet<string>(RemovedServerAdminsField);
            if (activeAdmins == null || removedAdmins == null)
            {
                error = "Together v5 admin state could not be resolved.";
                return false;
            }

            try
            {
                string persistentId = player.GetPersistentId();
                player.ServerPlayerState.admin_privilege = enabled;

                if (enabled)
                {
                    removedAdmins.Remove(persistentId);
                    SendAdminGranted(player.playerId);
                }
                else
                {
                    activeAdmins.Remove(player);
                    removedAdmins.Add(persistentId);
                    SendNativeAdminDenial(player.playerId);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static HashSet<T> GetSet<T>(FieldInfo field)
        {
            return field != null ? field.GetValue(null) as HashSet<T> : null;
        }

        private static void SendAdminGranted(knetid clientId)
        {
            NetDataWriter writer = CatPatchNetwork.CreateWriter(AdminGrantedMessage);
            Net.Server_SendToClients(
                DeliveryMethod.ReliableOrdered,
                writer,
                new List<knetid> { clientId });
        }

        private static void SendNativeAdminDenial(knetid clientId)
        {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_Console_DenyAdminMode);
            writer.Put(false);
            Net.Server_SendToClients(
                DeliveryMethod.ReliableOrdered,
                writer,
                new List<knetid> { clientId });
        }

        private static void OnAdminGrantedReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (!Net.IsRunning || !Net.IsClient)
                return;

            Con.client_adminmode = true;
            Con.client_isadmin = true;

            NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_Console_ActivateAdminMode);
            writer.Put(true);
            writer.Put(string.Empty, 0);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            Util.DoAlert("The host granted you admin mode.", false);
        }
    }
}
