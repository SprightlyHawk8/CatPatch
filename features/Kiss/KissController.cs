using Together;
using UnityEngine;

namespace CatPatchspace
{
    internal sealed class KissController : MonoBehaviour
    {
        private readonly HugTargetFinder _targetFinder = new HugTargetFinder();
        private readonly KissVisualController _visualController = new KissVisualController();
        private KissNetworkController _networkController;
        private KissPromptState _promptState;
        private double _nextAllowedRequestAt;

        private void Awake()
        {
            CatPatchKeybinds.RegisterDefaultsOnce();
            _promptState = new KissPromptState();
            _networkController = new KissNetworkController(_promptState, _visualController);
            KissNetworkController.SetInstance(_networkController);
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
            _visualController.UpdateHands();
        }

        private void OnGUI()
        {
            if (!_promptState.HasIncomingPrompt)
                return;

            Rect box = new Rect(Screen.width * 0.5f - 190f, Screen.height * 0.68f, 380f, 62f);
            GUI.Box(box, "");
            GUI.Label(new Rect(box.x + 12f, box.y + 9f, box.width - 24f, 22f), _promptState.RequesterName + " wants to kiss.");
            GUI.Label(
                new Rect(box.x + 12f, box.y + 32f, box.width - 24f, 22f),
                "Press " + CatPatchKeybinds.GetBindName(CatPatchKeybinds.KissAction, KeyCode.None)
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
                if (CatPatchKeybinds.GetKeyDown(CatPatchKeybinds.KissAction, KeyCode.None))
                    AcceptIncomingKiss();

                if (CatPatchKeybinds.GetKeyDown(CatPatchKeybinds.HugRejectAction, CatPatchKeybinds.FallbackHugRejectKey))
                    RejectIncomingKiss();

                return;
            }

            if (!CatPatchKeybinds.IsBound(CatPatchKeybinds.KissAction))
                return;

            if (!CatPatchKeybinds.GetKeyDown(CatPatchKeybinds.KissAction, KeyCode.None))
                return;

            TrySendKissRequest();
        }

        private void TrySendKissRequest()
        {
            if (Time.realtimeSinceStartupAsDouble < _nextAllowedRequestAt)
                return;

            ScavPlayer localPlayer = ScavPlayer.LocalPlayer;
            Body localBody = localPlayer != null ? localPlayer.body : null;
            if (!HugValidation.IsBodyHuggable(localBody))
                return;

            ScavPlayer target = _targetFinder.FindNearestHuggableTarget(localPlayer, localBody.transform.position);
            if (target == null)
                return;

            _nextAllowedRequestAt = Time.realtimeSinceStartupAsDouble + KissSettings.RequestCooldownSeconds;
            _networkController.SendRequest(target.playerId);
        }

        private void AcceptIncomingKiss()
        {
            if (!_promptState.HasIncomingPrompt)
                return;

            knetid requesterClientId = _promptState.RequesterClientId;
            _promptState.Clear();
            _networkController.SendResponse(requesterClientId, true);
        }

        private void RejectIncomingKiss()
        {
            if (!_promptState.HasIncomingPrompt)
                return;

            knetid requesterClientId = _promptState.RequesterClientId;
            _promptState.Clear();
            _networkController.SendResponse(requesterClientId, false);
        }
    }
}
