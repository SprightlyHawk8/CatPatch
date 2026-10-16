using UnityEngine;

namespace CatPatchspace
{
    internal sealed class EmoteController : MonoBehaviour
    {
        private const float WheelRadius = 86f;
        private const float SlotWidth = 84f;
        private const float SlotHeight = 38f;

        private readonly EmoteVisualController _visualController = new EmoteVisualController();
        private EmoteNetworkController _networkController;

        private bool _wheelOpen;
        private Vector2 _wheelAnchorScreen;

        private void Awake()
        {
            _networkController = new EmoteNetworkController(_visualController);
            _networkController.RegisterHandlersOnce();
        }

        private void Update()
        {
            _networkController.Tick();
            _networkController.RegisterHandlersOnce();
            HandleInput();
        }

        private void LateUpdate()
        {
            _visualController.LateTick();
        }

        private void OnDestroy()
        {
            _visualController.StopAll();
        }

        private void OnGUI()
        {
            if (_wheelOpen)
                DrawWheel();
        }

        private void HandleInput()
        {
            if (CatPatchConsoleGuard.IsGameInputUiActive())
            {
                CancelWheel();
                return;
            }

            KeyCode key = CatPatchKeybinds.GetKeyCode(
                CatPatchKeybinds.EmoteWheelAction,
                KeyCode.None);

            if (key == KeyCode.None)
            {
                CancelWheel();
                return;
            }

            if (Input.GetKeyDown(key))
            {
                _wheelOpen = true;
                _wheelAnchorScreen = Input.mousePosition;
                return;
            }

            if (!_wheelOpen)
                return;

            if (!Input.GetKeyUp(key))
                return;

            EmoteDefinition hovered;
            if (TryGetHoveredEmote(out hovered))
                _networkController.SendOrStartEmote(hovered.Id);

            CancelWheel();
        }

        private void CancelWheel()
        {
            _wheelOpen = false;
        }

        private void DrawWheel()
        {
            EmoteDefinition[] emotes = EmoteCatalog.All;
            if (emotes == null || emotes.Length == 0)
                return;

            Vector2 center = ToGuiPoint(_wheelAnchorScreen);
            EmoteDefinition hovered;
            TryGetHoveredEmote(out hovered);

            GUI.Box(
                new Rect(center.x - 42f, center.y - 17f, 84f, 34f),
                "Emotes");

            for (int i = 0; i < emotes.Length; i++)
            {
                Rect rect = GetEmoteRect(center, i, emotes.Length);
                Color oldColor = GUI.color;

                if (hovered == emotes[i])
                    GUI.color = new Color(1f, 0.95f, 0.55f, 1f);

                GUI.Box(rect, emotes[i].Label);
                GUI.color = oldColor;
            }
        }

        private bool TryGetHoveredEmote(out EmoteDefinition emote)
        {
            emote = null;

            EmoteDefinition[] emotes = EmoteCatalog.All;
            if (emotes == null || emotes.Length == 0)
                return false;

            Vector2 center = ToGuiPoint(_wheelAnchorScreen);
            Vector2 mouse = ToGuiPoint(Input.mousePosition);

            for (int i = 0; i < emotes.Length; i++)
            {
                if (GetEmoteRect(center, i, emotes.Length).Contains(mouse))
                {
                    emote = emotes[i];
                    return true;
                }
            }

            return false;
        }

        private static Rect GetEmoteRect(Vector2 center, int index, int count)
        {
            float angle = ((float)index / Mathf.Max(1f, count) * 360f - 90f) * Mathf.Deg2Rad;
            Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * WheelRadius;

            return new Rect(
                center.x + offset.x - SlotWidth * 0.5f,
                center.y + offset.y - SlotHeight * 0.5f,
                SlotWidth,
                SlotHeight);
        }

        private static Vector2 ToGuiPoint(Vector2 screenPoint)
        {
            return new Vector2(screenPoint.x, Screen.height - screenPoint.y);
        }
    }
}
