using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Together;
using UnityEngine;

namespace CatPatchspace
{
    /// <summary>
    /// Read-only fingerprint of the live world and the bindings that a future
    /// retained-scene host switch must reconstruct. This probe never changes
    /// authority or blocks the proven reload-based transfer.
    /// </summary>
    internal sealed class RetainedWorldSnapshotProbe
    {
        internal const int CurrentSchema = 1;

        public int SchemaVersion = CurrentSchema;
        public bool Captured;
        public string Error = "";
        public string NetworkRole = "";
        public string EntityManagerType = "";
        public int Layer;
        public int WorldWidth;
        public int WorldHeight;
        public int TotalTraveled;
        public int BlockSnapshotBytes;
        public int FluidSnapshotBytes;
        public string BlockSha256 = "";
        public string FluidSha256 = "";
        public int PlayerCount;
        public int PlayersWithBodies;
        public int PlayersWithBaseEntities;
        public string PlayerIdentitySha256 = "";
        public int NetBodyCount;
        public int SyncInfoCount;
        public int BoundSyncObjectCount;
        public int MissingSyncObjectCount;
        public string SyncObjectIdentitySha256 = "";

        internal static RetainedWorldSnapshotProbe Capture()
        {
            RetainedWorldSnapshotProbe probe = new RetainedWorldSnapshotProbe();
            List<string> errors = new List<string>();

            try
            {
                probe.NetworkRole = Net.IsServer ? "server" : (Net.IsClient ? "client" : "offline");
                probe.EntityManagerType = GetEntityManagerType();
            }
            catch (Exception ex)
            {
                errors.Add("network=" + ex.Message);
            }

            try
            {
                if (WorldGeneration.world == null || !WorldGeneration.world.worldExists)
                    throw new InvalidOperationException("world is not generated");

                probe.Layer = WorldGeneration.world.biomeDepth;
                probe.WorldWidth = checked((int)WorldGeneration.world.width);
                probe.WorldHeight = checked((int)WorldGeneration.world.height);
                probe.TotalTraveled = WorldGeneration.world.totalTraveled;

                byte[] blocks = SavesystemPatch.SerializeWorldBlocks();
                byte[] fluids = SavesystemPatch.SerializeWorldFluids();
                probe.BlockSnapshotBytes = blocks == null ? 0 : blocks.Length;
                probe.FluidSnapshotBytes = fluids == null ? 0 : fluids.Length;
                probe.BlockSha256 = ComputeSha256(blocks);
                probe.FluidSha256 = ComputeSha256(fluids);
            }
            catch (Exception ex)
            {
                errors.Add("terrain=" + ex.Message);
            }

            CapturePlayers(probe, errors);
            CaptureSyncObjects(probe, errors);

            probe.Captured = probe.WorldWidth > 0 && probe.WorldHeight > 0 &&
                             !string.IsNullOrEmpty(probe.BlockSha256) &&
                             !string.IsNullOrEmpty(probe.FluidSha256);
            probe.Error = string.Join("; ", errors.ToArray());
            return probe;
        }

        internal string ToJson()
        {
            try
            {
                return JsonConvert.SerializeObject(this, Formatting.None);
            }
            catch (Exception ex)
            {
                return "{\"SchemaVersion\":" + CurrentSchema +
                       ",\"Captured\":false,\"Error\":\"serialization failed: " +
                       EscapeJson(ex.Message) + "\"}";
            }
        }

        internal static bool TryParse(string json, out RetainedWorldSnapshotProbe probe, out string error)
        {
            probe = null;
            error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                    throw new InvalidOperationException("host probe was empty");
                probe = JsonConvert.DeserializeObject<RetainedWorldSnapshotProbe>(json);
                if (probe == null)
                    throw new InvalidOperationException("host probe deserialized to null");
                if (probe.SchemaVersion != CurrentSchema)
                    throw new InvalidOperationException("unsupported probe schema " + probe.SchemaVersion);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static RetainedWorldProbeComparison Compare(
            RetainedWorldSnapshotProbe host,
            RetainedWorldSnapshotProbe local)
        {
            RetainedWorldProbeComparison result = new RetainedWorldProbeComparison();
            if (host == null || local == null)
            {
                result.Summary = "probe unavailable";
                return result;
            }

            result.LocalCaptured = local.Captured;
            result.WorldShapeMatches = host.Layer == local.Layer &&
                                       host.WorldWidth == local.WorldWidth &&
                                       host.WorldHeight == local.WorldHeight;
            result.BlocksMatch = NonEmptyEqual(host.BlockSha256, local.BlockSha256);
            result.FluidsMatch = NonEmptyEqual(host.FluidSha256, local.FluidSha256);
            result.PlayersMatch = host.PlayerCount == local.PlayerCount &&
                                  NonEmptyEqual(host.PlayerIdentitySha256, local.PlayerIdentitySha256);
            result.SyncObjectsMatch = host.SyncInfoCount == local.SyncInfoCount &&
                                      NonEmptyEqual(host.SyncObjectIdentitySha256,
                                          local.SyncObjectIdentitySha256);
            result.Summary =
                "captured=" + local.Captured +
                " shape=" + MatchText(result.WorldShapeMatches) +
                " blocks=" + MatchText(result.BlocksMatch) +
                " fluids=" + MatchText(result.FluidsMatch) +
                " players=" + MatchText(result.PlayersMatch) +
                " syncObjects=" + MatchText(result.SyncObjectsMatch) +
                " localRole=" + local.NetworkRole +
                " manager=" + local.EntityManagerType +
                (string.IsNullOrEmpty(local.Error) ? "" : " errors=" + local.Error);
            return result;
        }

        internal string Describe()
        {
            return "captured=" + Captured +
                   " role=" + NetworkRole +
                   " manager=" + EntityManagerType +
                   " layer=" + Layer +
                   " world=" + WorldWidth + "x" + WorldHeight +
                   " blocks=" + ShortHash(BlockSha256) + "/" + BlockSnapshotBytes +
                   " fluids=" + ShortHash(FluidSha256) + "/" + FluidSnapshotBytes +
                   " players=" + PlayerCount + "/" + PlayersWithBodies + "/" + PlayersWithBaseEntities +
                   " netBodies=" + NetBodyCount +
                   " syncObjects=" + SyncInfoCount + "/" + BoundSyncObjectCount +
                   (string.IsNullOrEmpty(Error) ? "" : " errors=" + Error);
        }

        private static void CapturePlayers(RetainedWorldSnapshotProbe probe, List<string> errors)
        {
            try
            {
                List<string> identities = new List<string>();
                List<ScavPlayer> players = new List<ScavPlayer>(ScavPlayer.ClientIdToPlayerDict.Values);
                for (int i = 0; i < players.Count; i++)
                {
                    ScavPlayer player = players[i];
                    if (player == null)
                        continue;

                    probe.PlayerCount++;
                    Body body = player.body;
                    NetBody netBody = body == null ? null : body.GetComponent<NetBody>();
                    if (body != null)
                        probe.PlayersWithBodies++;
                    if (player.BasePlayer != null)
                        probe.PlayersWithBaseEntities++;

                    identities.Add(player.SteamId + "|" + player.playerId + "|" +
                                   (netBody == null ? "none" : netBody.netId.ToString()));
                }
                identities.Sort(StringComparer.Ordinal);
                probe.PlayerIdentitySha256 = ComputeSha256(Encoding.UTF8.GetBytes(
                    string.Join("\n", identities.ToArray())));
                probe.NetBodyCount = NetBody.all_instances == null ? 0 : NetBody.all_instances.Count;
            }
            catch (Exception ex)
            {
                errors.Add("players=" + ex.Message);
            }
        }

        private static void CaptureSyncObjects(RetainedWorldSnapshotProbe probe, List<string> errors)
        {
            try
            {
                List<string> identities = new List<string>();
                List<KeyValuePair<knetid, SyncInfo>> objects =
                    new List<KeyValuePair<knetid, SyncInfo>>(NetObjectRegistry.NetIdToSyncInfoDict);
                for (int i = 0; i < objects.Count; i++)
                {
                    KeyValuePair<knetid, SyncInfo> entry = objects[i];
                    SyncInfo sync = entry.Value;
                    if (sync == null)
                        continue;

                    probe.SyncInfoCount++;
                    bool hasObject = sync.go != null;
                    if (hasObject)
                        probe.BoundSyncObjectCount++;
                    else
                        probe.MissingSyncObjectCount++;

                    string resource = sync.resourceId == null ? "" : sync.resourceId.Value;
                    identities.Add(entry.Key + "|" + resource + "|" + hasObject);
                }
                identities.Sort(StringComparer.Ordinal);
                probe.SyncObjectIdentitySha256 = ComputeSha256(Encoding.UTF8.GetBytes(
                    string.Join("\n", identities.ToArray())));
            }
            catch (Exception ex)
            {
                errors.Add("syncObjects=" + ex.Message);
            }
        }

        private static string GetEntityManagerType()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo serverProperty = typeof(Net).GetProperty("ServerEntityManager", flags);
            PropertyInfo clientProperty = typeof(Net).GetProperty("ClientEntityManager", flags);
            object server = serverProperty == null ? null : serverProperty.GetValue(null, null);
            object client = clientProperty == null ? null : clientProperty.GetValue(null, null);
            if (server != null)
                return server.GetType().FullName;
            if (client != null)
                return client.GetType().FullName;
            return "none";
        }

        private static string ComputeSha256(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "";
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static bool NonEmptyEqual(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string MatchText(bool value)
        {
            return value ? "match" : "DIFF";
        }

        private static string ShortHash(string hash)
        {
            return string.IsNullOrEmpty(hash) ? "none" :
                hash.Substring(0, Math.Min(12, hash.Length));
        }

        private static string EscapeJson(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    internal sealed class RetainedWorldProbeComparison
    {
        public bool LocalCaptured;
        public bool WorldShapeMatches;
        public bool BlocksMatch;
        public bool FluidsMatch;
        public bool PlayersMatch;
        public bool SyncObjectsMatch;
        public string Summary = "";
    }
}
