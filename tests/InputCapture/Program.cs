using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CatPatchspace;
using HarmonyLib;
using TMPro;
using Together;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

internal static class Program
{
    private static int _passed;
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAILED " + label);
        _passed++;
        Console.WriteLine("PASS " + label);
    }
    private static bool Prefix(Type type, params object[] args) => (bool)type.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    private static void At(float x, float y) => Input.mousePosition = new Vector2(x, Screen.height - y);
    private static void Main()
    {
        var harmony = new Harmony(Plugin.ModGUID);
        harmony.CreateClassProcessor(typeof(CatPatchAdaptiveOverlayGuard)).Patch();
        harmony.CreateClassProcessor(typeof(CatPatchAdaptiveClickGuard)).Patch();
        var getter = AccessTools.PropertyGetter(typeof(AdaptiveButton), "overlayActive");
        Check(AccessTools.Method(typeof(AdaptiveButton), "overlayActive") == null, "old method-name search misses the actual property getter");
        Check(getter.Name == "get_overlayActive" && Harmony.GetPatchInfo(getter).Postfixes.Any(p => p.owner == Plugin.ModGUID), "real Harmony patch is installed on property getter");
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(AdaptiveButton), "Clicked")).Prefixes.Any(p => p.owner == Plugin.ModGUID), "real Harmony action guard is installed");
        var button = new AdaptiveButton();
        Input.MouseDown = true;
        CatPatchHostSettingsMenu.IsWindowOpen = true;
        At(15, 15);
        Event.current = new Event { type = EventType.Repaint, rawType = EventType.Repaint, mousePosition = new Vector2(80, 80) };
        Check(button.overlayActive, "live pointer blocks main-menu getter despite stale GUI pointer outside");
        button.PollRawMouse();
        Check(button.Actions == 0, "raw mouse-down polling cannot trigger covered menu button");
        button.Clicked();
        Check(button.Actions == 0, "direct action invocation is blocked even if hover gate was bypassed");
        At(50, 50);
        Event.current.mousePosition = new Vector2(15, 15);
        Check(!button.overlayActive, "stale GUI pointer inside does not block live pointer outside");
        button.PollRawMouse();
        Check(button.Actions == 1, "outside raw mouse input remains usable with settings open");
        CatPatchHostSettingsMenu.WindowRect = new Rect(40, 40, 30, 30);
        Check(button.overlayActive, "dragged settings bounds immediately follow the window");
        button.Clicked();
        Check(button.Actions == 1, "covered action stays blocked after moving window over it");
        CatPatchHostSettingsMenu.IsWindowOpen = false;
        CatPatchUiInput.HoldSettingsClosingInput(CatPatchHostSettingsMenu.WindowRect);
        Check(CatPatchUiInput.IsSettingsInputCaptured(), "closing settings retains capture over previous bounds");
        At(15, 15);
        Check(!CatPatchUiInput.IsSettingsInputCaptured(), "closing settings does not block the rest of the screen");
        Time.frameCount += 2;
        At(50, 50);
        Check(!CatPatchUiInput.IsSettingsInputCaptured(), "closed rectangle expires without sticky input lock");
        CatPatchHostSettingsMenu.WindowRect = new Rect(0, 0, 30, 30);
        Input.MouseDown = false;
        Event.current = null;
        Check(!CatPatchConsoleGuard.IsGameInputUiActive(), "ordinary gameplay shortcuts remain available");
        UIBullshit.MenuOpen = true;
        Check(CatPatchConsoleGuard.IsGameInputUiActive(), "multiplayer bug-report menu captures spray shortcuts");
        UIBullshit.MenuOpen = false;
        Chat.CHAT_textbox_input_focused = true;
        Check(CatPatchConsoleGuard.IsGameInputUiActive(), "chat typing remains protected");
        Chat.CHAT_textbox_input_focused = false;
        var events = EventSystem.current = new EventSystem();
        var textObject = new GameObject();
        var tmp = new TMP_InputField { isFocused = true };
        textObject.Components[typeof(TMP_InputField)] = tmp;
        events.currentSelectedGameObject = textObject;
        Check(CatPatchConsoleGuard.IsGameInputUiActive(), "focused TMP input captures shortcuts");
        int lookups = textObject.ComponentLookups;
        CatPatchConsoleGuard.IsGameInputUiActive();
        Check(textObject.ComponentLookups == lookups, "focused components remain cached");
        tmp.isActiveAndEnabled = false;
        Check(!CatPatchConsoleGuard.IsGameInputUiActive(), "disabled field does not trap input");
        events.currentSelectedGameObject = null;
        ConsoleScript.instance = new ConsoleScript { active = true };
        Check(CatPatchConsoleGuard.IsGameInputUiActive(), "console guard is preserved");
        ConsoleScript.instance.active = false;
        PlayerCamera.main = new PlayerCamera { craftingPanel = new GameObject() };
        Check(CatPatchConsoleGuard.IsGameInputUiActive(), "crafting guard is preserved");
        PlayerCamera.main.craftingPanel.activeInHierarchy = false;

        foreach (bool popup in new[] { false, true })
        {
            CatPatchHostSettingsMenu.IsWindowOpen = !popup;
            CatPatchUpdater.IsPopupOpen = popup;
            At(15, 15);
            string name = popup ? "updater" : "settings";
            Check(button.overlayActive, name + " blocks the actual patched property getter");
            int before = button.Actions;
            button.Clicked();
            Check(button.Actions == before, name + " blocks final action with real Harmony prefix");
            foreach (var type in new[] { EventType.MouseDown, EventType.MouseUp, EventType.MouseDrag, EventType.ScrollWheel, EventType.ContextClick, EventType.KeyDown, EventType.KeyUp })
            {
                GUI.enabled = true;
                Event.current = new Event { type = type, rawType = type, mousePosition = new Vector2(15, 15) };
                Check(!CatPatchUiInput.BeginBackgroundGui() && Event.current.type == EventType.Used && GUI.enabled, name + " consumes inside " + type + " without global disable");
            }
            foreach (var type in new[] { EventType.Layout, EventType.Repaint })
            {
                Event.current = new Event { type = type, rawType = type, mousePosition = new Vector2(15, 15) };
                Check(CatPatchUiInput.BeginBackgroundGui() && Event.current.type == type && GUI.enabled, name + " preserves " + type);
            }
            Event.current = new Event { type = EventType.MouseDown, rawType = EventType.MouseDown, mousePosition = new Vector2(50, 50) };
            Check(CatPatchUiInput.BeginBackgroundGui() && Event.current.type == EventType.MouseDown, name + " leaves outside GUI events available");
            At(50, 50);
            Check(!CatPatchUiInput.IsPointerInputCaptured(), name + " leaves outside live pointer available");
            var rays = new List<RaycastResult> { new() };
            Check(Prefix(typeof(CatPatchWindowCanvasInputPatch), rays) && rays.Count == 1, name + " keeps outside Canvas raycasts");
            At(15, 15);
            Check(!Prefix(typeof(CatPatchWindowCanvasInputPatch), rays) && rays.Count == 0, name + " blocks covered Canvas raycasts");
            var camera = new PlayerCamera { body = new Body { moveDir = new Vector2(1, 1), forceWalk = true } };
            if (popup) Check(Prefix(typeof(CatPatchModalPlayerInputPatch), camera), "updater still does not freeze movement");
            else Check(!Prefix(typeof(CatPatchModalPlayerInputPatch), camera) && camera.body.moveDir.x == 0, "settings captures local keys only over its window");
            At(50, 50);
            Check(Prefix(typeof(CatPatchModalPlayerInputPatch), camera), name + " permits movement outside");
        }
        CatPatchUpdater.IsPopupOpen = false;
        CatPatchHostSettingsMenu.IsWindowOpen = false;
        CatPatchHostSettingsMenu.ShowButton = true;
        At(75, 75);
        Check(button.overlayActive, "CP toggle blocks covered native menu button before opening");
        CatPatchHostSettingsMenu.ShowButton = false;
        At(15, 15);
        CatPatchUiInput.HoldUpdaterClosingInput(CatPatchUpdater.PopupRect);
        Check(CatPatchUiInput.IsPointerInputCaptured(), "updater closing click remains captured");
        At(50, 50);
        Check(!CatPatchUiInput.IsPointerInputCaptured(), "updater closing capture remains rectangle-only");
        Time.frameCount += 3;
        Check(!CatPatchUiInput.IsPointerInputCaptured(), "all closing guards expire");
        Console.WriteLine($"INPUT_CAPTURE_CHECKS_COMPLETE passed={_passed} failed=0");
    }
}
