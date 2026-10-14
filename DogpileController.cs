using Together;
using System.Collections.Generic;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class DogpileController : MonoBehaviour
    {
        private const double ScanIntervalSeconds = 0.25;

        private readonly Dictionary<Body, double> _sleepStartedAtByBody = new Dictionary<Body, double>();
        private readonly Dictionary<Body, DogpileBoostState> _boostStatesByBody = new Dictionary<Body, DogpileBoostState>();
        private readonly List<Body> _sleepingBodies = new List<Body>();
        private readonly HashSet<Body> _activeBoostedBodies = new HashSet<Body>();
        private readonly HashSet<Body> _seenBodies = new HashSet<Body>();
        private readonly List<Body> _staleBodies = new List<Body>();
        private double _nextScanAt;

        private void Update()
        {
            if (!IsDogpileAvailable())
            {
                ClearBoosts();
                _sleepStartedAtByBody.Clear();
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextScanAt)
                return;

            _nextScanAt = now + ScanIntervalSeconds;
            UpdateDogpiles(now);
        }

        private void UpdateDogpiles(double now)
        {
            RefreshSleepingBodies(now);
            _activeBoostedBodies.Clear();

            if (_sleepingBodies.Count < 2)
            {
                ClearInactiveBoosts();
                return;
            }

            bool[] visited = new bool[_sleepingBodies.Count];
            List<Body> cluster = new List<Body>();

            for (int i = 0; i < _sleepingBodies.Count; i++)
            {
                if (visited[i])
                    continue;

                cluster.Clear();
                CollectCluster(i, visited, cluster);

                if (cluster.Count < 2)
                    continue;

                ApplyCluster(cluster);
            }

            ClearInactiveBoosts();
        }

        private void RefreshSleepingBodies(double now)
        {
            _sleepingBodies.Clear();
            _seenBodies.Clear();

            foreach (ScavPlayer player in ScavPlayer.ClientIdToPlayerDict.Values)
            {
                Body body = player != null ? player.body : null;
                if (!IsEligibleSleeper(body))
                    continue;

                _sleepingBodies.Add(body);
                _seenBodies.Add(body);

                if (!_sleepStartedAtByBody.ContainsKey(body))
                    _sleepStartedAtByBody[body] = now;
            }

            _staleBodies.Clear();
            foreach (KeyValuePair<Body, double> pair in _sleepStartedAtByBody)
            {
                if (pair.Key == null || !_seenBodies.Contains(pair.Key))
                    _staleBodies.Add(pair.Key);
            }

            for (int i = 0; i < _staleBodies.Count; i++)
                _sleepStartedAtByBody.Remove(_staleBodies[i]);
        }

        private void CollectCluster(int startIndex, bool[] visited, List<Body> cluster)
        {
            Queue<int> queue = new Queue<int>();
            queue.Enqueue(startIndex);
            visited[startIndex] = true;

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                Body body = _sleepingBodies[index];
                cluster.Add(body);

                for (int i = 0; i < _sleepingBodies.Count; i++)
                {
                    if (visited[i])
                        continue;

                    if (!AreCloseEnough(body, _sleepingBodies[i]))
                        continue;

                    visited[i] = true;
                    queue.Enqueue(i);
                }
            }
        }

        private void ApplyCluster(List<Body> cluster)
        {
            cluster.Sort(CompareSleepStartTime);

            for (int i = 1; i < cluster.Count; i++)
            {
                Body body = cluster[i];
                _activeBoostedBodies.Add(body);
                ApplyBoost(body);
            }
        }

        private void ApplyBoost(Body body)
        {
            if (body == null)
                return;

            DogpileBoostState state;
            if (!_boostStatesByBody.TryGetValue(body, out state))
            {
                state = new DogpileBoostState
                {
                    OriginalForcedSleepQuality = body.forcedSleepQuality
                };

                _boostStatesByBody[body] = state;
            }

            body.eyeCloseTime = Mathf.Max(body.eyeCloseTime, 0.3f);
            if (!state.AnnouncedLocally && IsLocalBody(body))
            {
                state.AnnouncedLocally = true;
                HugController.ShowLocalMessage("Dogpile sleep boost activated. Sleep quality: Good.");
            }

            bool canApplyAuthoritativeEffects = KrokoshaScavMultiplayer.is_server;
            if (canApplyAuthoritativeEffects)
            {
                body.forcedSleepQuality = Body.SleepQuality.Good;
                state.AppliedForcedSleepQuality = true;
            }
        }

        private void ClearInactiveBoosts()
        {
            List<Body> inactiveBodies = new List<Body>();
            foreach (KeyValuePair<Body, DogpileBoostState> pair in _boostStatesByBody)
            {
                if (pair.Key == null || !_activeBoostedBodies.Contains(pair.Key) || !IsEligibleSleeper(pair.Key))
                    inactiveBodies.Add(pair.Key);
            }

            for (int i = 0; i < inactiveBodies.Count; i++)
            {
                Body body = inactiveBodies[i];
                RestoreBoost(body);
                _boostStatesByBody.Remove(body);
            }
        }

        private void ClearBoosts()
        {
            List<Body> boostedBodies = new List<Body>(_boostStatesByBody.Keys);
            for (int i = 0; i < boostedBodies.Count; i++)
                RestoreBoost(boostedBodies[i]);

            _boostStatesByBody.Clear();
            _activeBoostedBodies.Clear();
        }

        private void RestoreBoost(Body body)
        {
            if (body == null)
                return;

            DogpileBoostState state;
            if (!_boostStatesByBody.TryGetValue(body, out state))
                return;

            if (state.AppliedForcedSleepQuality
                && body.forcedSleepQuality.HasValue
                && body.forcedSleepQuality.Value == Body.SleepQuality.Good)
                body.forcedSleepQuality = state.OriginalForcedSleepQuality;
        }

        private static bool IsEligibleSleeper(Body body)
        {
            return IsEligibleBody(body)
                && body.sleeping;
        }

        private static bool IsDogpileAvailable()
        {
            return CatPatchServerSettings.DogpileEnabled
                && KrokoshaScavMultiplayer.network_system_is_running
                && IsKrokSleepEnabled();
        }

        private static bool IsKrokSleepEnabled()
        {
            try
            {
                MultiplayerGameRules rules = KrokoshaScavMultiplayer.rules;
                return rules.EnableSleep;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsEligibleBody(Body body)
        {
            return body != null
                && body.gameObject != null
                && body.gameObject.activeInHierarchy
                && body.alive;
        }

        private static bool IsLocalBody(Body body)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            return localPlayer != null && localPlayer.body == body;
        }

        private static bool AreCloseEnough(Body bodyA, Body bodyB)
        {
            if (bodyA == null || bodyB == null)
                return false;

            float radius = CatPatchServerSettings.DogpileRadius;
            float radiusSquared = radius * radius;
            return ((Vector2)bodyA.transform.position - (Vector2)bodyB.transform.position).sqrMagnitude <= radiusSquared;
        }

        private int CompareSleepStartTime(Body bodyA, Body bodyB)
        {
            double timeA = GetSleepStartTime(bodyA);
            double timeB = GetSleepStartTime(bodyB);
            int timeCompare = timeA.CompareTo(timeB);
            if (timeCompare != 0)
                return timeCompare;

            int idA = bodyA != null ? bodyA.GetInstanceID() : 0;
            int idB = bodyB != null ? bodyB.GetInstanceID() : 0;
            return idA.CompareTo(idB);
        }

        private double GetSleepStartTime(Body body)
        {
            double startedAt;
            if (body != null && _sleepStartedAtByBody.TryGetValue(body, out startedAt))
                return startedAt;

            return 0.0;
        }

        private sealed class DogpileBoostState
        {
            public Body.SleepQuality? OriginalForcedSleepQuality;
            public bool AppliedForcedSleepQuality;
            public bool AnnouncedLocally;
        }
    }
}
