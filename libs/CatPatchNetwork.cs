using System.Collections.Generic;
using LiteNetLib.Utils;
using Together;

namespace CatPatchspace
{
    internal static class CatPatchNetwork
    {
        private const string MessagePrefix = "meow.catpatch.v5.";

        private static readonly HashSet<ushort> ServerReceivers = new HashSet<ushort>();
        private static readonly HashSet<ushort> ClientReceivers = new HashSet<ushort>();

        public static NetDataWriter CreateWriter(ushort messageKey)
        {
            return Multiplayer.CreateNamedWriter(GetMessageName(messageKey));
        }

        public static NetDataWriter CreateWriter(ushort messageKey, int initialSize, bool autoResize)
        {
            return CreateWriter(messageKey);
        }

        public static bool IsRegistered(bool serverReceiver, ushort messageKey)
        {
            return serverReceiver
                ? ServerReceivers.Contains(messageKey)
                : ClientReceivers.Contains(messageKey);
        }

        public static void RegisterServer(
            ushort messageKey,
            KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate handler)
        {
            if (!ServerReceivers.Add(messageKey))
                return;

            Multiplayer.RegisterCustomServerReceiver(
                GetMessageName(messageKey),
                delegate(ScavPlayer sender, NetDataReader reader)
                {
                    if (sender == null)
                        return;

                    handler(sender.playerId, ref reader);
                });
        }

        public static void RegisterClient(
            ushort messageKey,
            KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate handler)
        {
            if (!ClientReceivers.Add(messageKey))
                return;

            Multiplayer.RegisterCustomClientReceiver(
                GetMessageName(messageKey),
                delegate(NetDataReader reader)
                {
                    handler(CatPatchNetId.None, ref reader);
                });
        }

        private static string GetMessageName(ushort messageKey)
        {
            return MessagePrefix + messageKey.ToString();
        }
    }

    // Temporary source-compatibility surface while the feature controllers are
    // moved from the v4 multiplayer API to Together's v5 API.
    internal static class KrokoshaScavMultiplayer
    {
        internal delegate void KrokoshaHandleNamedMessageDelegate(knetid senderClientId, ref NetDataReader reader);

        public static bool network_system_is_running { get { return Net.IsRunning; } }
        public static bool network_system_im_client_and_waiting_connecting { get { return Net.IsClient && Net.IsConnecting; } }
        public static bool is_server { get { return Net.IsServer; } }
        public static bool is_dedicated_server { get { return Net.IsDedicatedServer; } }
        public static bool IsNetworkActiveAndIsHost() { return Net.IsRunning && Net.IsHost; }
        public static bool IsNetworkActiveAndIsServer() { return Net.IsRunning && Net.IsServer; }
        public static MultiplayerGameRules rules { get { return Multiplayer.rules; } set { Multiplayer.rules = value; } }
        public static string multiplayer_status_message { get { return Multiplayer.multiplayerStatusMessage; } }
        public static double last_multiplayer_status_message_change_time { get { return Multiplayer.lastMultiplayerStatusMessageChangeTime; } }
        public static bool showMultiplayerMenu { get { return Multiplayer.showMultiplayerMenu; } set { Multiplayer.showMultiplayerMenu = value; } }
        public static string INPUT_PASSWORD { get { return Multiplayer.INPUT_PASSWORD; } set { Multiplayer.INPUT_PASSWORD = value; } }
        public static string INPUT_USERNAME { get { return UIMainMenu.USERINPUT_NAME; } set { UIMainMenu.USERINPUT_NAME = value; } }
    }
}
