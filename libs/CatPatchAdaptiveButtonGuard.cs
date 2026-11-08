using HarmonyLib;

namespace CatPatchspace
{
    // overlayActive is a property. Searching for a method named overlayActive
    // silently skipped the guard; its actual method name is get_overlayActive.
    [HarmonyPatch(typeof(AdaptiveButton), "overlayActive", MethodType.Getter)]
    internal static class CatPatchAdaptiveOverlayGuard
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(AdaptiveButton __instance, ref bool __result)
        {
            bool blocked = CatPatchUiInput.IsPointerInputCaptured() || CatPatchHostModerationMenu.IsPointerOverWindow();
            if (blocked) __result = true;
        }
    }

    // Update polls Input.GetMouseButtonDown and invokes Clicked directly, without
    // going through EventSystem or IMGUI. Guard the action boundary as well.
    [HarmonyPatch(typeof(AdaptiveButton), "Clicked")]
    internal static class CatPatchAdaptiveClickGuard
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(AdaptiveButton __instance)
        {
            bool blocked = CatPatchUiInput.IsPointerInputCaptured() || CatPatchHostModerationMenu.IsPointerOverWindow();
            return !blocked;
        }
    }
}
