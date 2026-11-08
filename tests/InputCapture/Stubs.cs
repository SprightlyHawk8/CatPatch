using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public static class GUI { public static bool enabled = true; }
    public static class Input
    {
        public static Vector2 mousePosition = new(50, 50);
        public static bool MouseDown, MouseUp;
        public static bool GetMouseButtonDown(int button) => MouseDown;
        public static bool GetMouseButtonUp(int button) => MouseUp;
    }
    public static class Screen { public static int height = 100; }
    public enum EventType { MouseDown, MouseUp, MouseDrag, MouseMove, ScrollWheel, ContextClick, KeyDown, KeyUp, DragUpdated, DragPerform, DragExited, ValidateCommand, ExecuteCommand, Layout, Repaint, Used }
    public class Event
    {
        public static Event current;
        public EventType type, rawType;
        public Vector2 mousePosition;
        public bool isMouse => type == EventType.MouseDown || type == EventType.MouseUp || type == EventType.MouseDrag || type == EventType.MouseMove;
        public void Use() => type = EventType.Used;
    }
    public struct Rect
    {
        private float _x, _y, _w, _h;
        public Rect(float x, float y, float w, float h) { _x = x; _y = y; _w = w; _h = h; }
        public bool Contains(Vector2 p) => p.x >= _x && p.x < _x + _w && p.y >= _y && p.y < _y + _h;
    }
    public class GameObject
    {
        public bool activeInHierarchy = true;
        public string name = "safe test button";
        public int ComponentLookups;
        public Dictionary<Type, object> Components = new();
        public T GetComponent<T>() where T : class
        {
            ComponentLookups++;
            return Components.TryGetValue(typeof(T), out var component) ? component as T : null;
        }
    }
    public static class Time { public static int frameCount = 10; }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float a, float b) { x = a; y = b; }
        public static Vector2 zero => new(0, 0);
    }
}
namespace UnityEngine.EventSystems
{
    public struct RaycastResult { }
    public class EventSystem
    {
        public static EventSystem current;
        public UnityEngine.GameObject currentSelectedGameObject;
        public void SetSelectedGameObject(UnityEngine.GameObject selected) => currentSelectedGameObject = selected;
        public void RaycastAll() { }
    }
}
namespace UnityEngine.UI { public class InputField { public bool isActiveAndEnabled = true, isFocused; } }
namespace TMPro { public class TMP_InputField { public bool isActiveAndEnabled = true, isFocused; } }
namespace Together
{
    public static class Net { public static bool IsRunning; }
    public class ScavPlayer { public static ScavPlayer LocalPlayer; public Body body; }
    public static class UIBullshit { public static bool MenuOpen; public static bool IsAnyMenuOpen() => MenuOpen; public static void CheckCursorOverlap(Rect rect) { } }
    public static class Chat { public static bool CHAT_textbox_input_focused; }
    public static class Con { public static bool IsConsoleOpen() => ConsoleScript.instance != null && ConsoleScript.instance.active; }
}
public class Body { public UnityEngine.Vector2 moveDir; public bool forceWalk; }
public class AdaptiveButton
{
    public GameObject gameObject = new();
    public int Actions;
    public bool overlayActive { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] get => false; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Clicked() => Actions++;
    public void PollRawMouse() { if (Input.GetMouseButtonDown(0) && !overlayActive) Clicked(); }
}
public class PlayerCamera
{
    public static PlayerCamera main;
    public UnityEngine.GameObject craftingPanel;
    public Body body;
    public void HandleInput() { }
}
public class ConsoleScript
{
    public static ConsoleScript instance;
    public bool active;
    public UnityEngine.GameObject gameObject = new();
}
namespace CatPatchspace
{
    internal static class CatPatchUpdater
    {
        internal static bool IsPopupOpen;
        internal static Rect PopupRect = new(0, 0, 30, 30);
        internal static bool TryGetCaptureRect(out Rect rect) { rect = PopupRect; return IsPopupOpen; }
    }
    internal static class CatPatchHostSettingsMenu
    {
        internal static bool IsWindowOpen, ShowButton;
        internal static Rect WindowRect = new(0, 0, 30, 30), ButtonRect = new(70, 70, 10, 10);
        internal static bool TryGetCaptureRect(out Rect rect) { rect = WindowRect; return IsWindowOpen; }
        internal static bool TryGetButtonRect(out Rect rect) { rect = ButtonRect; return ShowButton; }
        internal static bool IsPointerOverButton() => ShowButton && ButtonRect.Contains(CatPatchUiInput.GetLiveGuiPointerPosition());
    }
    internal static class CatPatchHostModerationMenu { internal static bool IsPointerOverWindow() => false; }
    internal static class Plugin
    {
        internal const string ModGUID = "catpatch.input.tests";
        internal static TestLog Logger = new();
    }
    internal class TestLog
    {
        internal List<string> Messages = new();
        internal void LogInfo(string text) => Messages.Add(text);
        internal void LogError(string text) => throw new Exception(text);
    }
}
