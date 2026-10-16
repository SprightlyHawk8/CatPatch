using CatPatchspace;
using Together;
using System.Collections.Generic;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class HugVisualController
    {
        private Body _bodyA;
        private Body _bodyB;
        private double _endsAt;

        public void Start(knetid clientA, knetid clientB, float durationSeconds)
        {
            ScavPlayer playerA;
            Body bodyA;
            ScavPlayer playerB;
            Body bodyB;
            if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientA, out playerA, out bodyA))
                return;

            if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientB, out playerB, out bodyB))
                return;

            _bodyA = bodyA;
            _bodyB = bodyB;
            _endsAt = Time.realtimeSinceStartupAsDouble + Mathf.Max(0.25f, durationSeconds);
        }

        public void Stop()
        {
            _bodyA = null;
            _bodyB = null;
            _endsAt = 0.0;
        }

        public void UpdatePose()
        {
            if (!IsActive())
            {
                Stop();
                return;
            }

            ApplyHugPose(_bodyA, _bodyB);
        }

        public void UpdateArms()
        {
            if (IsActive())
                ApplyHugArmVisuals(_bodyA, _bodyB);
        }

        private bool IsActive()
        {
            return _bodyA != null
                && _bodyB != null
                && Time.realtimeSinceStartupAsDouble < _endsAt
                && HugValidation.AreBodiesWithinDistance(_bodyA, _bodyB, HugSettings.MaxVisualHugDistance);
        }

        private static void ApplyHugPose(Body bodyA, Body bodyB)
        {
            if (bodyA == null || bodyB == null)
                return;

            Vector2 posA = bodyA.transform.position;
            Vector2 posB = bodyB.transform.position;
            Vector2 direction = posB - posA;

            if (direction.sqrMagnitude < 0.001f)
                direction = Vector2.right;

            Vector2 normal = direction.normalized;
            Vector2 center = (posA + posB) * 0.5f;
            Vector2 targetA = center - normal * 0.42f;
            Vector2 targetB = center + normal * 0.42f;

            targetA.y = posA.y;
            targetB.y = posB.y;

            bodyA.SetVelocity((targetA - posA) * 7.5f);
            bodyB.SetVelocity((targetB - posB) * 7.5f);
            bodyA.eyeCloseTime = Mathf.Max(bodyA.eyeCloseTime, 0.2f);
            bodyB.eyeCloseTime = Mathf.Max(bodyB.eyeCloseTime, 0.2f);

            ForceLookAt(bodyA, bodyB);
            ForceLookAt(bodyB, bodyA);
        }

        private static void ApplyHugArmVisuals(Body bodyA, Body bodyB)
        {
            if (bodyA == null || bodyB == null)
                return;

            Vector2 direction = bodyB.transform.position - bodyA.transform.position;
            if (direction.sqrMagnitude < 0.001f)
                direction = Vector2.right;

            Vector2 normal = direction.normalized;
            Vector2 perpendicular = new Vector2(-normal.y, normal.x);
            Vector2 bodyATarget = (Vector2)bodyB.transform.position - normal * 0.18f;
            Vector2 bodyBTarget = (Vector2)bodyA.transform.position + normal * 0.18f;

            ApplyArmPair(bodyA, bodyATarget + perpendicular * 0.22f, bodyATarget - perpendicular * 0.22f);
            ApplyArmPair(bodyB, bodyBTarget - perpendicular * 0.22f, bodyBTarget + perpendicular * 0.22f);
        }

        private static void ForceLookAt(Body source, Body target)
        {
            if (source == null || target == null)
                return;

            Vector3 targetPosition = target.transform.position;
            source.targetLookPos = new Vector2(targetPosition.x, targetPosition.y + 1f);
        }

        private static void ApplyArmPair(Body body, Vector2 upperTarget, Vector2 lowerTarget)
        {
            if (body == null || body.limbs == null || body.limbs.Length == 0)
                return;

            Limb leftForearm;
            Limb leftHand;
            Limb rightForearm;
            Limb rightHand;
            ResolveArmTargets(body, out leftForearm, out leftHand, out rightForearm, out rightHand);

            ApplyArmLimbOverride(leftForearm, upperTarget);
            ApplyArmLimbOverride(leftHand, Vector2.Lerp(leftForearm != null ? (Vector2)leftForearm.transform.position : upperTarget, upperTarget, 0.8f));
            ApplyArmLimbOverride(rightForearm, lowerTarget);
            ApplyArmLimbOverride(rightHand, Vector2.Lerp(rightForearm != null ? (Vector2)rightForearm.transform.position : lowerTarget, lowerTarget, 0.8f));
        }

        private static void ResolveArmTargets(Body body, out Limb leftForearm, out Limb leftHand, out Limb rightForearm, out Limb rightHand)
        {
            leftForearm = GetValidLimb(body, 4);
            leftHand = GetValidLimb(body, 5);
            rightForearm = GetValidLimb(body, 7);
            rightHand = GetValidLimb(body, 8);

            if (leftForearm != null && leftHand != null && rightForearm != null && rightHand != null)
                return;

            List<Limb> arms = new List<Limb>();
            foreach (Limb limb in body.limbs)
            {
                if (limb != null && limb.isArm && !limb.dismembered)
                    arms.Add(limb);
            }

            if (leftForearm == null && arms.Count > 0) leftForearm = arms[0];
            if (leftHand == null && arms.Count > 1) leftHand = arms[1];
            if (rightForearm == null && arms.Count > 2) rightForearm = arms[2];
            if (rightHand == null && arms.Count > 3) rightHand = arms[3];
        }

        private static Limb GetValidLimb(Body body, int index)
        {
            if (body == null || body.limbs == null || index < 0 || index >= body.limbs.Length)
                return null;

            Limb limb = body.limbs[index];
            if (limb == null || limb.dismembered)
                return null;

            return limb;
        }

        private static void ApplyArmLimbOverride(Limb limb, Vector2 targetPosition)
        {
            if (limb == null || limb.transform == null)
                return;

            Vector2 newPosition = Vector2.Lerp(
                limb.transform.position,
                targetPosition,
                0.82f);

            limb.transform.position = new Vector3(
                newPosition.x,
                newPosition.y,
                limb.transform.position.z);
        }
    }
}