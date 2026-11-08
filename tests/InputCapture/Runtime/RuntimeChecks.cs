using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

[BepInPlugin("meow.catpatch.inputchecks", "CatPatch Input Capture Checks", "1.0.0")]
[BepInDependency("meow.catpatch")]
public sealed class InputCaptureRuntimeChecks : BaseUnityPlugin
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private Assembly _patch;
    private object _updater;
    private int _passed, _failed;
    private static bool? _actionRan;
    private void Awake()
    {
        _patch = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "CatPatch");
        _updater = Type("CatPatchUpdater").GetProperty("Instance", All).GetValue(null);
        Set(_updater, "_startupCheckStarted", true);
    }
    private IEnumerator Start()
    {
        yield return new WaitForSecondsRealtime(3);
        try { Run(); }
        catch (Exception ex) { _failed++; Logger.LogError(ex.ToString()); }
        Logger.LogInfo($"INPUT_CAPTURE_RUNTIME_COMPLETE passed={_passed} failed={_failed}");
    }
    private static void ObserveAction(bool __runOriginal) => _actionRan = __runOriginal;
    private void Run()
    {
        var settings = Type("CatPatchHostSettingsMenu");
        object savedOpen = settings.GetField("_windowOpen", All).GetValue(null);
        object savedSettingsRect = settings.GetField("_windowRect", All).GetValue(null);
        string[] fields = { "_manifest", "_updateAvailable", "_showStartupPopup", "_popupRect" };
        object[] saved = fields.Select(f => Get(_updater, f)).ToArray();
        Event savedEvent = Event.current;
        var observer = new Harmony("meow.catpatch.inputchecks.actionobserver");
        GameObject target = null, raysObject = null, eventObject = null;
        try
        {
            var game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
            var buttonType = game.GetType("AdaptiveButton", true);
            var getter = AccessTools.PropertyGetter(buttonType, "overlayActive");
            var clicked = AccessTools.Method(buttonType, "Clicked");
            Check(Harmony.GetPatchInfo(getter)?.Postfixes.Any(p => p.owner == "meow.catpatch") == true, "installed game getter has CatPatch postfix");
            Check(Harmony.GetPatchInfo(clicked)?.Prefixes.Any(p => p.owner == "meow.catpatch") == true, "installed game action has CatPatch prefix");
            observer.Patch(clicked, postfix: new HarmonyMethod(typeof(InputCaptureRuntimeChecks).GetMethod("ObserveAction", All)));
            target = new GameObject("Safe AdaptiveButton raw-action probe");
            target.SetActive(false); // Never run its normal Update or Start.
            object button = target.AddComponent(buttonType);
            // An out-of-range action is a no-op in installed Clicked's switch.
            // The real Quit/Start/Settings actions are never selected by this probe.
            var action = buttonType.GetField("action", All);
            action.SetValue(button, Enum.ToObject(action.FieldType, 999));
            Vector2 live = (Vector2)Type("CatPatchUiInput").GetMethod("GetLiveGuiPointerPosition", All).Invoke(null, null);
            Rect near = new Rect(live.x - 60, live.y - 60, 120, 120);
            Rect far = new Rect(live.x + 500, live.y + 500, 120, 120);
            object manifest = Activator.CreateInstance(Type("CatPatchUpdateManifest"), true);
            manifest.GetType().GetProperty("LatestVersion").SetValue(manifest, "99.0.0");
            Set(_updater, "_manifest", manifest);
            Set(_updater, "_updateAvailable", true);
            Set(_updater, "_showStartupPopup", false);
            settings.GetField("_windowOpen", All).SetValue(null, true);
            settings.GetField("_windowRect", All).SetValue(null, near);
            Event.current = new Event { type = EventType.Repaint, mousePosition = far.center };
            Check((bool)getter.Invoke(button, null), "settings rectangle forces installed overlay getter despite stale GUI pointer");
            Click(clicked, button, false, "settings blocks actual installed Clicked body");
            settings.GetField("_windowRect", All).SetValue(null, far);
            Click(clicked, button, true, "settings moved away permits harmless installed action");
            settings.GetField("_windowRect", All).SetValue(null, near);
            Click(clicked, button, false, "dragged settings rectangle blocks installed action again");
            settings.GetField("_windowOpen", All).SetValue(null, false);
            Set(_updater, "_showStartupPopup", true);
            Set(_updater, "_popupRect", near);
            Check((bool)getter.Invoke(button, null), "updater rectangle forces installed overlay getter");
            Click(clicked, button, false, "updater blocks actual installed Clicked body");
            Set(_updater, "_popupRect", far);
            Click(clicked, button, true, "updater moved away permits harmless installed action");
            var movement = Type("CatPatchModalPlayerInputPatch").GetMethod("Prefix", All);
            Check((bool)movement.Invoke(null, new object[] { null }), "window outside pointer does not suspend player input");
            if (EventSystem.current == null)
            {
                eventObject = new GameObject("Input check event system");
                eventObject.AddComponent<EventSystem>();
            }
            raysObject = new GameObject("Safe background raycast probe");
            var raycaster = raysObject.AddComponent<SafeBackgroundRaycaster>();
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current), hits);
            Check(hits.Any(h => h.gameObject == raysObject), "outside-window Canvas raycast remains available");
            int calls = raycaster.Calls;
            Set(_updater, "_popupRect", near);
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current), hits);
            Check(hits.Count == 0 && raycaster.Calls == calls, "inside-window Canvas raycast is blocked");
            var plugin = BepInEx.Bootstrap.Chainloader.PluginInfos["meow.catpatch"].Instance;
            object spray = plugin.GetComponent(Type("SprayController"));
            Set(spray, "_sprayKeyHeld", true);
            Set(spray, "_radialOpen", true);
            spray.GetType().GetMethod("HandleSprayInput", All).Invoke(spray, null);
            Check(!(bool)Get(spray, "_sprayKeyHeld") && !(bool)Get(spray, "_radialOpen"), "covered menu cancels actual held spray input");
        }
        finally
        {
            observer.UnpatchSelf();
            settings.GetField("_windowOpen", All).SetValue(null, savedOpen);
            settings.GetField("_windowRect", All).SetValue(null, savedSettingsRect);
            for (int i = 0; i < fields.Length; i++) Set(_updater, fields[i], saved[i]);
            if (savedEvent != null) Event.current = savedEvent;
            if (target != null) Destroy(target);
            if (raysObject != null) Destroy(raysObject);
            if (eventObject != null) Destroy(eventObject);
        }
    }
    private void Click(MethodInfo method, object target, bool expected, string label)
    {
        _actionRan = null;
        method.Invoke(target, null);
        Check(_actionRan.HasValue && _actionRan.Value == expected, label);
    }
    private Type Type(string name) => _patch.GetType("CatPatchspace." + name, true);
    private static object Get(object target, string name) => target.GetType().GetField(name, All).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, All).SetValue(target, value);
    private void Check(bool ok, string label)
    {
        if (ok) { _passed++; Logger.LogInfo("PASS " + label); }
        else { _failed++; Logger.LogError("FAIL " + label); }
    }
}
public sealed class SafeBackgroundRaycaster : BaseRaycaster
{
    public int Calls;
    public override Camera eventCamera => null;
    public override void Raycast(PointerEventData eventData, List<RaycastResult> results)
    {
        Calls++;
        results.Add(new RaycastResult { gameObject = gameObject, module = this });
    }
}