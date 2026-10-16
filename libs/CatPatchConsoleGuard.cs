using UnityEngine;
using System;
using System.Reflection;

namespace CatPatchspace
{
    internal static class CatPatchConsoleGuard
    {
        private static bool _searchedForConMethod;
        private static MethodInfo _conIsConsoleOpenMethod;

        public static bool IsGameInputUiActive()
        {
            return IsConsoleActive() || IsCraftingMenuActive();
        }

        private static bool IsConsoleActive()
        {
            bool consoleOpen;
            if (TryGetConIsConsoleOpen(out consoleOpen))
                return consoleOpen;

            try
            {
                ConsoleScript console = ConsoleScript.instance;
                return console != null && console.active && console.gameObject.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCraftingMenuActive()
        {
            try
            {
                PlayerCamera camera = PlayerCamera.main;
                if (camera == null || camera.craftingPanel == null)
                    return false;

                return camera.craftingPanel.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetConIsConsoleOpen(out bool consoleOpen)
        {
            consoleOpen = false;

            try
            {
                if (_searchedForConMethod)
                {
                    if (_conIsConsoleOpenMethod == null)
                        return false;

                    consoleOpen = (bool)_conIsConsoleOpenMethod.Invoke(null, null);
                    return true;
                }

                _searchedForConMethod = true;

                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
                {
                    Type[] types;
                    try
                    {
                        types = assemblies[assemblyIndex].GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types;
                    }

                    if (types == null)
                        continue;

                    for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                    {
                        Type type = types[typeIndex];
                        if (type == null || type.Name != "Con")
                            continue;

                        MethodInfo method = type.GetMethod(
                            "IsConsoleOpen",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                        if (method == null || method.ReturnType != typeof(bool))
                            continue;

                        _conIsConsoleOpenMethod = method;
                        consoleOpen = (bool)_conIsConsoleOpenMethod.Invoke(null, null);
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }
    }
}
