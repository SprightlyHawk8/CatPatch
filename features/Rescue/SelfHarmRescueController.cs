using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CatPatchspace
{
    internal sealed class SelfHarmRescueController : MonoBehaviour
    {
        private const double RegistrationVerifyIntervalSeconds = 2.0;
        private const double RescueWindowSeconds = 24.0;
        private const float RescueDistance = 9.0f;
        private const float FlashIntervalSeconds = 0.28f;
        private const float SwipeTimeoutSeconds = 2.2f;
        private const float SwipeMaxDurationSeconds = 0.31f;
        private const float SwipeMinDistance = 170.0f;
        private const float SwipeHorizontalRatio = 1.6f;
        private const double ActiveInterventionWindowSeconds = SwipeTimeoutSeconds + 20.0;
        private const double TreatmentAcceptanceSeconds = 120.0;
        private const double ObserverHandSendIntervalSeconds = 0.06;
        private const byte HarmTypeSuicide = 0;
        private const byte HarmTypeEye = 1;
        private const byte HarmTypeSelfHarm = 2;
        private const string FailedRescueLine = "GET THE FUCK AWAY FROM ME!!!";

        private static readonly string[] RescueVoiceLines =
        {
            "Oh no, you're not leaving me yet.",  // -- pretty sure "not giving up on me" means YOU're the one getting given up on ... unsure
            "Stay with me. We're getting through this.",
            "Not today. I've got you.",
            "Hey, look at me. You're still here.",
            "Nope. You're not in this alone.", // -- "youre not doing this alone" can be read as "im cutting you too" ngl

            // some more 

            "You're okay. You're fine.",
            "Don't! It's- It's okay!",
            "It'll be alright. Breathe.",
            "Take a deep breath, okay?",
            "You'll be okay. Look at me.",
            "Not on my watch.",
            "I've got you. Come here.",
            "Stay with me, okay?",
            "You're still here. I'm here.",
            "I'm here for you, buddy.",
            "You're stuck with me for now.",
            "Okay, okay... let's sit down, alright?"
        };

        private static readonly FieldInfo SelfHarmerCooldownField =
            AccessTools.Field(typeof(SelfHarmer), "cooldownTime");

        private static readonly MethodInfo SelfHarmerClawAnimMethod =
            AccessTools.Method(typeof(SelfHarmer), "ClawAnim");

        private static readonly MethodInfo SelfHarmerWaitForMinigameMethod =
            AccessTools.Method(typeof(SelfHarmer), "WaitForMinigame", new[] { typeof(int), typeof(bool) });

        private static readonly MethodInfo MedicalSyncQueueHealthMethod =
            AccessTools.Method(
                AccessTools.TypeByName("Together.MedicalSync"),
                "Server_QueueSendCharacterHealth");

        private static SelfHarmRescueController _instance;
        private static bool _handlersRegistered;
        private static double _nextRegistrationVerifyAt;

        private readonly Dictionary<knetid, RescueState> _activeStates = new Dictionary<knetid, RescueState>();
        private readonly Dictionary<knetid, LocalPendingHarm> _localPendingByClientId = new Dictionary<knetid, LocalPendingHarm>();
        private readonly Dictionary<knetid, double> _acceptTreatmentUntilByClientId = new Dictionary<knetid, double>();
        private readonly Dictionary<knetid, double> _suppressTalkUntilByClientId = new Dictionary<knetid, double>();
        private readonly Dictionary<SpriteRenderer, Color> _originalSpriteColors = new Dictionary<SpriteRenderer, Color>();

        private RescueSwipeMinigame _activeMinigame;
        private RescueObserverMinigame _observerMinigame;
        private GunSuicideAttempt _activeGunSuicide;
        private double _nextObserverHandSendAt;

        private void Awake()
        {
            _instance = this;
            Plugin.Logger.LogInfo("[SelfHarmRescue] controller initialized; AttemptHarm routes through rescue tracking.");
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;

            if (_activeGunSuicide != null && _activeGunSuicide.Body != null)
                _activeGunSuicide.Body.forceWalk = false;

            RestoreAllSpriteColors();
        }

        private void Update()
        {
            RegisterHandlersOnce();
            CleanupExpiredStates();

            if (_activeMinigame != null && (MinigameBase.main == null || MinigameBase.main.currentMinigame != _activeMinigame))
                _activeMinigame = null;

            if (_observerMinigame != null && (MinigameBase.main == null || MinigameBase.main.currentMinigame != _observerMinigame))
                _observerMinigame = null;
        }

        private void LateUpdate()
        {
            UpdateBodyFlash();
        }

        internal static IEnumerator RunSelfHarmCoroutine(SelfHarmer harmer)
        {
            if (_instance == null)
            {
                yield break;
            }

            Body body = harmer != null ? harmer.GetComponent<Body>() : null;
            if (body == null)
            {
                if (harmer != null)
                    harmer.harming = false;
                yield break;
            }

            knetid clientId;
            LocalPendingHarm pending = null;
            if (TryGetBodyClientId(body, out clientId) && !CatPatchNetId.IsNone(clientId))
            {
                pending = _instance.BeginLocalPendingHarm(clientId, body, harmer, HarmTypeSelfHarm);
            }
            else if (KrokoshaScavMultiplayer.network_system_is_running)
            {
                Plugin.Logger.LogWarning("[SelfHarmRescue] local self-harm began without a player ID; rescue state could not start.");
            }

            if (!StartSelfHarmerMinigame(harmer, 1, false) && MinigameBase.main != null)
                MinigameBase.main.StartMinigame(new SelfHarmMinigame(1, false), null);

            while ((MinigameBase.main != null && MinigameBase.main.currentMinigame != null)
                || (pending != null && pending.InterventionActive))
            {
                if (pending != null && Time.realtimeSinceStartupAsDouble >= pending.ExpiresAt)
                {
                    pending.InterventionActive = false;
                    pending.ForceImmediateHarm = true;
                }

                if (pending != null && pending.ForceImmediateHarm)
                {
                    MinigameBase.main.EndMinigame();
                    break;
                }

                yield return null;
            }

            bool rescued = pending != null && pending.Rescued;
            _instance.EndLocalPendingHarm(clientId);

            if (rescued)
            {
                ApplySuccessfulRescue(harmer, body, pending);
                yield break;
            }

            PrepareFailedRescueOutcome(clientId);
            if (pending == null || !pending.FailureDamageAppliedByServer)
                ApplySelfHarmDamage(body, false);
            SetSelfHarmerCooldown(harmer, 180.0f);
            harmer.harming = false;
            TryInvokeClawAnim(harmer);
        }

        internal static IEnumerator SkipRemoteSelfHarmCoroutine()
        {
            yield break;
        }

        private static void PrepareFailedRescueOutcome(knetid clientId)
        {
            if (_instance == null)
                return;

            _instance._suppressTalkUntilByClientId.Remove(clientId);
        }

        internal static IEnumerator RunSuicideCoroutine(SelfHarmer harmer)
        {
            if (_instance == null)
            {
                yield break;
            }

            Body body = harmer != null ? harmer.GetComponent<Body>() : null;
            if (body == null)
            {
                if (harmer != null)
                    harmer.harming = false;
                yield break;
            }

            Item gun;
            if (body.FindByTagThorough("gun", out gun))
            {
                yield return RunVanillaGunSuicideBranch(harmer, body, gun);
                yield break;
            }

            bool isEye = body.totalHappiness <= -99.9f && !body.bothEyesGone && UnityEngine.Random.value < 0.5f;
            knetid clientId;
            LocalPendingHarm pending = null;
            if (TryGetBodyClientId(body, out clientId) && !CatPatchNetId.IsNone(clientId))
            {
                pending = _instance.BeginLocalPendingHarm(
                    clientId,
                    body,
                    harmer,
                    isEye ? HarmTypeEye : HarmTypeSuicide);
            }
            else if (KrokoshaScavMultiplayer.network_system_is_running)
            {
                Plugin.Logger.LogWarning("[SelfHarmRescue] local suicide began without a player ID; rescue state could not start.");
            }

            if (!StartSelfHarmerMinigame(harmer, isEye ? 1 : 5, isEye) && MinigameBase.main != null)
                MinigameBase.main.StartMinigame(new SelfHarmMinigame(isEye ? 1 : 5, isEye), null);

            while ((MinigameBase.main != null && MinigameBase.main.currentMinigame != null)
                || (pending != null && pending.InterventionActive))
            {
                if (pending != null && Time.realtimeSinceStartupAsDouble >= pending.ExpiresAt)
                {
                    pending.InterventionActive = false;
                    pending.ForceImmediateHarm = true;
                }

                if (pending != null && pending.ForceImmediateHarm)
                {
                    MinigameBase.main.EndMinigame();
                    break;
                }

                yield return null;
            }

            bool rescued = pending != null && pending.Rescued;
            _instance.EndLocalPendingHarm(clientId);

            if (rescued)
            {
                ApplySuccessfulRescue(harmer, body, pending);
                yield break;
            }

            PrepareFailedRescueOutcome(clientId);
            if (pending == null || !pending.FailureDamageAppliedByServer)
                ApplySuicideDamage(body, isEye, false);
            SetSelfHarmerCooldown(harmer, 30.0f);
            harmer.harming = false;
            TryInvokeClawAnim(harmer);
        }

        internal static bool TryStartRescueForBody(Body body)
        {
            if (_instance == null || body == null)
                return false;

            return _instance.TryStartRescueMinigame(body);
        }

        private static bool StartSelfHarmerMinigame(SelfHarmer harmer, int cuts, bool isEye)
        {
            if (harmer == null || MinigameBase.main == null || SelfHarmerWaitForMinigameMethod == null)
                return false;

            try
            {
                IEnumerator routine = SelfHarmerWaitForMinigameMethod.Invoke(
                    harmer,
                    new object[] { cuts, isEye }) as IEnumerator;
                return routine != null && routine.MoveNext();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("SelfHarmRescue: base WaitForMinigame invocation failed: " + ex.Message);
                return false;
            }
        }

        private LocalPendingHarm BeginLocalPendingHarm(knetid clientId, Body body, SelfHarmer harmer, byte harmType)
        {
            LocalPendingHarm pending = new LocalPendingHarm
            {
                ClientId = clientId,
                Body = body,
                Harmer = harmer,
                HarmType = harmType,
                ExpiresAt = Time.realtimeSinceStartupAsDouble + RescueWindowSeconds
            };

            _localPendingByClientId[clientId] = pending;
            SetStateLocal(clientId, true);
            Plugin.Logger.LogInfo("[SelfHarmRescue] local attempt started target=" + CatPatchNetId.ToDisplay(clientId)
                + " type=" + harmType);
            SendStartToServer(clientId, harmType);
            return pending;
        }

        private void EndLocalPendingHarm(knetid clientId)
        {
            if (!CatPatchNetId.IsNone(clientId))
                _localPendingByClientId.Remove(clientId);

            SetStateLocal(clientId, false);
            SendEndToServer(clientId);
        }

        private bool TryStartRescueMinigame(Body targetBody)
        {
            if (!KrokoshaScavMultiplayer.network_system_is_running)
            {
                return false;
            }

            if (UIInGame.SpectatorModeActive)
            {
                return false;
            }

            if (MinigameBase.main == null || MinigameBase.main.currentMinigame != null)
            {
                return false;
            }

            knetid targetClientId;
            if (!TryGetBodyClientId(targetBody, out targetClientId) || CatPatchNetId.IsNone(targetClientId))
            {
                return false;
            }

            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null
                || localPlayer.body == null
                || !localPlayer.body.alive
                || !localPlayer.body.conscious
                || localPlayer.playerId == targetClientId)
            {
                return false;
            }

            if (!IsStateActive(targetClientId))
            {
                return false;
            }

            ExtendRescueStateWindow(targetClientId, ActiveInterventionWindowSeconds);
            SendInterventionToServer(targetClientId);
            _activeMinigame = new RescueSwipeMinigame(this, targetClientId, SafePlayerName(targetClientId));
            StartCatPatchMinigame(_activeMinigame);
            Plugin.Logger.LogInfo("[SelfHarmRescue] rescuer minigame started target=" + CatPatchNetId.ToDisplay(targetClientId));
            return true;
        }

        private void OnRescueMinigameSucceeded(knetid targetClientId)
        {
            _activeMinigame = null;
            SendAttemptToServer(targetClientId, true);
        }

        private void OnRescueMinigameFailed(knetid targetClientId, string reason)
        {
            _activeMinigame = null;
            SendAttemptToServer(targetClientId, false);

            if (!string.IsNullOrWhiteSpace(reason))
                Util.DoAlert(reason, false);
        }

        private void SendStartToServer(knetid targetClientId, byte harmType)
        {
            if (!KrokoshaScavMultiplayer.network_system_is_running)
                return;

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerStart(targetClientId, harmType);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueStart);
            writer.Put(targetClientId);
            writer.Put(harmType);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        private void SendEndToServer(knetid targetClientId)
        {
            if (!KrokoshaScavMultiplayer.network_system_is_running || CatPatchNetId.IsNone(targetClientId))
                return;

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerEnd(targetClientId);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueEnd);
            writer.Put(targetClientId);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        private void SendAttemptToServer(knetid targetClientId, bool success)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null)
                return;

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerAttempt(localPlayer.playerId, targetClientId, success);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueAttempt);
            writer.Put(targetClientId);
            writer.Put(success);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        private void SendInterventionToServer(knetid targetClientId)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null)
                return;

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerIntervention(localPlayer.playerId, targetClientId);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueIntervene);
            writer.Put(targetClientId);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        private void SendObserverHandState(knetid targetClientId, Vector2 handPosition, Vector2 handVelocity, bool clicking)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextObserverHandSendAt)
                return;

            _nextObserverHandSendAt = now + ObserverHandSendIntervalSeconds;

            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer == null)
                return;

            if (KrokoshaScavMultiplayer.is_server)
            {
                HandleServerObserverHandState(localPlayer.playerId, targetClientId, handPosition, handVelocity, clicking);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueHandState);
            writer.Put(targetClientId);
            writer.Put(handPosition.x);
            writer.Put(handPosition.y);
            writer.Put(handVelocity.x);
            writer.Put(handVelocity.y);
            writer.Put(clicking);
            Net.Client_Send(DeliveryMethod.Unreliable, writer);
        }

        private void HandleServerObserverHandState(
            knetid helperClientId,
            knetid targetClientId,
            Vector2 handPosition,
            Vector2 handVelocity,
            bool clicking)
        {
            RescueState state;
            if (!_activeStates.TryGetValue(targetClientId, out state)
                || state == null
                || state.HelperClientId != helperClientId
                || !IsStateActive(targetClientId))
                return;

            handPosition.x = Mathf.Clamp(handPosition.x, -524.0f, 524.0f);
            handPosition.y = Mathf.Clamp(handPosition.y, -524.0f, 524.0f);
            handVelocity = Vector2.ClampMagnitude(handVelocity, 100.0f);

            if (IsLocalClient(targetClientId))
            {
                UpdateObserverHandState(targetClientId, handPosition, handVelocity, clicking);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueHandState);
            writer.Put(targetClientId);
            writer.Put(handPosition.x);
            writer.Put(handPosition.y);
            writer.Put(handVelocity.x);
            writer.Put(handVelocity.y);
            writer.Put(clicking);
            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, new List<knetid> { targetClientId });
        }

        private void UpdateObserverHandState(knetid targetClientId, Vector2 handPosition, Vector2 handVelocity, bool clicking)
        {
            if (_observerMinigame != null && _observerMinigame.TargetClientId == targetClientId)
                _observerMinigame.SetRemoteHandState(handPosition, handVelocity, clicking);
        }

        private void HandleServerIntervention(knetid helperClientId, knetid targetClientId)
        {
            if (helperClientId == targetClientId || !IsStateActive(targetClientId))
                return;

            Body helperBody;
            if (!TryGetBody(helperClientId, out helperBody)
                || helperBody == null
                || !helperBody.alive
                || !helperBody.conscious)
            {
                SendAlert(helperClientId, "Cannot rescue while dead or unconscious.");
                return;
            }

            RescueState state;
            if (!_activeStates.TryGetValue(targetClientId, out state) || state == null)
                return;

            if (!CatPatchNetId.IsNone(state.HelperClientId) && state.HelperClientId != helperClientId)
            {
                SendAlert(helperClientId, "Someone else is already helping that player.");
                return;
            }

            state.HelperClientId = helperClientId;
            state.ExpiresAt = Math.Max(
                state.ExpiresAt,
                Time.realtimeSinceStartupAsDouble + ActiveInterventionWindowSeconds);

            if (IsLocalClient(targetClientId))
            {
                BeginLocalIntervention(targetClientId, helperClientId);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueObserveStart);
            writer.Put(targetClientId);
            writer.Put(helperClientId);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, new List<knetid> { targetClientId });
        }

        private void BeginLocalIntervention(knetid targetClientId, knetid helperClientId)
        {
            LocalPendingHarm pending;
            if (!_localPendingByClientId.TryGetValue(targetClientId, out pending) || pending == null || pending.Body == null)
                return;

            pending.InterventionActive = true;
            pending.ExpiresAt = Math.Max(
                pending.ExpiresAt,
                Time.realtimeSinceStartupAsDouble + ActiveInterventionWindowSeconds);
            ExtendRescueStateWindow(targetClientId, ActiveInterventionWindowSeconds);
            _suppressTalkUntilByClientId[targetClientId] = Time.realtimeSinceStartupAsDouble + RescueWindowSeconds;

            if (MinigameBase.main != null && MinigameBase.main.currentMinigame is SelfHarmMinigame)
            {
                // The MP EndMinigame patch interprets a SelfHarmMinigame close as a
                // completed attempt. Swap the type first so rescue cancellation only
                // closes the UI and never emits the MP damage packet (10076).
                MinigameBase.main.currentMinigame = new CancelledSelfHarmMinigame();
                MinigameBase.main.EndMinigame();
            }

            if (MinigameBase.main == null || MinigameBase.main.currentMinigame != null)
                return;

            _observerMinigame = new RescueObserverMinigame(targetClientId, SafePlayerName(helperClientId));
            StartCatPatchMinigame(_observerMinigame);
        }

        private void HandleServerStart(knetid targetClientId, byte harmType)
        {
            Body body;
            if (!TryGetBody(targetClientId, out body) || body == null || !body.alive)
            {
                Plugin.Logger.LogWarning("[SelfHarmRescue] start rejected: target body unavailable or dead, client="
                    + CatPatchNetId.ToDisplay(targetClientId));
                return;
            }

            SelfHarmer harmer = body.GetComponent<SelfHarmer>();
            if (harmer == null)
            {
                Plugin.Logger.LogWarning("[SelfHarmRescue] start rejected: SelfHarmer missing, client="
                    + CatPatchNetId.ToDisplay(targetClientId));
                return;
            }

            // The owner starts SelfHarmer locally. Its `harming` field is not reliably
            // replicated to the host before this packet arrives, so sender ownership is
            // the authority for remote starts (validated in OnServerStartReceived).
            SetStateLocal(targetClientId, true);
            RescueState state;
            if (_activeStates.TryGetValue(targetClientId, out state) && state != null)
                state.HarmType = NormalizeHarmType(harmType);
            Plugin.Logger.LogInfo("[SelfHarmRescue] server state started target="
                + CatPatchNetId.ToDisplay(targetClientId) + " type=" + harmType);
            BroadcastState(targetClientId, true);
        }

        private void HandleServerEnd(knetid targetClientId)
        {
            SetStateLocal(targetClientId, false);
            BroadcastState(targetClientId, false);
        }

        private void HandleServerAttempt(knetid helperClientId, knetid targetClientId, bool success)
        {
            RescueState state;
            if (!IsStateActive(targetClientId)
                || !_activeStates.TryGetValue(targetClientId, out state)
                || state == null)
            {
                SendAlert(helperClientId, "That rescue window has closed.");
                return;
            }

            if (state.HelperClientId != helperClientId)
            {
                SendAlert(helperClientId, "That rescue attempt was not accepted by the host.");
                return;
            }

            Body helperBody;
            Body targetBody;
            if (!TryGetBody(helperClientId, out helperBody) || !TryGetBody(targetClientId, out targetBody))
            {
                SendAlert(helperClientId, "Rescue target is no longer valid.");
                return;
            }

            if (helperClientId == targetClientId || helperBody == null || targetBody == null || !helperBody.alive || !helperBody.conscious || !targetBody.alive)
            {
                SendAlert(helperClientId, "Cannot rescue right now.");
                return;
            }

            if (!Util.DoFullInteractionCheck(helperBody, targetBody, false, RescueDistance, true))
            {
                SendAlert(helperClientId, "Need close range and clear line of sight.");
                return;
            }

            if (success)
                _suppressTalkUntilByClientId[targetClientId] = Time.realtimeSinceStartupAsDouble + 4.0;
            else
                ApplyAuthoritativeFailedHarm(state.HarmType, targetBody);

            SendResultToTarget(targetClientId, success);
            SendAlert(helperClientId, success ? "You stopped the self-harm." : "Rescue failed.");
            if (success && helperBody.talker != null)
                helperBody.talker.Talk(ChooseRescueLine(), null, true, false);

            if (success)
            {
                GrantTreatmentAcceptance(targetClientId, true);
                StartCoroutine(SpeakRescuedTargetFromServerAfterDelay(targetClientId));
                SetStateLocal(targetClientId, false);
                BroadcastState(targetClientId, false);
            }
            else
            {
                ApplyFailedRescueRetaliation(helperClientId, targetClientId, helperBody, targetBody);
                StartCoroutine(SpeakFailedTargetFromServerAfterDelay(targetClientId));
            }
        }

        private void ApplyFailedRescueRetaliation(knetid helperClientId, knetid targetClientId, Body helperBody, Body targetBody)
        {
            if (helperBody == null || targetBody == null)
                return;

            Limb scratchedLimb = null;
            int scratchedLimbIndex = -1;
            if (helperBody.limbs != null && helperBody.limbs.Length > 0)
            {
                for (int attempt = 0; attempt < 32 && (scratchedLimb == null || scratchedLimb.dismembered); attempt++)
                {
                    scratchedLimbIndex = UnityEngine.Random.Range(0, helperBody.limbs.Length);
                    scratchedLimb = helperBody.limbs[scratchedLimbIndex];
                }
            }

            if (scratchedLimb != null && !scratchedLimb.dismembered)
            {
                scratchedLimb.skinHealth -= 20.0f;
                scratchedLimb.muscleHealth -= 5.0f;
                scratchedLimb.bleedAmount += UnityEngine.Random.Range(3.0f, 6.0f);
                scratchedLimb.pain += UnityEngine.Random.Range(10.0f, 18.0f);

                AddScratchWoundVisual(scratchedLimb);
            }

            ApplyRetaliationMotion(helperBody, targetBody);
            Sound.Play("cut1", helperBody.transform.position, false, true, null, 1.0f, 1.0f, false, false);

            BroadcastRetaliationPresentation(helperClientId, targetClientId, scratchedLimbIndex);

            ScavPlayer helperPlayer;
            if (helperBody.TryGetNetPlayer(out helperPlayer) && helperPlayer != null)
            {
                helperPlayer.Server_RemindPlayersCurrentState(true, true);
                QueueServerHealthSync(helperPlayer);
            }

        }

        private static void AddScratchWoundVisual(Limb scratchedLimb)
        {
            if (scratchedLimb != null && WoundView.view != null)
                WoundView.view.AddImageToLimb(scratchedLimb, Resources.Load<Sprite>("Special/selfHarmWound"), false, l => l.skinHealth >= 95.0f);
        }

        private static void ApplyRetaliationMotion(Body helperBody, Body targetBody)
        {
            if (helperBody == null || targetBody == null)
                return;

            Vector2 away = (helperBody.transform.position - targetBody.transform.position).normalized;
            if (away.sqrMagnitude < 0.01f)
                away = Vector2.right;

            Vector2 currentVelocity = helperBody.rb != null ? helperBody.rb.velocity : Vector2.zero;
            helperBody.Ragdoll();
            helperBody.SetVelocity(currentVelocity * 0.2f + (away + Vector2.up * 0.2f).normalized * 12.0f);
        }

        private static void BroadcastRetaliationPresentation(knetid helperClientId, knetid targetClientId, int scratchedLimbIndex)
        {
            IReadOnlyList<knetid> destinations = GetClientIdsExceptHost();
            if (destinations == null || destinations.Count == 0)
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueRetaliation);
            writer.Put(helperClientId);
            writer.Put(targetClientId);
            writer.Put(scratchedLimbIndex >= 0 && scratchedLimbIndex < byte.MaxValue ? (byte)scratchedLimbIndex : byte.MaxValue);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, destinations);
        }

        private void ReceiveResultLocal(knetid targetClientId, bool rescued)
        {
            LocalPendingHarm pending;
            if (!_localPendingByClientId.TryGetValue(targetClientId, out pending) || pending == null)
                return;

            if (rescued)
            {
                pending.Rescued = true;
                _suppressTalkUntilByClientId[targetClientId] = Time.realtimeSinceStartupAsDouble + 4.0;
            }
            else
            {
                pending.ForceImmediateHarm = true;
                pending.FailureDamageAppliedByServer = true;
            }

            pending.InterventionActive = false;

            if (rescued)
                GrantTreatmentAcceptance(targetClientId, false);

            if (MinigameBase.main != null && MinigameBase.main.currentMinigame == _observerMinigame)
                MinigameBase.main.EndMinigame();

            _observerMinigame = null;
        }

        private IEnumerator SpeakRescuedTargetFromServerAfterDelay(knetid targetClientId)
        {
            yield return new WaitForSecondsRealtime(0.25f);

            if (!KrokoshaScavMultiplayer.is_server)
                yield break;

            Body body;
            if (!TryGetBody(targetClientId, out body) || body == null || body.talker == null)
                yield break;

            double suppressUntil;
            bool wasSuppressed = _suppressTalkUntilByClientId.TryGetValue(targetClientId, out suppressUntil);
            _suppressTalkUntilByClientId.Remove(targetClientId);
            body.talker.Talk("...", null, true, false);

            if (wasSuppressed && suppressUntil > Time.realtimeSinceStartupAsDouble)
                _suppressTalkUntilByClientId[targetClientId] = suppressUntil;
        }

        private IEnumerator SpeakFailedTargetFromServerAfterDelay(knetid targetClientId)
        {
            yield return new WaitForSecondsRealtime(0.1f);

            if (!KrokoshaScavMultiplayer.is_server)
                yield break;

            Body body;
            if (!TryGetBody(targetClientId, out body) || body == null || body.talker == null)
                yield break;

            _suppressTalkUntilByClientId.Remove(targetClientId);
            body.talker.Talk(FailedRescueLine, null, true, false);
        }

        private void SendResultToTarget(knetid targetClientId, bool rescued)
        {
            if (IsLocalClient(targetClientId))
            {
                ReceiveResultLocal(targetClientId, rescued);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueResult);
            writer.Put(targetClientId);
            writer.Put(rescued);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, new List<knetid> { targetClientId });
        }

        private void SendAlert(knetid destinationClientId, string message, string voiceLine = null)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (IsLocalClient(destinationClientId))
            {
                Util.DoAlert(message, false);
                SpeakAsLocalPlayer(voiceLine);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueAlert);
            writer.Put(message, true);
            writer.Put(voiceLine ?? string.Empty, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, new List<knetid> { destinationClientId });
        }

        private static void SpeakAsLocalPlayer(string voiceLine)
        {
            if (string.IsNullOrWhiteSpace(voiceLine))
                return;

            Body body = null;
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer != null)
                body = localPlayer.body;

            if (body == null && PlayerCamera.main != null)
                body = PlayerCamera.main.body;

            if (body != null && body.talker != null)
                body.talker.Talk(voiceLine, null, true, false);
        }

        internal static bool ShouldAllowTalk(Talker talker, object[] args)
        {
            if (_instance == null || talker == null)
                return true;

            Body speakerBody = talker.GetComponent<Body>();
            knetid speakerId;
            if (!TryGetBodyClientId(speakerBody, out speakerId))
                return true;

            double suppressUntil;
            if (!_instance._suppressTalkUntilByClientId.TryGetValue(speakerId, out suppressUntil))
                return true;

            if (Time.realtimeSinceStartupAsDouble >= suppressUntil)
            {
                _instance._suppressTalkUntilByClientId.Remove(speakerId);
                return true;
            }

            return false;
        }

        private void BroadcastState(knetid targetClientId, bool active)
        {
            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmRescueState);
            writer.Put(targetClientId);
            writer.Put(active);

            IReadOnlyList<knetid> destinationClientIds = GetClientIdsExceptHost();
            if (destinationClientIds == null || destinationClientIds.Count == 0)
                return;

            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, destinationClientIds);
        }

        private void GrantTreatmentAcceptance(knetid targetClientId, bool broadcast)
        {
            double until = Time.realtimeSinceStartupAsDouble + TreatmentAcceptanceSeconds;
            _acceptTreatmentUntilByClientId[targetClientId] = until;

            if (!broadcast || !KrokoshaScavMultiplayer.is_server)
                return;

            IReadOnlyList<knetid> destinationClientIds = GetClientIdsExceptHost();
            if (destinationClientIds == null || destinationClientIds.Count == 0)
                return;

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.SelfHarmTreatmentAccept);
            writer.Put(targetClientId);
            writer.Put((float)TreatmentAcceptanceSeconds);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, destinationClientIds);
        }

        internal static bool HasTreatmentAcceptanceOverride(Body body)
        {
            if (_instance == null || body == null)
                return false;

            knetid clientId;
            if (!TryGetBodyClientId(body, out clientId) || CatPatchNetId.IsNone(clientId))
                return false;

            double until;
            if (!_instance._acceptTreatmentUntilByClientId.TryGetValue(clientId, out until))
                return false;

            if (Time.realtimeSinceStartupAsDouble <= until)
                return true;

            _instance._acceptTreatmentUntilByClientId.Remove(clientId);
            return false;
        }

        private void SetStateLocal(knetid targetClientId, bool active)
        {
            if (CatPatchNetId.IsNone(targetClientId))
                return;

            if (active)
            {
                _activeStates[targetClientId] = new RescueState
                {
                    TargetClientId = targetClientId,
                    HelperClientId = CatPatchNetId.None,
                    ExpiresAt = Time.realtimeSinceStartupAsDouble + RescueWindowSeconds
                };
            }
            else
            {
                _activeStates.Remove(targetClientId);
                RestoreSpriteColorsForBody(targetClientId);

                if (_observerMinigame != null
                    && _observerMinigame.TargetClientId == targetClientId
                    && MinigameBase.main != null
                    && MinigameBase.main.currentMinigame == _observerMinigame)
                {
                    MinigameBase.main.EndMinigame();
                    _observerMinigame = null;
                }
            }
        }

        private bool IsStateActive(knetid targetClientId)
        {
            RescueState state;
            return _activeStates.TryGetValue(targetClientId, out state)
                && state != null
                && Time.realtimeSinceStartupAsDouble < state.ExpiresAt;
        }

        private void ExtendRescueStateWindow(knetid targetClientId, double seconds)
        {
            RescueState state;
            if (_activeStates.TryGetValue(targetClientId, out state) && state != null)
                state.ExpiresAt = Math.Max(state.ExpiresAt, Time.realtimeSinceStartupAsDouble + seconds);
        }

        private void CleanupExpiredStates()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            List<knetid> expired = null;

            foreach (KeyValuePair<knetid, RescueState> pair in _activeStates)
            {
                if (pair.Value == null || now >= pair.Value.ExpiresAt)
                {
                    if (expired == null)
                        expired = new List<knetid>();

                    expired.Add(pair.Key);
                }
            }

            if (expired == null)
                return;

            for (int i = 0; i < expired.Count; i++)
                SetStateLocal(expired[i], false);
        }

        private void UpdateBodyFlash()
        {
            double now = Time.realtimeSinceStartupAsDouble;

            foreach (KeyValuePair<knetid, RescueState> pair in _activeStates)
            {
                Body body;
                if (!TryGetBody(pair.Key, out body) || body == null)
                    continue;

                bool red = Mathf.PingPong((float)now, FlashIntervalSeconds) < FlashIntervalSeconds * 0.5f;
                SpriteRenderer[] renderers = body.GetComponentsInChildren<SpriteRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    SpriteRenderer renderer = renderers[i];
                    if (renderer == null)
                        continue;

                    if (!_originalSpriteColors.ContainsKey(renderer))
                        _originalSpriteColors[renderer] = renderer.color;

                    Color baseColor = _originalSpriteColors[renderer];
                    renderer.color = red ? Color.Lerp(baseColor, Color.red, 0.55f) : baseColor;
                }
            }
        }

        private void RestoreSpriteColorsForBody(knetid clientId)
        {
            Body body;
            if (!TryGetBody(clientId, out body) || body == null)
                return;

            SpriteRenderer[] renderers = body.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer renderer = renderers[i];
                Color color;
                if (renderer != null && _originalSpriteColors.TryGetValue(renderer, out color))
                {
                    renderer.color = color;
                    _originalSpriteColors.Remove(renderer);
                }
            }
        }

        private void RestoreAllSpriteColors()
        {
            foreach (KeyValuePair<SpriteRenderer, Color> pair in _originalSpriteColors)
            {
                if (pair.Key != null)
                    pair.Key.color = pair.Value;
            }

            _originalSpriteColors.Clear();
        }

        private static byte NormalizeHarmType(byte harmType)
        {
            return harmType <= HarmTypeSelfHarm ? harmType : HarmTypeSelfHarm;
        }

        private static void ApplyAuthoritativeFailedHarm(byte harmType, Body targetBody)
        {
            if (targetBody == null)
                return;

            byte normalized = NormalizeHarmType(harmType);
            if (normalized == HarmTypeSelfHarm)
                ApplySelfHarmDamage(targetBody, false);
            else
                ApplySuicideDamage(targetBody, normalized == HarmTypeEye, false);

            SelfHarmer harmer = targetBody.GetComponent<SelfHarmer>();
            if (harmer != null)
            {
                SetSelfHarmerCooldown(harmer, normalized == HarmTypeSelfHarm ? 180.0f : 30.0f);
                harmer.harming = false;
            }

        }

        private static void ApplySelfHarmDamage(Body body, bool playDialogue = true)
        {
            if (body == null || body.limbs == null || body.limbs.Length == 0)
                return;

            Limb limb = null;
            int guard = 0;
            while ((limb == null || limb.dismembered) && guard < 64)
            {
                int index = UnityEngine.Random.Range(1, body.limbs.Length);
                limb = body.limbs[index];
                guard++;
            }

            if (limb == null)
                limb = body.limbs[0];

            if (limb == null)
                return;

            limb.muscleHealth -= 13.0f;
            limb.skinHealth -= 35.0f;
            limb.bleedAmount += UnityEngine.Random.Range(6.0f, 10.0f);
            limb.pain += UnityEngine.Random.Range(20.0f, 30.0f);

            if (WoundView.view != null)
                WoundView.view.AddImageToLimb(limb, Resources.Load<Sprite>("Special/selfHarmWound"), false, l => l.skinHealth >= 95.0f);

            if (body.rb != null)
                body.rb.AddForce(UnityEngine.Random.insideUnitCircle * 500.0f, ForceMode2D.Impulse);

            if (playDialogue && body.talker != null)
                body.talker.Talk(Locale.GetCharacter("selfharm"), null, true, false);

            body.happiness += 4.0f;
            Sound.Play("selfharm", body.transform.position, false, true, null, 1.0f, 1.0f, false, false);
            Sound.Play("harmSting", Vector2.zero, true, false, null, 0.7f, 1.0f, false, false);

            ScavPlayer player;
            if (KrokoshaScavMultiplayer.IsNetworkActiveAndIsServer() && body.TryGetNetPlayer(out player) && player != null)
            {
                player.Server_RemindPlayersCurrentState(false, true);
                QueueServerHealthSync(player);
            }
        }

        private static IEnumerator RunVanillaGunSuicideBranch(SelfHarmer harmer, Body body, Item item)
        {
            if (harmer == null || body == null || item == null)
                yield break;

            Container container;
            if (item.transform.parent != null && item.transform.parent.TryGetComponent<Container>(out container))
                container.UnloadItem(item, null);

            body.DropItem(body.handSlot);
            body.DropItem(item);
            body.PickUpItem(item, body.handSlot, false);
            item.GetComponent<GunScript>().roundInChamber = GunScript.RoundInChamber.Round;
            body.armsAnimator.Play("ArmsGunSuicide");
            _instance.AnnounceLocalGunSuicidePresentation(body, true);
            SetSelfHarmerCooldown(harmer, 600.0f);
            harmer.harming = false;

            GunSuicideAttempt attempt = new GunSuicideAttempt
            {
                Harmer = harmer,
                Body = body,
                Gun = item
            };
            _instance._activeGunSuicide = attempt;

            body.forceWalk = true;
            float waitUntil = Time.time + 2.0f;
            while (Time.time < waitUntil && !attempt.CancelRequested)
                yield return null;

            if (attempt.CancelRequested)
            {
                CompletePushedGunCancellation(attempt);
                yield break;
            }

            if (body.HoldingItem(item) && item.GetComponent<GunScript>().racked)
                item.GetComponent<GunScript>().TryRack();

            body.eyeCloseTime = 2.0f;
            if (body.talker != null)
                body.talker.Talk("...", null, true, false);

            waitUntil = Time.time + 1.0f;
            while (Time.time < waitUntil && !attempt.CancelRequested)
                yield return null;

            if (attempt.CancelRequested)
            {
                CompletePushedGunCancellation(attempt);
                yield break;
            }

            bool dropped = body.GetItem(body.handSlot) != item;
            if (body.HoldingItem(item))
                item.GetComponent<GunScript>().Fire(true);

            Sound.Play("harmSting", Vector2.zero, true, false, null, 0.7f, 1.0f, false, false);
            body.forceWalk = false;
            _instance.AnnounceLocalGunSuicidePresentation(body, false);
            if (_instance != null && ReferenceEquals(_instance._activeGunSuicide, attempt))
                _instance._activeGunSuicide = null;

            if (dropped)
            {
                yield return new WaitForSeconds(1.0f);
                harmer.harming = true;
                yield return RunSuicideCoroutine(harmer);
            }
        }

        internal static void HandleAuthoritativePush(NetBody victim)
        {
            if (_instance == null
                || victim == null
                || victim.body == null
                || !KrokoshaScavMultiplayer.network_system_is_running
                || !KrokoshaScavMultiplayer.is_server)
                return;

            knetid victimClientId;
            if (!TryGetBodyClientId(victim.body, out victimClientId) || CatPatchNetId.IsNone(victimClientId))
                return;

            if (IsLocalClient(victimClientId))
            {
                _instance.RequestLocalGunSuicideCancellation(victimClientId);
                return;
            }

            NetDataWriter writer = CatPatchNetwork.CreateWriter(NetworkMessageIds.GunSuicidePushCancel);
            writer.Put(victimClientId);
            Net.Server_SendToClients(
                DeliveryMethod.ReliableOrdered,
                writer,
                new List<knetid> { victimClientId });
        }

        private void RequestLocalGunSuicideCancellation(knetid victimClientId)
        {
            GunSuicideAttempt attempt = _activeGunSuicide;
            if (attempt == null || attempt.Body == null || attempt.Gun == null)
                return;

            knetid activeClientId;
            if (!TryGetBodyClientId(attempt.Body, out activeClientId) || activeClientId != victimClientId)
                return;

            attempt.CancelRequested = true;
        }

        private static void CompletePushedGunCancellation(GunSuicideAttempt attempt)
        {
            if (attempt == null)
                return;

            Body body = attempt.Body;
            Item gun = attempt.Gun;
            SelfHarmer harmer = attempt.Harmer;

            if (body != null)
            {
                body.forceWalk = false;
                if (gun != null && body.HoldingItem(gun))
                    body.DropItem(gun);

                if (body.armsAnimator != null)
                    body.armsAnimator.Play("Grounded");
            }

            if (gun != null && gun.rb != null && gun.transform.parent == null)
            {
                gun.rb.velocity += new Vector2(
                    UnityEngine.Random.Range(-2.5f, 2.5f),
                    UnityEngine.Random.Range(3.0f, 5.0f));
                gun.rb.angularVelocity += UnityEngine.Random.Range(-240.0f, 240.0f);
            }

            if (harmer != null)
            {
                harmer.harming = false;
                harmer.timeWasStill = 0.0f;
                SetSelfHarmerCooldown(harmer, 60.0f);
            }

            if (_instance != null && ReferenceEquals(_instance._activeGunSuicide, attempt))
                _instance._activeGunSuicide = null;

            if (_instance != null && body != null)
                _instance.AnnounceLocalGunSuicidePresentation(body, false);

            Util.DoAlert("The gun was knocked away.", false);
        }

        private void AnnounceLocalGunSuicidePresentation(Body body, bool active)
        {
            if (body == null || !KrokoshaScavMultiplayer.network_system_is_running)
                return;

            knetid victimClientId;
            if (!TryGetBodyClientId(body, out victimClientId) || CatPatchNetId.IsNone(victimClientId))
                return;

            if (KrokoshaScavMultiplayer.is_server)
            {
                BroadcastGunSuicidePresentation(victimClientId, active, victimClientId);
                return;
            }

            ushort messageId = active
                ? NetworkMessageIds.GunSuicidePresentationStart
                : NetworkMessageIds.GunSuicidePresentationEnd;
            NetDataWriter writer = CatPatchNetwork.CreateWriter(messageId);
            writer.Put(victimClientId);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        private static void HandleServerGunSuicidePresentation(knetid senderClientId, bool active)
        {
            Body body;
            if (!TryGetBody(senderClientId, out body) || body == null || (active && !body.alive))
                return;

            ApplyGunSuicidePresentation(senderClientId, active);
            BroadcastGunSuicidePresentation(senderClientId, active, senderClientId);
        }

        private static void BroadcastGunSuicidePresentation(knetid victimClientId, bool active, knetid excludedClientId)
        {
            IReadOnlyList<knetid> allClients = GetClientIdsExceptHost();
            List<knetid> recipients = new List<knetid>();
            for (int i = 0; i < allClients.Count; i++)
            {
                if (allClients[i] != excludedClientId)
                    recipients.Add(allClients[i]);
            }

            if (recipients.Count == 0)
                return;

            ushort messageId = active
                ? NetworkMessageIds.GunSuicidePresentationStart
                : NetworkMessageIds.GunSuicidePresentationEnd;
            NetDataWriter writer = CatPatchNetwork.CreateWriter(messageId);
            writer.Put(victimClientId);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, recipients);
        }

        private static void ApplyGunSuicidePresentation(knetid victimClientId, bool active)
        {
            Body body;
            if (!TryGetBody(victimClientId, out body) || body == null)
                return;

            if (active)
            {
                body.forceWalk = true;
                if (body.armsAnimator != null)
                    body.armsAnimator.Play("ArmsGunSuicide");
                return;
            }

            body.forceWalk = false;
            if (body.armsAnimator == null)
                return;

            AnimatorClipInfo[] clips = body.armsAnimator.GetCurrentAnimatorClipInfo(0);
            if (clips.Length > 0 && clips[0].clip != null && clips[0].clip.name == "ArmsGunSuicide")
                body.armsAnimator.Play("Grounded");
        }

        private static void ApplySuicideDamage(Body body, bool isEye, bool playDialogue = true)
        {
            if (body == null || body.limbs == null || body.limbs.Length == 0)
                return;

            Limb limb = null;
            if (isEye)
            {
                limb = body.limbs[0];
            }
            else if (body.limbs.Length > 7)
            {
                limb = UnityEngine.Random.Range(0.0f, 1.0f) < 0.5f ? body.limbs[4] : body.limbs[7];
            }

            if (limb == null || limb.dismembered)
                limb = body.limbs.Length > 1 ? body.limbs[1] : body.limbs[0];

            if (limb == null)
                return;

            limb.muscleHealth -= 30.0f;
            limb.skinHealth -= 70.0f;
            limb.bleedAmount += UnityEngine.Random.Range(45.0f, 60.0f);
            limb.pain += UnityEngine.Random.Range(25.0f, 35.0f);
            body.adrenaline += 50.0f;

            if (body.rb != null)
                body.rb.AddForce(UnityEngine.Random.insideUnitCircle * 700.0f, ForceMode2D.Impulse);

            if (playDialogue && !isEye && body.talker != null)
                body.talker.Talk(Locale.GetCharacter("suicide"), null, true, false);

            body.happiness += isEye ? 16.0f : 12.5f;
            Sound.Play("suicide", body.transform.position, false, true, null, 1.0f, 1.0f, false, false);

            if (MusicManager.main != null)
                MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/whathaveidone"));

            Sound.Play("harmSting", Vector2.zero, true, false, null, 1.0f, 0.7f, false, false);

            if (WoundView.view != null)
                WoundView.view.AddImageToLimb(limb, Resources.Load<Sprite>("Special/suicideWound"), false, l => l.skinHealth >= 95.0f);

            if (isEye)
                body.RemoveEye();

            ScavPlayer player;
            if (KrokoshaScavMultiplayer.IsNetworkActiveAndIsServer() && body.TryGetNetPlayer(out player) && player != null)
            {
                player.Server_RemindPlayersCurrentState(false, true);
                QueueServerHealthSync(player);
            }
        }

        private static void ApplySuccessfulRescue(SelfHarmer harmer, Body body, LocalPendingHarm pending)
        {
            if (harmer != null)
            {
                harmer.harming = false;
                harmer.timeWasStill = 0.0f;
                harmer.blackAmount = 0.0f;
                SetSelfHarmerCooldown(harmer, 240.0f);

                if (harmer.scarySource != null)
                {
                    harmer.scarySource.Stop();
                    harmer.scarySource.volume = 0.0f;
                }
            }

            if (body != null)
            {
                body.happiness = Mathf.Clamp(body.happiness + 8.0f, -100.0f, 100.0f);

                ScavPlayer player;
                if (KrokoshaScavMultiplayer.IsNetworkActiveAndIsServer() && body.TryGetNetPlayer(out player) && player != null)
                    player.Server_RemindPlayersCurrentState(false, true);
            }

            Util.DoAlert("Self-harm stopped.", false);
        }

        private static string ChooseRescueLine()
        {
            if (RescueVoiceLines.Length == 0)
                return "...";

            return RescueVoiceLines[UnityEngine.Random.Range(0, RescueVoiceLines.Length)];
        }

        private static void SetSelfHarmerCooldown(SelfHarmer harmer, float cooldown)
        {
            try
            {
                if (SelfHarmerCooldownField != null)
                    SelfHarmerCooldownField.SetValue(harmer, cooldown);
                else
                    harmer.cooldownTime = cooldown;
            }
            catch
            {
                try
                {
                    harmer.cooldownTime = cooldown;
                }
                catch
                {
                }
            }
        }

        private static void TryInvokeClawAnim(SelfHarmer harmer)
        {
            try
            {
                if (SelfHarmerClawAnimMethod != null)
                    SelfHarmerClawAnimMethod.Invoke(harmer, null);
            }
            catch
            {
            }
        }

        private static void QueueServerHealthSync(ScavPlayer player)
        {
            NetBody netBody;
            if (player == null || !player.TryGetNetBody(out netBody) || MedicalSyncQueueHealthMethod == null)
                return;

            try
            {
                MedicalSyncQueueHealthMethod.Invoke(null, new object[] { netBody, true });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("SelfHarmRescue: could not queue multiplayer health sync: " + ex.Message);
            }
        }

        private static bool TryGetBody(knetid clientId, out Body body)
        {
            body = null;
            ScavPlayer player;
            return ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out player, out body) && body != null;
        }

        private static bool TryGetBodyClientId(Body body, out knetid clientId)
        {
            clientId = CatPatchNetId.None;

            if (body == null)
                return false;

            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            if (localPlayer != null && localPlayer.body == body)
            {
                clientId = localPlayer.playerId;
                return true;
            }

            try
            {
                ScavPlayer player;
                if (body.TryGetNetPlayer(out player) && player != null)
                {
                    clientId = player.playerId;
                    return true;
                }
            }
            catch
            {
            }

            if (ScavPlayer.ClientIdToPlayerDict == null)
                return false;

            foreach (ScavPlayer player in ScavPlayer.ClientIdToPlayerDict.Values)
            {
                if (player == null || player.body != body)
                    continue;

                clientId = player.playerId;
                return true;
            }

            return false;
        }

        internal static bool IsOwnedByLocalPlayer(Body body)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            return body != null && localPlayer != null && localPlayer.body == body;
        }

        private static bool IsLocalClient(knetid clientId)
        {
            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            return localPlayer != null && localPlayer.playerId == clientId;
        }

        private static string SafePlayerName(knetid clientId)
        {
            try
            {
                ScavPlayer player = ScavPlayer.GetPlayerFromClientId(clientId);
                if (player != null && !string.IsNullOrWhiteSpace(player.playerName))
                    return player.playerName;
            }
            catch
            {
            }

            return "Player " + CatPatchNetId.ToDisplay(clientId);
        }

        private static bool StartCatPatchMinigame(Minigame minigame)
        {
            MinigameBase main = MinigameBase.main;
            if (main == null || minigame == null || main.currentMinigame != null)
                return false;

            main.itemImage.enabled = false;
            main.currentItem = null;
            main.guideText.text = string.Empty;
            main.currentMinigame = minigame;
            minigame.Start();
            main.UpdateHandSprite(true);
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

        private static void RegisterHandlersOnce()
        {
            if (_handlersRegistered)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (now < _nextRegistrationVerifyAt)
                    return;

                _nextRegistrationVerifyAt = now + RegistrationVerifyIntervalSeconds;
                if (CatPatchNetwork.IsRegistered(true, NetworkMessageIds.SelfHarmRescueStart)
                    && CatPatchNetwork.IsRegistered(true, NetworkMessageIds.SelfHarmRescueAttempt)
                    && CatPatchNetwork.IsRegistered(false, NetworkMessageIds.SelfHarmRescueState)
                    && CatPatchNetwork.IsRegistered(false, NetworkMessageIds.SelfHarmRescueResult)
                    && CatPatchNetwork.IsRegistered(false, NetworkMessageIds.SelfHarmTreatmentAccept)
                    && CatPatchNetwork.IsRegistered(true, NetworkMessageIds.SelfHarmRescueIntervene)
                    && CatPatchNetwork.IsRegistered(false, NetworkMessageIds.SelfHarmRescueRetaliation)
                    && CatPatchNetwork.IsRegistered(false, NetworkMessageIds.SelfHarmRescueObserveStart)
                    && CatPatchNetwork.IsRegistered(true, NetworkMessageIds.SelfHarmRescueHandState)
                    && CatPatchNetwork.IsRegistered(false, NetworkMessageIds.SelfHarmRescueHandState)
                    && CatPatchNetwork.IsRegistered(false, NetworkMessageIds.GunSuicidePushCancel)
                    && CatPatchNetwork.IsRegistered(true, NetworkMessageIds.GunSuicidePresentationStart)
                    && CatPatchNetwork.IsRegistered(true, NetworkMessageIds.GunSuicidePresentationEnd)
                    && CatPatchNetwork.IsRegistered(false, NetworkMessageIds.GunSuicidePresentationStart)
                    && CatPatchNetwork.IsRegistered(false, NetworkMessageIds.GunSuicidePresentationEnd))
                    return;
            }

            _handlersRegistered = true;
            _nextRegistrationVerifyAt = Time.realtimeSinceStartupAsDouble + RegistrationVerifyIntervalSeconds;

            CatPatchNetwork.RegisterServer(NetworkMessageIds.SelfHarmRescueStart, OnServerStartReceived);
            CatPatchNetwork.RegisterServer(NetworkMessageIds.SelfHarmRescueEnd, OnServerEndReceived);
            CatPatchNetwork.RegisterServer(NetworkMessageIds.SelfHarmRescueAttempt, OnServerAttemptReceived);
            CatPatchNetwork.RegisterServer(NetworkMessageIds.SelfHarmRescueIntervene, OnServerInterventionReceived);
            CatPatchNetwork.RegisterServer(NetworkMessageIds.SelfHarmRescueHandState, OnServerObserverHandStateReceived);
            CatPatchNetwork.RegisterServer(NetworkMessageIds.GunSuicidePresentationStart, OnServerGunSuicideStartReceived);
            CatPatchNetwork.RegisterServer(NetworkMessageIds.GunSuicidePresentationEnd, OnServerGunSuicideEndReceived);

            CatPatchNetwork.RegisterClient(NetworkMessageIds.SelfHarmRescueState, OnClientStateReceived);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.SelfHarmRescueResult, OnClientResultReceived);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.SelfHarmRescueAlert, OnClientAlertReceived);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.SelfHarmTreatmentAccept, OnClientTreatmentAcceptReceived);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.SelfHarmRescueRetaliation, OnClientRetaliationReceived);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.SelfHarmRescueObserveStart, OnClientObserveStartReceived);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.SelfHarmRescueHandState, OnClientObserverHandStateReceived);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.GunSuicidePushCancel, OnClientGunSuicidePushCancelReceived);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.GunSuicidePresentationStart, OnClientGunSuicideStartReceived);
            CatPatchNetwork.RegisterClient(NetworkMessageIds.GunSuicidePresentationEnd, OnClientGunSuicideEndReceived);
        }

        private static void OnServerGunSuicideStartReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid claimedClientId;
            reader.Get(out claimedClientId);
            if (claimedClientId == senderClientId)
                HandleServerGunSuicidePresentation(senderClientId, true);
        }

        private static void OnServerGunSuicideEndReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid claimedClientId;
            reader.Get(out claimedClientId);
            if (claimedClientId == senderClientId)
                HandleServerGunSuicidePresentation(senderClientId, false);
        }

        private static void OnServerStartReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            byte harmType;
            reader.Get(out targetClientId);
            reader.Get(out harmType);

            if (targetClientId == senderClientId)
                _instance.HandleServerStart(targetClientId, harmType);
        }

        private static void OnServerEndReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            reader.Get(out targetClientId);

            if (targetClientId == senderClientId)
                _instance.HandleServerEnd(targetClientId);
        }

        private static void OnServerAttemptReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            bool success;
            reader.Get(out targetClientId);
            reader.Get(out success);

            _instance.HandleServerAttempt(senderClientId, targetClientId, success);
        }

        private static void OnServerInterventionReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            reader.Get(out targetClientId);
            _instance.HandleServerIntervention(senderClientId, targetClientId);
        }

        private static void OnServerObserverHandStateReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            reader.Get(out targetClientId);
            Vector2 handPosition = new Vector2(reader.GetFloat(), reader.GetFloat());
            Vector2 handVelocity = new Vector2(reader.GetFloat(), reader.GetFloat());
            bool clicking;
            reader.Get(out clicking);
            _instance.HandleServerObserverHandState(senderClientId, targetClientId, handPosition, handVelocity, clicking);
        }

        private static void OnClientStateReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            bool active;
            reader.Get(out targetClientId);
            reader.Get(out active);

            _instance.SetStateLocal(targetClientId, active);
            if (active)
                Plugin.Logger.LogInfo("[SelfHarmRescue] client state started target="
                    + CatPatchNetId.ToDisplay(targetClientId));
        }

        private static void OnClientResultReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            bool rescued;
            reader.Get(out targetClientId);
            reader.Get(out rescued);

            _instance.ReceiveResultLocal(targetClientId, rescued);
        }

        private static void OnClientAlertReceived(knetid senderClientId, ref NetDataReader reader)
        {
            string message;
            string voiceLine;
            reader.Get(out message, true);
            reader.Get(out voiceLine, true);
            Util.DoAlert(message, false);
            SpeakAsLocalPlayer(voiceLine);
        }

        private static void OnClientTreatmentAcceptReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            float seconds;
            reader.Get(out targetClientId);
            reader.Get(out seconds);

            _instance._acceptTreatmentUntilByClientId[targetClientId] = Time.realtimeSinceStartupAsDouble + Mathf.Max(0.0f, seconds);
            _instance._suppressTalkUntilByClientId[targetClientId] = Time.realtimeSinceStartupAsDouble + 4.0;
        }

        private static void OnClientObserveStartReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            knetid helperClientId;
            reader.Get(out targetClientId);
            reader.Get(out helperClientId);
            if (IsLocalClient(targetClientId))
                _instance.BeginLocalIntervention(targetClientId, helperClientId);
        }

        private static void OnClientObserverHandStateReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid targetClientId;
            reader.Get(out targetClientId);
            Vector2 handPosition = new Vector2(reader.GetFloat(), reader.GetFloat());
            Vector2 handVelocity = new Vector2(reader.GetFloat(), reader.GetFloat());
            bool clicking;
            reader.Get(out clicking);
            _instance.UpdateObserverHandState(targetClientId, handPosition, handVelocity, clicking);
        }

        private static void OnClientRetaliationReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid helperClientId;
            knetid targetClientId;
            byte scratchedLimbIndex;
            reader.Get(out helperClientId);
            reader.Get(out targetClientId);
            reader.Get(out scratchedLimbIndex);

            Body helperBody;
            Body targetBody;
            if (!TryGetBody(helperClientId, out helperBody)
                || !TryGetBody(targetClientId, out targetBody)
                || helperBody == null
                || targetBody == null)
                return;

            if (scratchedLimbIndex != byte.MaxValue
                && helperBody.limbs != null
                && scratchedLimbIndex < helperBody.limbs.Length)
                AddScratchWoundVisual(helperBody.limbs[scratchedLimbIndex]);

            bool localHelper = IsLocalClient(helperClientId);
            if (localHelper)
            {
                ApplyRetaliationMotion(helperBody, targetBody);
                Sound.Play("cut1", helperBody.transform.position, false, true, null, 1.0f, 1.0f, false, false);
            }

        }

        private static void OnClientGunSuicidePushCancelReceived(knetid senderClientId, ref NetDataReader reader)
        {
            if (_instance == null)
                return;

            knetid victimClientId;
            reader.Get(out victimClientId);
            if (IsLocalClient(victimClientId))
                _instance.RequestLocalGunSuicideCancellation(victimClientId);
        }

        private static void OnClientGunSuicideStartReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid victimClientId;
            reader.Get(out victimClientId);
            if (!IsLocalClient(victimClientId))
                ApplyGunSuicidePresentation(victimClientId, true);
        }

        private static void OnClientGunSuicideEndReceived(knetid senderClientId, ref NetDataReader reader)
        {
            knetid victimClientId;
            reader.Get(out victimClientId);
            if (!IsLocalClient(victimClientId))
                ApplyGunSuicidePresentation(victimClientId, false);
        }

        private static bool HandlerDictionaryContains(string fieldName, ushort messageId)
        {
            try
            {
                FieldInfo field = typeof(Net).GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                    return false;

                object dictionaryObject = field.GetValue(null);
                Dictionary<ushort, KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate> dictionary =
                    dictionaryObject as Dictionary<ushort, KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate>;

                return dictionary != null && dictionary.ContainsKey(messageId);
            }
            catch
            {
                return false;
            }
        }

        private static void LegacyRegisterNetReceiver(
            string methodName,
            ushort messageId,
            KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate handler)
        {
            try
            {
                MethodInfo method = typeof(Net).GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
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
                Plugin.Logger.LogWarning("Could not register " + methodName + " for self-harm rescue msg " + messageId + ": " + ex.Message);
            }
        }

        private sealed class CancelledSelfHarmMinigame : Minigame
        {
            public override HandSpriteType HandType() { return HandSpriteType.SelfHarm; }
            public override string GuideLocaleString() { return string.Empty; }
            public override bool NeedsItem() { return false; }
            public override bool CanExit() { return false; }
            public override void Start() { }
            public override void Update(List<RaycastResult> uiCasts) { }
        }

        private sealed class RescueObserverMinigame : Minigame
        {
            private readonly string _helperName;
            private Vector2 _remoteHandPosition;
            private Vector2 _remoteHandVelocity;
            private bool _remoteClicking;

            public RescueObserverMinigame(knetid targetClientId, string helperName)
            {
                TargetClientId = targetClientId;
                _helperName = string.IsNullOrWhiteSpace(helperName) ? "Another player" : helperName;
            }

            public knetid TargetClientId { get; private set; }

            public void SetRemoteHandState(Vector2 position, Vector2 velocity, bool clicking)
            {
                _remoteHandPosition = position;
                _remoteHandVelocity = velocity;
                _remoteClicking = clicking;
            }

            public override HandSpriteType HandType()
            {
                return _remoteClicking ? HandSpriteType.SelfHarmForceOn : HandSpriteType.SelfHarm;
            }

            public override string GuideLocaleString() { return string.Empty; }
            public override bool NeedsItem() { return false; }
            public override bool CanExit() { return false; }

            public override void Start()
            {
                MinigameBase.main.CreateScreen("Special/SelfHarmMinigame");
                Transform screen = MinigameBase.main.spawnedMiniGame;
                if (screen != null)
                {
                    if (screen.childCount > 1)
                        screen.GetChild(1).gameObject.SetActive(true);
                    if (screen.childCount > 0)
                        screen.GetChild(0).gameObject.SetActive(false);
                    if (screen.childCount > 2)
                        screen.GetChild(2).gameObject.SetActive(false);
                }

                SetUiText(MinigameBase.main, "itemText", "Being rescued by " + _helperName);
                SetUiText(MinigameBase.main, "guideText", "Watch their attempt.");
            }

            public override void PhysicsUpdate(float deltaTime)
            {
                MinigameBase.main.handPos = Vector2.Lerp(MinigameBase.main.handPos, _remoteHandPosition, 0.65f);
                MinigameBase.main.handVelocity = _remoteHandVelocity;
            }

            public override void Update(List<RaycastResult> uiCasts)
            {
                SetUiText(MinigameBase.main, "guideText", "Watch " + _helperName + " try to help.");
            }

            private static void SetUiText(object uiOwner, string memberName, string value)
            {
                if (uiOwner == null)
                    return;

                try
                {
                    FieldInfo field = AccessTools.Field(uiOwner.GetType(), memberName);
                    object obj = field != null ? field.GetValue(uiOwner) : null;
                    if (obj == null)
                    {
                        PropertyInfo property = AccessTools.Property(uiOwner.GetType(), memberName);
                        obj = property != null ? property.GetValue(uiOwner, null) : null;
                    }

                    PropertyInfo textProperty = obj != null ? AccessTools.Property(obj.GetType(), "text") : null;
                    if (textProperty != null && textProperty.CanWrite)
                        textProperty.SetValue(obj, value, null);
                }
                catch
                {
                }
            }
        }

        private sealed class RescueSwipeMinigame : Minigame
        {
            private readonly SelfHarmRescueController _runtime;
            private readonly knetid _targetClientId;
            private readonly string _targetName;
            private readonly double _endsAt;
            private bool _finished;
            private bool _trackingSwipe;
            private Vector2 _swipeStartPos;
            private float _swipeStartTime;

            public RescueSwipeMinigame(SelfHarmRescueController runtime, knetid targetClientId, string targetName)
            {
                _runtime = runtime;
                _targetClientId = targetClientId;
                _targetName = string.IsNullOrWhiteSpace(targetName) ? "Player" : targetName;
                _endsAt = Time.realtimeSinceStartupAsDouble + SwipeTimeoutSeconds;
            }

            public override Minigame.HandSpriteType HandType()
            {
                return _trackingSwipe ? Minigame.HandSpriteType.SelfHarmForceOn : Minigame.HandSpriteType.SelfHarm;
            }

            public override string GuideLocaleString()
            {
                return "";
            }

            public override bool NeedsItem()
            {
                return false;
            }

            public override bool CanExit()
            {
                return true;
            }

            public override void Start()
            {
                MinigameBase.main.CreateScreen("Special/SelfHarmMinigame");
                Transform spawnedMiniGame = MinigameBase.main.spawnedMiniGame;
                if (spawnedMiniGame != null)
                {
                    if (spawnedMiniGame.childCount > 1)
                        spawnedMiniGame.GetChild(1).gameObject.SetActive(true);

                    if (spawnedMiniGame.childCount > 0)
                        spawnedMiniGame.GetChild(0).gameObject.SetActive(false);

                    if (spawnedMiniGame.childCount > 2)
                        spawnedMiniGame.GetChild(2).gameObject.SetActive(false);
                }

                TrySetUiText(MinigameBase.main, "itemText", "Stop: " + _targetName);
                TrySetUiText(MinigameBase.main, "guideText", "Fast side swipe before time runs out.");
            }

            public override void Update(List<RaycastResult> uiCasts)
            {
                if (_finished)
                    return;

                Body body;
                if (!TryGetBody(_targetClientId, out body) || body == null || !body.alive || !_runtime.IsStateActive(_targetClientId))
                {
                    Fail("Target is invalid.");
                    return;
                }

                if (Time.realtimeSinceStartupAsDouble >= _endsAt)
                {
                    Fail("Too slow.");
                    return;
                }

                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
                {
                    Fail("Rescue canceled.");
                    return;
                }

                if (!_trackingSwipe && MinigameBase.main.handStartedClicking)
                {
                    _trackingSwipe = true;
                    _swipeStartPos = MinigameBase.main.handPos;
                    _swipeStartTime = Time.unscaledTime;
                    return;
                }

                if (_trackingSwipe && MinigameBase.main.handStoppedClicking)
                {
                    _trackingSwipe = false;
                    Vector2 delta = MinigameBase.main.handPos - _swipeStartPos;
                    float duration = Mathf.Max(0.0001f, Time.unscaledTime - _swipeStartTime);

                    if (TryResolveSwipe(delta, duration))
                        Succeed();
                    else
                        Util.DoAlert("Swipe faster and more sideways.", false);
                }

                double remaining = Math.Max(0.0, _endsAt - Time.realtimeSinceStartupAsDouble);
                TrySetUiText(MinigameBase.main, "guideText", "Swipe hard side to side. " + remaining.ToString("0.0") + "s");
                _runtime.SendObserverHandState(
                    _targetClientId,
                    MinigameBase.main.handPos,
                    MinigameBase.main.handVelocity,
                    _trackingSwipe || MinigameBase.main.handClicking);
            }

            private static bool TryResolveSwipe(Vector2 delta, float duration)
            {
                float x = Mathf.Abs(delta.x);
                float y = Mathf.Abs(delta.y);

                return duration <= SwipeMaxDurationSeconds
                    && x >= SwipeMinDistance
                    && x >= y * SwipeHorizontalRatio;
            }

            private void Succeed()
            {
                _finished = true;
                _runtime.OnRescueMinigameSucceeded(_targetClientId);
                if (MinigameBase.main != null)
                    MinigameBase.main.EndMinigame();
            }

            private void Fail(string reason)
            {
                _finished = true;
                _runtime.OnRescueMinigameFailed(_targetClientId, reason);
                if (MinigameBase.main != null)
                    MinigameBase.main.EndMinigame();
            }

            private static void TrySetUiText(object uiOwner, string memberName, string value)
            {
                if (uiOwner == null || string.IsNullOrWhiteSpace(memberName))
                    return;

                try
                {
                    FieldInfo field = AccessTools.Field(uiOwner.GetType(), memberName);
                    object obj = field != null ? field.GetValue(uiOwner) : null;
                    if (obj == null)
                    {
                        PropertyInfo property = AccessTools.Property(uiOwner.GetType(), memberName);
                        obj = property != null ? property.GetValue(uiOwner, null) : null;
                    }

                    if (obj == null)
                        return;

                    PropertyInfo textProperty = AccessTools.Property(obj.GetType(), "text");
                    if (textProperty != null && textProperty.CanWrite)
                        textProperty.SetValue(obj, value, null);
                }
                catch
                {
                }
            }
        }

        private sealed class RescueState
        {
            public knetid TargetClientId;
            public knetid HelperClientId;
            public byte HarmType;
            public double ExpiresAt;
        }

        private sealed class LocalPendingHarm
        {
            public knetid ClientId;
            public Body Body;
            public SelfHarmer Harmer;
            public byte HarmType;
            public double ExpiresAt;
            public bool Rescued;
            public bool ForceImmediateHarm;
            public bool InterventionActive;
            public bool FailureDamageAppliedByServer;
        }

        private sealed class GunSuicideAttempt
        {
            public SelfHarmer Harmer;
            public Body Body;
            public Item Gun;
            public bool CancelRequested;
        }

    }

    [HarmonyPatch(typeof(NetBody), nameof(NetBody.Push))]
    internal static class NetBodyPushGunSuicideCancelPatch
    {
        private static void Prefix(NetBody __instance, NetBody victim, out bool __state)
        {
            __state = victim != null
                && __instance != null
                && victim.piggybacking_on != __instance
                && victim.carrying_person != __instance;
        }

        private static void Postfix(NetBody victim, bool __state)
        {
            if (__state)
                SelfHarmRescueController.HandleAuthoritativePush(victim);
        }
    }

    [HarmonyPatch(typeof(SelfHarmer), "SelfHarm")]
    internal static class SelfHarmerSelfHarmCatPatch
    {
        internal static IEnumerator CreateRoutine(SelfHarmer harmer)
        {
            IEnumerator routine = null;
            Prefix(harmer, ref routine);
            return routine;
        }

        private static bool Prefix(SelfHarmer __instance, ref IEnumerator __result)
        {
            Body body = __instance != null ? __instance.GetComponent<Body>() : null;
            if (body == null)
                return true;

            try
            {
                if (KrokoshaScavMultiplayer.network_system_is_running
                    && !SelfHarmRescueController.IsOwnedByLocalPlayer(body))
                {
                    __instance.harming = false;
                    __result = SelfHarmRescueController.SkipRemoteSelfHarmCoroutine();
                    return false;
                }
            }
            catch
            {
            }

            __result = SelfHarmRescueController.RunSelfHarmCoroutine(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(SelfHarmer), "Suicide")]
    internal static class SelfHarmerSuicideCatPatch
    {
        internal static IEnumerator CreateRoutine(SelfHarmer harmer)
        {
            IEnumerator routine = null;
            Prefix(harmer, ref routine);
            return routine;
        }

        private static bool Prefix(SelfHarmer __instance, ref IEnumerator __result)
        {
            Body body = __instance != null ? __instance.GetComponent<Body>() : null;
            if (body == null)
                return true;

            try
            {
                if (KrokoshaScavMultiplayer.network_system_is_running
                    && !SelfHarmRescueController.IsOwnedByLocalPlayer(body))
                {
                    __instance.harming = false;
                    __result = SelfHarmRescueController.SkipRemoteSelfHarmCoroutine();
                    return false;
                }
            }
            catch
            {
            }

            __result = SelfHarmRescueController.RunSuicideCoroutine(__instance);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class SelfHarmRescueOpenSpecificBodyPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Together.PlayerCamera_ToggleWoundView_MultiplayerPatch");
            if (type == null)
                return null;

            return AccessTools.Method(type, "OpenSpecificBody", new[] { typeof(Body), typeof(bool) });
        }

        private static bool Prefix(object[] __args)
        {
            Body b = __args != null && __args.Length > 0 ? __args[0] as Body : null;
            if (SelfHarmRescueController.TryStartRescueForBody(b))
                return false;

            return true;
        }
    }

    [HarmonyPatch]
    internal static class SelfHarmRescueWoundViewSetTargetBodyPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Together.PlayerCamera_ToggleWoundView_MultiplayerPatch");
            if (type == null)
                return null;

            return AccessTools.Method(type, "WoundView_SetTargetBody", new[] { typeof(Body) });
        }

        private static bool Prefix(object[] __args)
        {
            Body body = __args != null && __args.Length > 0 ? __args[0] as Body : null;
            if (SelfHarmRescueController.TryStartRescueForBody(body))
                return false;

            return true;
        }
    }

    [HarmonyPatch]
    internal static class SelfHarmRescueMedicalRefusalPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Together.MedicalSync");
            if (type == null)
                return null;

            return AccessTools.Method(type, "IsRefusingHelp", new[] { typeof(Body) });
        }

        private static void Postfix(Body b, ref bool __result)
        {
            if (!__result)
                return;

            if (SelfHarmRescueController.HasTreatmentAcceptanceOverride(b))
                __result = false;
        }
    }

    [HarmonyPatch]
    internal static class SelfHarmRescueTalkSuppressionPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(Talker)))
            {
                if (method.Name == "Talk")
                    yield return method;
            }
        }

        private static bool Prefix(Talker __instance, object[] __args)
        {
            try
            {
                return SelfHarmRescueController.ShouldAllowTalk(__instance, __args);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("Self-harm rescue speech suppression failed: " + ex.Message);
                return true;
            }
        }
    }
}
