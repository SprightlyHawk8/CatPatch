using HarmonyLib;
using Together;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Collections.Generic;

namespace CatPatchspace
{
    // IMGUI event capture does not stop Input.GetKey polling or Canvas input fields.
    // Share this guard between mod shortcuts and the game's world-input entry points.
    internal static class CatPatchUiInput
    {
        private static int _settingsClosedThroughFrame = -1;
        private static int _updaterClosedThroughFrame = -1;
        private static Rect _closedSettingsRect;
        private static Rect _closedUpdaterRect;
        private static GameObject _selectedObject;
        private static TMP_InputField _selectedTmpField;
        private static InputField _selectedLegacyField;

        internal static Vector2 GetLiveGuiPointerPosition()
        {
            Vector2 mouse = Input.mousePosition;
            mouse.y = Screen.height - mouse.y;
            return mouse;
        }

        // Only GUI event routing uses Event.current. Update and Canvas processing
        // must use the live screen pointer; Event.current can be stale or window-local.
        internal static Vector2 GetGuiPointerPosition() => Event.current != null
            ? Event.current.mousePosition : GetLiveGuiPointerPosition();

        internal static bool IsSettingsPointCaptured(Vector2 point)
        {
            return (CatPatchHostSettingsMenu.TryGetCaptureRect(out var rect) && rect.Contains(point))
                || (Time.frameCount <= _settingsClosedThroughFrame && _closedSettingsRect.Contains(point));
        }

        internal static bool IsUpdaterPointCaptured(Vector2 point)
        {
            return (CatPatchUpdater.TryGetCaptureRect(out var rect) && rect.Contains(point))
                || (Time.frameCount <= _updaterClosedThroughFrame && _closedUpdaterRect.Contains(point));
        }

        internal static bool IsPointCaptured(Vector2 point)
        {
            return IsSettingsPointCaptured(point) || IsUpdaterPointCaptured(point)
                || (CatPatchHostSettingsMenu.TryGetButtonRect(out var button) && button.Contains(point));
        }

        internal static bool IsSettingsInputCaptured() => IsSettingsPointCaptured(GetLiveGuiPointerPosition());
        internal static bool IsUpdaterPointerCaptured() => IsUpdaterPointCaptured(GetLiveGuiPointerPosition());
        internal static bool IsPointerInputCaptured() => IsPointCaptured(GetLiveGuiPointerPosition());

        internal static void HoldSettingsClosingInput(Rect window)
        {
            _closedSettingsRect = window;
            _settingsClosedThroughFrame = Time.frameCount + 1;
        }

        internal static void HoldUpdaterClosingInput(Rect window)
        {
            _closedUpdaterRect = window;
            _updaterClosedThroughFrame = Time.frameCount + 1;
        }

        internal static void CaptureUpdaterInput(Rect window)
        {
            if (!window.Contains(GetGuiPointerPosition()))
                return;
            UIBullshit.CheckCursorOverlap(window);
            ConsumeWindowPointer(window);
        }

        internal static void CaptureCanvasFocus()
        {
            if (!IsPointerInputCaptured())
                return;
            var events = EventSystem.current;
            if (events != null && events.currentSelectedGameObject != null)
                events.SetSelectedGameObject(null);
        }

        internal static bool BeginBackgroundGui()
        {
            var input = Event.current;
            if (!IsPointCaptured(GetGuiPointerPosition()) || input == null)
                return true;
            if (input.type == EventType.Used)
                return false;
            if (!IsUserInput(input.rawType))
                return true;
            input.Use();
            return false;
        }

        internal static void ConsumeWindowPointer(Rect window)
        {
            var current = Event.current;
            if (current != null && window.Contains(current.mousePosition)
                && (current.isMouse || current.type == EventType.ScrollWheel || current.type == EventType.ContextClick))
                current.Use();
        }
        private static bool IsUserInput(EventType type)
        {
            switch (type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.MouseMove:
                case EventType.ScrollWheel:
                case EventType.ContextClick:
                case EventType.KeyDown:
                case EventType.KeyUp:
                case EventType.DragUpdated:
                case EventType.DragPerform:
                case EventType.DragExited:
                case EventType.ValidateCommand:
                case EventType.ExecuteCommand:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsShortcutInputCaptured()
        {
            if (IsPointerInputCaptured())
                return true;

            // Do not use showMultiplayerMenu: Together can keep that legacy flag set
            // while its actual menu is closed. IsAnyMenuOpen covers its bug-report tab.
            return UIBullshit.IsAnyMenuOpen() || Chat.CHAT_textbox_input_focused || IsTextFieldFocused();
        }

        private static bool IsTextFieldFocused()
        {
            var events = EventSystem.current;
            var selected = events != null ? events.currentSelectedGameObject : null;
            if (selected != _selectedObject)
            {
                _selectedObject = selected;
                _selectedTmpField = selected != null ? selected.GetComponent<TMP_InputField>() : null;
                _selectedLegacyField = selected != null ? selected.GetComponent<InputField>() : null;
            }
            if (selected == null || !selected.activeInHierarchy)
                return false;
            return (_selectedTmpField != null && _selectedTmpField.isActiveAndEnabled && _selectedTmpField.isFocused)
                || (_selectedLegacyField != null && _selectedLegacyField.isActiveAndEnabled && _selectedLegacyField.isFocused);
        }
    }

    // IMGUI windows are not raycast targets. Without this, Unity Canvas buttons
    // can still receive the same click even after the IMGUI event is consumed.
    [HarmonyPatch(typeof(EventSystem), nameof(EventSystem.RaycastAll))]
    internal static class CatPatchWindowCanvasInputPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(List<RaycastResult> raycastResults)
        {
            if (!CatPatchUiInput.IsPointerInputCaptured() && !CatPatchHostSettingsMenu.IsPointerOverButton())
                return true;
            raycastResults.Clear();
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleInput))]
    internal static class CatPatchModalPlayerInputPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerCamera __instance)
        {
            if (!CatPatchUiInput.IsSettingsInputCaptured())
                return true;
            // Skipping input alone leaves the last movement request held in multiplayer.
            var body = __instance.body;
            var localPlayer = Net.IsRunning ? ScavPlayer.LocalPlayer : null;
            if (body != null && (!Net.IsRunning || (localPlayer != null && localPlayer.body == body)))
            {
                body.moveDir = Vector2.zero;
                body.forceWalk = false;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerCamera), "HandleAttacks")]
    internal static class CatPatchModalAttackInputPatch
    {
        [HarmonyPrefix]
        private static bool Prefix() => !CatPatchUiInput.IsPointerInputCaptured();
    }

    [HarmonyPatch(typeof(PlayerCamera), "HandleDragging")]
    internal static class CatPatchModalDragInputPatch
    {
        [HarmonyPrefix]
        private static bool Prefix() => !CatPatchUiInput.IsPointerInputCaptured();
    }
}
