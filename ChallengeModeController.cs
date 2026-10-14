using Together;
using System.Collections.Generic;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class ChallengeModeController : MonoBehaviour
    {
        private const double TickIntervalSeconds = 1.0;

        private static ChallengeModeController _instance;
        private readonly HashSet<string> _spawnedTrapLayerKeys = new HashSet<string>();
        private readonly HashSet<int> _kitGrantedBodyIds = new HashSet<int>();
        private readonly HashSet<string> _missingResourceWarnings = new HashSet<string>();
        private int _lastWorldInstanceId;
        private double _nextTickAt;

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Update()
        {
            if (!CatPatchServerSettings.ChallengeModeEnabled || !KrokoshaScavMultiplayer.network_system_is_running)
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextTickAt)
                return;

            _nextTickAt = now + TickIntervalSeconds;

            if (KrokoshaScavMultiplayer.is_server)
            {
                TrySpawnChallengeTraps();
                TryGrantStarterKits();
            }
        }

        private void TrySpawnChallengeTraps()
        {
            WorldGeneration world = WorldGeneration.world;
            if (world == null || world.generatingWorld || world.instantiatingWorld)
                return;

            if (world.timeSinceFinishedGeneration < 0.5f)
                return;

            int worldInstanceId = world.GetInstanceID();
            if (_lastWorldInstanceId != worldInstanceId)
            {
                _lastWorldInstanceId = worldInstanceId;
                _spawnedTrapLayerKeys.Clear();
            }

            string layerKey = world.totalTraveled + ":" + world.biomeDepth + ":" + world.amountOfLayers;
            if (_spawnedTrapLayerKeys.Contains(layerKey))
                return;

            _spawnedTrapLayerKeys.Add(layerKey);
            int spawned = SpawnTrapPasses(world);
            Plugin.Logger.LogInfo("Challenge mode WIP trap scatter spawned " + spawned + " trap(s) for layer " + layerKey + ".");
        }

        private int SpawnTrapPasses(WorldGeneration world)
        {
            if (world == null)
                return 0;

            int passes = 0;
            float density = CatPatchServerSettings.ChallengeTrapDensity;
            for (int i = 0; i < MfcHazardCatalog.Hazards.Length; i++)
            {
                MfcHazardDefinition hazard = MfcHazardCatalog.Hazards[i];
                if (hazard.Id == "turret")
                    continue;

                if (DistributeChallengeTrap(world, hazard, density))
                    passes++;
            }

            if (SpawnChallengeTurrets(world, density))
                passes++;

            if (SpawnElderThornbacks(world))
                passes++;

            Plugin.Logger.LogInfo(
                "Challenge mode WIP ran "
                + passes
                + " vanilla trap distribution pass(es), density="
                + density.ToString("0.##")
                + ".");

            return passes;
        }

        private bool DistributeChallengeTrap(WorldGeneration world, MfcHazardDefinition hazard, float density)
        {
            GameObject prefab = LoadChallengePrefab(hazard.Id);
            if (prefab == null)
                return false;

            float spawnYOffset = hazard.SpawnYOffset;
            float spawnYOffsetDeviation = hazard.SpawnYOffsetDeviation;
            if (hazard.Id == "landmine")
            {
                if (CatPatchServerSettings.ChallengeRandomLandmineHeight)
                {
                    spawnYOffset = 5.0f;
                    spawnYOffsetDeviation = 5.0f;
                }
                else
                {
                    spawnYOffset = 0.4f;
                    spawnYOffsetDeviation = 0.0f;
                }
            }

            float trapDensity = density * CatPatchServerSettings.GetChallengeTrapMultiplier(hazard.Id);
            if (trapDensity <= 0.0f)
                return true;

            float randomRotation = hazard.RandomRotation;
            bool randomFlip = hazard.RandomFlip;
            if (hazard.Id == "spikestabber" &&
                CatPatchServerSettings.ChallengeRandomSpikeOrientation)
            {
                randomRotation = 180.0f;
                randomFlip = true;
            }

            try
            {
                world.DistributeEntities(
                    prefab,
                    hazard.MinPerChunk * trapDensity,
                    hazard.MaxPerChunk * trapDensity,
                    spawnYOffset,
                    randomRotation,
                    spawnYOffsetDeviation,
                    false,
                    randomFlip,
                    null,
                    hazard.IsTrap,
                    hazard.Direction,
                    false);
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning("Challenge mode WIP failed to distribute trap " + hazard.Id + ": " + ex.Message);
                return false;
            }

            return true;
        }

        private bool SpawnChallengeTurrets(WorldGeneration world, float density)
        {
            GameObject prefab = LoadChallengePrefab("turret");
            if (world == null || prefab == null)
                return false;

            float trapDensity = density * CatPatchServerSettings.GetChallengeTrapMultiplier("turret");
            if (trapDensity <= 0.0f)
                return true;

            int targetCount = Mathf.RoundToInt(
                Mathf.Max(1f, world.chunkWidth * world.chunkHeight) *
                Random.Range(0.03f, 0.05f) *
                trapDensity);

            int spawned = 0;
            for (int i = 0; i < targetCount; i++)
            {
                if (TrySpawnTurret(world, prefab))
                    spawned++;
            }

            Plugin.Logger.LogInfo("Challenge mode WIP turret pass target=" + targetCount + ", spawned=" + spawned + ".");
            return true;
        }

        private bool TrySpawnTurret(WorldGeneration world, GameObject prefab)
        {
            if (world == null || prefab == null)
                return false;

            try
            {
                float maxY = world.halfHeight;
                if (PlayerCamera.main != null && PlayerCamera.main.body != null)
                    maxY = PlayerCamera.main.body.transform.position.y - 5.0f;

                Vector2 origin = new Vector2(
                    Random.Range((float)(-(float)world.halfWidth), world.halfWidth),
                    Random.Range((float)(-(float)world.halfHeight), maxY));

                float side = Random.value > 0.5f ? 1.0f : -1.0f;
                Vector2 direction = Vector2.right * side;
                RaycastHit2D hit = Physics2D.Raycast(
                    origin,
                    direction,
                    WorldGeneration.CHUNKSIZE,
                    LayerMask.GetMask(new string[] { "Ground" }));

                if (!hit)
                    return false;

                GameObject turret = Instantiate(prefab, hit.point + Vector2.left * side, Quaternion.identity);
                turret.transform.localScale = new Vector3(-side, 1.0f, 1.0f);
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning("Challenge mode WIP failed to spawn turret: " + ex.Message);
                return false;
            }
        }

        private bool SpawnElderThornbacks(WorldGeneration world)
        {
            int count = CatPatchServerSettings.ChallengeElderThornbackCount;
            if (count <= 0)
                return true;

            GameObject prefab = LoadChallengePrefab("thornbackelder");
            if (world == null || prefab == null)
                return false;

            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    Instantiate(
                        prefab,
                        new Vector2(
                            Random.Range((float)(-(float)world.halfWidth), world.halfWidth),
                            Random.Range((float)(-(float)world.halfHeight), world.halfHeight)),
                        Quaternion.identity);

                    spawned++;
                }
                catch (System.Exception ex)
                {
                    Plugin.Logger.LogWarning("Challenge mode WIP failed to spawn elder thornback: " + ex.Message);
                }
            }

            Plugin.Logger.LogInfo("Challenge mode WIP elder thornback pass target=" + count + ", spawned=" + spawned + ".");
            return true;
        }

        private GameObject LoadChallengePrefab(string resourceName)
        {
            GameObject prefab = Resources.Load<GameObject>(resourceName);
            if (prefab != null)
                return prefab;

            if (_missingResourceWarnings.Add(resourceName))
                Plugin.Logger.LogWarning("Challenge mode resource not found: " + resourceName);

            return null;
        }

        private void TryGrantStarterKits()
        {
            string[] kitItemIds = GetStarterKitItemIds();
            if (kitItemIds.Length == 0)
                return;

            GrantStarterKitToLocalServerBody(kitItemIds);
            IReadOnlyList<knetid> remoteClientIds = GetClientIdsExceptHost();
            for (int i = 0; i < remoteClientIds.Count; i++)
                GrantStarterKitToClient(remoteClientIds[i], kitItemIds);
        }

        private void GrantStarterKitToLocalServerBody(string[] kitItemIds)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null)
                return;

            GrantStarterKit(localPlayer.playerId, localPlayer.body, kitItemIds);
        }

        private void GrantStarterKitToClient(knetid clientId, string[] kitItemIds)
        {
            ScavPlayer player;
            Body body;
            if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out player, out body))
                return;

            GrantStarterKit(clientId, body, kitItemIds);
        }

        private void GrantStarterKit(knetid clientId, Body body, string[] kitItemIds)
        {
            if (body == null || !body.alive)
                return;

            int bodyId = body.GetInstanceID();
            if (_kitGrantedBodyIds.Contains(bodyId))
                return;

            int grantedCount = IsDefaultChallengeLoadout(kitItemIds)
                ? GrantDefaultChallengeLoadout(body)
                : GrantConfiguredStarterKit(body, kitItemIds);

            if (grantedCount <= 0)
                return;

            _kitGrantedBodyIds.Add(bodyId);
            Plugin.Logger.LogInfo("Challenge mode granted starter kit to client " + clientId + ".");
        }

        private int GrantConfiguredStarterKit(Body body, string[] kitItemIds)
        {
            int grantedCount = 0;
            for (int i = 0; i < kitItemIds.Length; i++)
            {
                if (TryGiveItem(body, kitItemIds[i]))
                    grantedCount++;
            }

            return grantedCount;
        }

        private int GrantDefaultChallengeLoadout(Body body)
        {
            int grantedCount = 0;

            Item medkit = CreateItem("medkit", body.transform.position + Vector3.up * 0.25f);
            if (medkit != null)
            {
                if (TryLoadIntoContainer(medkit, "combatpen"))
                    grantedCount++;

                for (int i = 0; i < 10; i++)
                {
                    if (TryLoadIntoContainer(medkit, "medicalsuture"))
                        grantedCount++;
                }

                if (TryLoadIntoContainer(medkit, "tourniquet"))
                    grantedCount++;

                if (TryGiveExistingItem(body, medkit))
                    grantedCount++;
            }

            Item belt = CreateItem("belt", body.transform.position + Vector3.up * 0.35f);
            Item pistol = CreateLoadedPistol(body.transform.position + Vector3.up * 0.45f);
            if (belt != null)
            {
                bool woreBelt = TryWearItem(body, belt);
                if (!woreBelt && TryGiveExistingItem(body, belt))
                    grantedCount++;
                else if (woreBelt)
                    grantedCount++;

                if (pistol != null)
                {
                    if (TryLoadIntoContainer(belt, pistol))
                        grantedCount++;
                    else if (TryGiveExistingItem(body, pistol))
                        grantedCount++;
                }
            }
            else if (pistol != null && TryGiveExistingItem(body, pistol))
            {
                grantedCount++;
            }

            if (TryGiveItem(body, "chestdrain"))
                grantedCount++;

            Item headlamp = CreateItem("headlamp", body.transform.position + Vector3.up * 0.55f);
            if (headlamp != null)
            {
                bool woreHeadlamp = TryWearItem(body, headlamp);
                if (!woreHeadlamp && TryGiveExistingItem(body, headlamp))
                    grantedCount++;
                else if (woreHeadlamp)
                    grantedCount++;
            }

            return grantedCount;
        }

        private bool TryGiveItem(Body body, string itemId)
        {
            if (body == null || string.IsNullOrWhiteSpace(itemId))
                return false;

            Item item = CreateItem(itemId.Trim(), body.transform.position + Vector3.up * 0.25f);
            if (item == null)
                return false;

            return TryGiveExistingItem(body, item);
        }

        private bool TryGiveExistingItem(Body body, Item item)
        {
            if (body == null || item == null)
                return false;

            try
            {
                int slot = body.FirstEmptySlot().GetValueOrDefault(-1);
                if (slot >= 0)
                    body.PickUpItem(item, slot, true);
                else
                    body.AutoPickUpItem(item);

                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning("Challenge mode failed to give item " + item.id + ": " + ex.Message);
                return false;
            }
        }

        private bool TryWearItem(Body body, Item item)
        {
            if (body == null || item == null)
                return false;

            try
            {
                if (item.Stats == null || !item.Stats.wearable)
                    return false;

                body.WearWearable(item);
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning("Challenge mode failed to wear item " + item.id + ": " + ex.Message);
                return false;
            }
        }

        private bool TryLoadIntoContainer(Item containerItem, string itemId)
        {
            if (containerItem == null || string.IsNullOrWhiteSpace(itemId))
                return false;

            Item item = CreateItem(itemId.Trim(), containerItem.transform.position + Vector3.up * 0.05f);
            if (item == null)
                return false;

            if (TryLoadIntoContainer(containerItem, item))
                return true;

            Destroy(item.gameObject);
            return false;
        }

        private bool TryLoadIntoContainer(Item containerItem, Item item)
        {
            if (containerItem == null || item == null || containerItem.container == null)
                return false;

            try
            {
                if (!containerItem.container.CanHoldItem(item))
                    return false;

                containerItem.container.LoadItem(item);
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning(
                    "Challenge mode failed to load item "
                    + item.id
                    + " into "
                    + containerItem.id
                    + ": "
                    + ex.Message);
                return false;
            }
        }

        private Item CreateLoadedPistol(Vector3 position)
        {
            Item pistol = CreateItem("pistol", position);
            if (pistol == null)
                return null;

            GunScript gun = pistol.GetComponent<GunScript>();
            if (gun == null)
                return pistol;

            Item magazine = CreateItem("smallmagazine", position + Vector3.up * 0.05f);
            AmmoScript magazineAmmo = magazine != null ? magazine.GetComponent<AmmoScript>() : null;
            if (magazineAmmo == null)
            {
                if (magazine != null)
                    Destroy(magazine.gameObject);

                return pistol;
            }

            magazineAmmo.rounds = Mathf.Max(magazineAmmo.rounds, magazineAmmo.maxRounds);
            try
            {
                gun.LoadMag(magazineAmmo);
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning("Challenge mode failed to load pistol magazine: " + ex.Message);
                Destroy(magazine.gameObject);
            }

            return pistol;
        }

        private Item CreateItem(string itemId, Vector3 position)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                return null;

            GameObject itemPrefab = Resources.Load<GameObject>(itemId.Trim());
            if (itemPrefab == null)
            {
                if (_missingResourceWarnings.Add(itemId))
                    Plugin.Logger.LogWarning("Challenge mode kit item resource not found: " + itemId);

                return null;
            }

            GameObject itemObject = Instantiate(itemPrefab, position, Quaternion.identity);
            Item item = itemObject != null ? itemObject.GetComponent<Item>() : null;
            if (item != null)
                return item;

            if (itemObject != null)
                Destroy(itemObject);

            Plugin.Logger.LogWarning("Challenge mode kit resource has no Item component: " + itemId);
            return null;
        }

        private static string[] GetStarterKitItemIds()
        {
            string rawItems = CatPatchServerSettings.ChallengeStarterKitItems;
            if (string.IsNullOrWhiteSpace(rawItems))
                return new string[0];

            string[] splitItems = rawItems.Split(',');
            List<string> itemIds = new List<string>();
            for (int i = 0; i < splitItems.Length; i++)
            {
                string itemId = splitItems[i].Trim();
                if (!string.IsNullOrWhiteSpace(itemId))
                    itemIds.Add(itemId);
            }

            return itemIds.ToArray();
        }

        private static bool IsDefaultChallengeLoadout(string[] kitItemIds)
        {
            if (kitItemIds == null || kitItemIds.Length == 0)
                return false;

            string[] defaultItems = CatPatchServerSettings.DefaultChallengeStarterKitItems.Split(',');
            if (kitItemIds.Length != defaultItems.Length)
                return false;

            for (int i = 0; i < defaultItems.Length; i++)
            {
                if (kitItemIds[i] != defaultItems[i])
                    return false;
            }

            return true;
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
    }
}
