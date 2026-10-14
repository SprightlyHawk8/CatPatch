using CatPatchspace;
using Together;
using UnityEngine;

namespace CatPatchspace
{
    public sealed class HugController : MonoBehaviour
    {
        private readonly HugTargetFinder _targetFinder = new HugTargetFinder();
        private readonly HugVisualController _visualController = new HugVisualController();
        private HugNetworkController _networkController;
        private HugPromptState _promptState;

        private double _nextAllowedRequestAt;

        private void Awake()
        {
            CatPatchKeybinds.RegisterDefaultsOnce();
            _promptState = new HugPromptState();
            _networkController = new HugNetworkController(_promptState, _visualController);
            HugNetworkController.SetInstance(_networkController);
        }

        private void Update()
        {
            if (!KrokoshaScavMultiplayer.network_system_is_running)
            {
                _networkController.ResetRegistration();
                _promptState.Clear();
                _visualController.Stop();
                return;
            }

            _networkController.RegisterHandlersOnce();
            _networkController.Tick();
            _promptState.Update();
            HandleInput();
            _visualController.UpdatePose();
        }

        private void LateUpdate()
        {
            _visualController.UpdateArms();
        }

        private void OnGUI()
        {
            if (!_promptState.HasIncomingPrompt)
                return;

            ScavPlayer requester;
            string requesterName = _promptState.RequesterName;
            if (ScavPlayer.TryGetPlayerFromClientId(_promptState.RequesterClientId, out requester) && !string.IsNullOrWhiteSpace(requester.playerName))
                requesterName = requester.playerName;

            Rect box = new Rect(Screen.width * 0.5f - 180f, Screen.height * 0.75f, 360f, 58f);
            GUI.Box(box, "");
            GUI.Label(new Rect(box.x + 12f, box.y + 10f, box.width - 24f, 22f), requesterName + " wants to hug.");
            GUI.Label(
                new Rect(box.x + 12f, box.y + 32f, box.width - 24f, 22f),
                "Press " + CatPatchKeybinds.GetBindName(CatPatchKeybinds.HugAction, HugSettings.FallbackHugKey)
                + " to accept, or "
                + CatPatchKeybinds.GetBindName(CatPatchKeybinds.HugRejectAction, CatPatchKeybinds.FallbackHugRejectKey)
                + " to reject.");
        }

        private void HandleInput()
        {
            if (CatPatchConsoleGuard.IsGameInputUiActive())
                return;

            if (_promptState.HasIncomingPrompt)
            {
                if (CatPatchKeybinds.GetKeyDown(CatPatchKeybinds.HugAction, HugSettings.FallbackHugKey))
                    AcceptIncomingHug();

                if (CatPatchKeybinds.GetKeyDown(CatPatchKeybinds.HugRejectAction, CatPatchKeybinds.FallbackHugRejectKey))
                    RejectIncomingHug();

                return;
            }

            if (!CatPatchKeybinds.GetKeyDown(CatPatchKeybinds.HugAction, HugSettings.FallbackHugKey))
                return;

            TrySendHugRequest();
        }

        private void TrySendHugRequest()
        {
            if (IsRequestOnCooldown())
                return;

            ScavPlayer localPlayer;
            Body localBody;

            if (!TryGetValidLocalBody(out localPlayer, out localBody))
                return;

            ScavPlayer target = FindValidHugTarget(localPlayer, localBody);
            if (target == null)
                return;

            StartRequestCooldown();

            _networkController.SendRequest(target.playerId);

            ShowLocalMessage(
                "Sending hug request to " + SafePlayerName(target) + "...");
        }

        private bool IsRequestOnCooldown()
        {
            return Time.realtimeSinceStartupAsDouble < _nextAllowedRequestAt;
        }

        private void StartRequestCooldown()
        {
            _nextAllowedRequestAt =
                Time.realtimeSinceStartupAsDouble
                + HugSettings.RequestCooldownSeconds;
        }

        private bool TryGetValidLocalBody(
            out ScavPlayer localPlayer,
            out Body localBody)
        {
            localPlayer = ScavPlayer.LocalPlayer;
            localBody = localPlayer != null
                ? localPlayer.body
                : null;

            if (localBody == null)
            {
                Plugin.Logger.LogWarning(
                    "Cannot request hug: local body not found.");

                return false;
            }

            if (HugValidation.IsBodyHuggable(localBody))
                return true;

            Plugin.Logger.LogWarning(
                "Cannot request hug: local body is not huggable.");

            return false;
        }

        private ScavPlayer FindValidHugTarget(
            ScavPlayer localPlayer,
            Body localBody)
        {
            ScavPlayer target =
                _targetFinder.FindNearestHuggableTarget(
                    localPlayer,
                    localBody.transform.position);

            if (target != null)
                return target;

            Plugin.Logger.LogWarning(
                "Cannot request hug: no nearby huggable target found.");

            return null;
        }

        private void AcceptIncomingHug()
        {
            if (!_promptState.HasIncomingPrompt)
                return;

            knetid requesterClientId = _promptState.RequesterClientId;
            _promptState.Clear();
            _networkController.SendResponse(requesterClientId, true);
            ShowLocalMessage("Hug accepted.");
        }

        private void RejectIncomingHug()
        {
            if (!_promptState.HasIncomingPrompt)
                return;

            knetid requesterClientId = _promptState.RequesterClientId;
            _promptState.Clear();
            _networkController.SendResponse(requesterClientId, false);
            ShowLocalMessage("Hug rejected.");
        }

        private static string SafePlayerName(ScavPlayer player)
        {
            if (player == null || string.IsNullOrWhiteSpace(player.playerName))
                return "Player";

            return player.playerName;
        }

        internal static void ShowLocalMessage(string message)
        {
            Plugin.Logger.LogInfo(message);

            try
            {
                if (PlayerCamera.main != null)
                    PlayerCamera.main.DoAlert(message, false);
            }
            catch
            {
                // Logging is enough during early testing.
            }
        }
    }
}
