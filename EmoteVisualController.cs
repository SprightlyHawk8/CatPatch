using System.Collections.Generic;
using Together;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class EmoteVisualController
    {
        private enum EmotePhase
        {
            Hold,
            SitPrep,
            Transition
        }

        private sealed class ActiveEmote
        {
            public knetid ClientId;
            public EmoteDefinition Definition;
            public float OriginalBodyAnimatorSpeed;
            public float OriginalArmsAnimatorSpeed;
            public EmotePhase Phase;
            public float PhaseStartedAt;
        }

        private readonly Dictionary<knetid, ActiveEmote> _activeEmotesByClientId =
            new Dictionary<knetid, ActiveEmote>();

        public void Start(knetid clientId, EmoteDefinition emote)
        {
            if (emote == null)
                return;

            Body body;
            if (!TryGetBody(clientId, out body) || !CanPlayEmote(body))
                return;

            ActiveEmote existingEmote;
            if (_activeEmotesByClientId.TryGetValue(clientId, out existingEmote))
                RestoreDefaultState(body, existingEmote);

            ActiveEmote activeEmote = new ActiveEmote
            {
                ClientId = clientId,
                Definition = emote,
                OriginalBodyAnimatorSpeed = body.bodyAnimator != null ? body.bodyAnimator.speed : 1f,
                OriginalArmsAnimatorSpeed = body.armsAnimator != null ? body.armsAnimator.speed : 1f,
                Phase = emote.StartFromSit ? EmotePhase.SitPrep : EmotePhase.Hold,
                PhaseStartedAt = Time.time
            };

            _activeEmotesByClientId[clientId] = activeEmote;

            if (activeEmote.Phase == EmotePhase.SitPrep)
                PlaySitPrep(body);
            else
                PlayEmote(body, activeEmote);
        }

        public void StopAll()
        {
            foreach (KeyValuePair<knetid, ActiveEmote> pair in _activeEmotesByClientId)
            {
                Body body;
                if (TryGetBody(pair.Key, out body))
                    RestoreDefaultState(body, pair.Value);
            }

            _activeEmotesByClientId.Clear();
        }

        public void LateTick()
        {
            if (_activeEmotesByClientId.Count == 0)
                return;

            List<knetid> inactiveClientIds = null;
            foreach (KeyValuePair<knetid, ActiveEmote> pair in _activeEmotesByClientId)
            {
                Body body;
                if (!TryGetBody(pair.Key, out body) || ShouldCancelEmote(body))
                {
                    if (inactiveClientIds == null)
                        inactiveClientIds = new List<knetid>();

                    inactiveClientIds.Add(pair.Key);
                    continue;
                }

                KeepEmoteActive(body, pair.Value);
            }

            for (int i = 0; inactiveClientIds != null && i < inactiveClientIds.Count; i++)
            {
                ActiveEmote emote;
                if (!_activeEmotesByClientId.TryGetValue(inactiveClientIds[i], out emote))
                    continue;

                Body body;
                if (TryGetBody(inactiveClientIds[i], out body))
                    RestoreDefaultState(body, emote);

                _activeEmotesByClientId.Remove(inactiveClientIds[i]);
            }
        }

        private static void KeepEmoteActive(Body body, ActiveEmote activeEmote)
        {
            EmoteDefinition emote = activeEmote != null ? activeEmote.Definition : null;
            if (body == null || emote == null)
                return;

            if (emote.StartFromSit && activeEmote.Phase != EmotePhase.SitPrep)
                body.idleTime = 0f;

            if (activeEmote.Phase == EmotePhase.SitPrep)
            {
                if (Time.time - activeEmote.PhaseStartedAt < emote.SitPrepSeconds)
                {
                    KeepSitPrepActive(body);
                    return;
                }

                activeEmote.Phase = EmotePhase.Transition;
                activeEmote.PhaseStartedAt = Time.time;
                body.idleTime = 0f;
                PlayEmoteTransition(body, activeEmote);
                return;
            }

            if (activeEmote.Phase == EmotePhase.Transition)
            {
                if (!IsCurrentClip(body.bodyAnimator, emote.BodyState) ||
                    !IsCurrentClip(body.armsAnimator, emote.ArmsState))
                {
                    PlayEmoteTransition(body, activeEmote);
                }

                if (emote.KeepEyesClosed)
                    body.eyeCloseTime = Mathf.Max(body.eyeCloseTime, 0.3f);

                if (Time.time - activeEmote.PhaseStartedAt < emote.TransitionSeconds)
                    return;

                activeEmote.Phase = EmotePhase.Hold;
                activeEmote.PhaseStartedAt = Time.time;
            }

            if (!IsCurrentClip(body.bodyAnimator, emote.BodyState) ||
                !IsCurrentClip(body.armsAnimator, emote.ArmsState))
            {
                PlayEmote(body, activeEmote);
            }

            if (emote.FreezeAtEnd)
                FreezeAnimators(body, emote);

            if (emote.KeepEyesClosed)
                body.eyeCloseTime = Mathf.Max(body.eyeCloseTime, 0.3f);
        }

        private static void PlayEmote(Body body, ActiveEmote activeEmote)
        {
            EmoteDefinition emote = activeEmote != null ? activeEmote.Definition : null;
            if (body == null || emote == null)
                return;

            PlayAnimatorState(body.bodyAnimator, emote.BodyState, float.NegativeInfinity);
            PlayAnimatorState(body.armsAnimator, emote.ArmsState, float.NegativeInfinity);

            if (emote.FreezeAtEnd)
                FreezeAnimators(body, emote);

            if (emote.Id == "sit")
                body.idleTime = Mathf.Max(body.idleTime, 13f);

            if (emote.KeepEyesClosed)
                body.eyeCloseTime = Mathf.Max(body.eyeCloseTime, 0.8f);
        }

        private static void PlayEmoteTransition(Body body, ActiveEmote activeEmote)
        {
            EmoteDefinition emote = activeEmote != null ? activeEmote.Definition : null;
            if (body == null || emote == null)
                return;

            if (body.bodyAnimator != null)
                body.bodyAnimator.speed = activeEmote.OriginalBodyAnimatorSpeed <= 0f
                    ? 1f
                    : activeEmote.OriginalBodyAnimatorSpeed;

            if (body.armsAnimator != null)
                body.armsAnimator.speed = activeEmote.OriginalArmsAnimatorSpeed <= 0f
                    ? 1f
                    : activeEmote.OriginalArmsAnimatorSpeed;

            PlayAnimatorState(body.bodyAnimator, emote.BodyState, 0f);
            PlayAnimatorState(body.armsAnimator, emote.ArmsState, 0f);

            if (emote.KeepEyesClosed)
                body.eyeCloseTime = Mathf.Max(body.eyeCloseTime, 0.8f);
        }

        private static void RestoreDefaultState(Body body, ActiveEmote activeEmote)
        {
            EmoteDefinition emote = activeEmote != null ? activeEmote.Definition : null;
            if (body == null || emote == null)
                return;

            if (body.bodyAnimator != null)
                body.bodyAnimator.speed = activeEmote.OriginalBodyAnimatorSpeed <= 0f
                    ? 1f
                    : activeEmote.OriginalBodyAnimatorSpeed;

            if (body.armsAnimator != null)
                body.armsAnimator.speed = activeEmote.OriginalArmsAnimatorSpeed <= 0f
                    ? 1f
                    : activeEmote.OriginalArmsAnimatorSpeed;

            if (emote.StartFromSit && IsCurrentClip(body.bodyAnimator, "ExperimentSit"))
                body.bodyAnimator.Play("Grounded");

            if (emote.StartFromSit && IsCurrentClip(body.armsAnimator, "ArmsSit"))
                body.armsAnimator.Play("Grounded");

            if (IsCurrentClip(body.bodyAnimator, emote.BodyState))
                body.bodyAnimator.Play("Grounded");

            if (IsCurrentClip(body.armsAnimator, emote.ArmsState))
                body.armsAnimator.Play("Grounded");

            if (emote.Id == "sit")
                body.idleTime = 0f;
        }

        private static void PlaySitPrep(Body body)
        {
            if (body == null)
                return;

            if (body.bodyAnimator != null)
            {
                body.bodyAnimator.speed = 1f;
                body.bodyAnimator.Play("ExperimentSit");
            }

            if (body.armsAnimator != null)
            {
                body.armsAnimator.speed = 1f;
                body.armsAnimator.Play("ArmsSit");
            }

            body.idleTime = Mathf.Max(body.idleTime, 13f);
        }

        private static void KeepSitPrepActive(Body body)
        {
            if (body == null)
                return;

            if (!IsCurrentClip(body.bodyAnimator, "ExperimentSit") ||
                !IsCurrentClip(body.armsAnimator, "ArmsSit"))
            {
                PlaySitPrep(body);
                return;
            }

            body.idleTime = Mathf.Max(body.idleTime, 13f);
        }

        private static void PlayAnimatorState(Animator animator, string stateName, float normalizedTime)
        {
            if (animator == null || string.IsNullOrWhiteSpace(stateName))
                return;

            if (float.IsNegativeInfinity(normalizedTime))
                animator.Play(stateName);
            else
                animator.Play(stateName, 0, normalizedTime);

            animator.Update(0f);
        }

        private static void FreezeAnimators(Body body, EmoteDefinition emote)
        {
            if (body == null || emote == null)
                return;

            if (body.bodyAnimator != null)
            {
                if (IsCurrentClip(body.bodyAnimator, emote.BodyState))
                    HoldAnimatorPose(body.bodyAnimator, emote.BodyState, emote.HoldNormalizedTime);
            }

            if (body.armsAnimator != null)
            {
                if (IsCurrentClip(body.armsAnimator, emote.ArmsState))
                    HoldAnimatorPose(body.armsAnimator, emote.ArmsState, emote.HoldNormalizedTime);
            }
        }

        private static void HoldAnimatorPose(Animator animator, string stateName, float normalizedTime)
        {
            if (animator == null || string.IsNullOrWhiteSpace(stateName))
                return;

            if (normalizedTime >= 0f)
            {
                animator.Play(stateName, 0, normalizedTime);
                animator.Update(0f);
            }

            animator.speed = 0f;
        }

        private static bool CanPlayEmote(Body body)
        {
            return body != null
                && body.alive
                && body.conscious
                && !body.sleeping
                && body.standing
                && body.currentClimbable == null
                && body.attackCooldown <= 0f;
        }

        private static bool ShouldCancelEmote(Body body)
        {
            if (!CanPlayEmote(body))
                return true;

            if (body.moveDir.sqrMagnitude > 0.01f)
                return true;

            return body.rb != null && body.rb.velocity.sqrMagnitude > 1.0f;
        }

        private static bool TryGetBody(knetid clientId, out Body body)
        {
            body = null;

            ScavPlayer player;
            return ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out player, out body)
                && body != null;
        }

        private static bool IsCurrentClip(Animator animator, string clipName)
        {
            if (animator == null || string.IsNullOrWhiteSpace(clipName))
                return false;

            AnimatorClipInfo[] clipInfo = animator.GetCurrentAnimatorClipInfo(0);
            return clipInfo != null
                && clipInfo.Length > 0
                && clipInfo[0].clip != null
                && clipInfo[0].clip.name == clipName;
        }
    }
}
