using Together;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class HugPositiveEffects
    {
        private readonly Dictionary<knetid, double> _hugCooldownStartedAtByClientId =
            new Dictionary<knetid, double>();

        public void TryApplyHugMoodReward(knetid clientA, Body bodyA, knetid clientB, Body bodyB)
        {
            double now = Time.realtimeSinceStartupAsDouble;

            float moodDeltaA = ComputeMoodDeltaForClient(clientA, now);
            float moodDeltaB = ComputeMoodDeltaForClient(clientB, now);

            ApplyMoodReward(bodyA, moodDeltaA);
            ApplyMoodReward(bodyB, moodDeltaB);
            ApplyCooldown(clientA, clientB, now);
        }

        private float ComputeMoodDeltaForClient(knetid clientId, double now)
        {
            float moodScale = GetMoodScaleForClient(clientId, now);
            if (moodScale <= 0f)
                return 0f;

            return UnityEngine.Random.Range(1, 6) * moodScale;
        }

        private float GetMoodScaleForClient(knetid clientId, double now)
        {
            double elapsedSeconds;
            if (!TryGetCooldownElapsedSeconds(clientId, now, out elapsedSeconds))
                return 1f;

            if (elapsedSeconds < HugSettings.HugHardLockSeconds)
                return 0f;

            double cooldownSeconds = HugSettings.HugMoodCooldownSeconds;
            if (cooldownSeconds <= 0.01)
                return 1f;

            float cooldownProgress = Mathf.Clamp01((float)(elapsedSeconds / cooldownSeconds));
            if (cooldownProgress < HugSettings.HugMoodScaleStartProgress)
                return 0f;

            if (cooldownProgress >= 1f)
                return 1f;

            float rampProgress = Mathf.Clamp01(
                (cooldownProgress - HugSettings.HugMoodScaleStartProgress)
                / (1f - HugSettings.HugMoodScaleStartProgress));

            return Mathf.Lerp(
                HugSettings.HugMoodScaleMinAtStart,
                1f,
                Mathf.Pow(rampProgress, 1.6f));
        }

        private bool TryGetCooldownElapsedSeconds(knetid clientId, double now, out double elapsedSeconds)
        {
            elapsedSeconds = 0.0;

            double cooldownStartedAt;
            if (!_hugCooldownStartedAtByClientId.TryGetValue(clientId, out cooldownStartedAt))
                return false;

            elapsedSeconds = Math.Max(0.0, now - cooldownStartedAt);
            if (elapsedSeconds >= HugSettings.HugMoodCooldownSeconds)
            {
                _hugCooldownStartedAtByClientId.Remove(clientId);
                return false;
            }

            return true;
        }

        private void ApplyCooldown(knetid clientA, knetid clientB, double now)
        {
            _hugCooldownStartedAtByClientId[clientA] = now;
            _hugCooldownStartedAtByClientId[clientB] = now;
        }

        private static void ApplyMoodReward(Body body, float moodDelta)
        {
            if (body == null || moodDelta <= 0f)
                return;

            body.happiness = Mathf.Clamp(body.happiness + moodDelta, -100f, 100f);
        }
    }
}
