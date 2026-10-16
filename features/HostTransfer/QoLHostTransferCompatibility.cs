using System;
using System.Reflection;
using HarmonyLib;

namespace CatPatchspace
{
    internal sealed class QoLSeedState
    {
        public bool QoLPresent;
        public bool IsSeeded;
        public int Seed;
        public string Input = "";

        public string Describe()
        {
            return "present=" + QoLPresent +
                   " seeded=" + IsSeeded +
                   " seed=" + (IsSeeded ? Seed.ToString() : "<none>") +
                   " inputChars=" + (Input == null ? 0 : Input.Length);
        }
    }

    /// <summary>
    /// Keeps QoL Unknown's single-player slot and seeded-run state from
    /// replacing a verified Together save while a promoted v5 host reloads it.
    /// QoL is optional and is resolved entirely through reflection.
    /// </summary>
    internal static class QoLHostTransferCompatibility
    {
        private const string QoLSaveManagerTypeName = "QoL_Unknown.SaveManager";
        private const string QoLSavePatcherTypeName = "QoL_Unknown.SaveSystemPatcher";
        private const string QoLMultiplayerLoadTypeName = "QoL_Unknown.MultiplayerSaveLoadPatcher";
        private const string QoLSeedManagerTypeName = "QoL_Unknown.SeedManager";
        private const string QoLSeededRunPatcherTypeName = "QoL_Unknown.SeededRunPatcher";

        private static readonly BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly Harmony Harmony = new Harmony(Plugin.ModGUID + ".qol-host-transfer");

        private static bool _patchesAttempted;
        private static bool _migrationLoadActive;
        private static bool _suppressQoLSeededWorldgen = true;
        private static FieldInfo _currentSaveFileField;
        private static string _previousSaveFile;

        public static QoLSeedState CaptureSourceSeedState()
        {
            QoLSeedState state = new QoLSeedState();
            try
            {
                Type saveManager = FindType(QoLSaveManagerTypeName);
                Type seedManager = FindType(QoLSeedManagerTypeName);
                state.QoLPresent = saveManager != null && seedManager != null;
                if (!state.QoLPresent)
                    return state;

                FieldInfo isSeeded = seedManager.GetField("IsSeeded", StaticFlags);
                FieldInfo currentSeed = seedManager.GetField("CurrentSeed", StaticFlags);
                FieldInfo inputString = seedManager.GetField("InputString", StaticFlags);
                if (isSeeded == null || currentSeed == null || inputString == null)
                    throw new MissingFieldException("QoL Unknown seed state surface changed.");

                state.IsSeeded = (bool)isSeeded.GetValue(null);
                state.Seed = (int)currentSeed.GetValue(null);
                state.Input = inputString.GetValue(null) as string ?? "";
                if (state.IsSeeded && state.Seed == 0)
                    state.IsSeeded = false;
            }
            catch (Exception ex)
            {
                state.IsSeeded = false;
                state.Seed = 0;
                state.Input = "";
                LogWarning("QoL source seed state could not be captured: " + Unwrap(ex).Message);
            }
            return state;
        }

        public static void BeginTransferredMultiplayerLoad(string transferId, QoLSeedState sourceSeedState)
        {
            EndTransferredMultiplayerLoad(transferId, false);

            Type saveManager = FindType(QoLSaveManagerTypeName);
            if (saveManager == null)
            {
                if (sourceSeedState != null && sourceSeedState.IsSeeded)
                    LogWarning("source world uses QoL seed " + sourceSeedState.Seed +
                               " but QoL is unavailable locally id=" + transferId +
                               "; exact terrain regeneration is not expected");
                return;
            }

            try
            {
                EnsureSeedGuardsPatched();

                _currentSaveFileField = saveManager.GetField("CurrentSaveFile", StaticFlags);
                if (_currentSaveFileField == null || _currentSaveFileField.FieldType != typeof(string))
                    throw new MissingFieldException(QoLSaveManagerTypeName, "CurrentSaveFile");

                _previousSaveFile = _currentSaveFileField.GetValue(null) as string;
                _currentSaveFileField.SetValue(null, "save.sv");
                ClearStaleQoLLoadState();
                _migrationLoadActive = true;
                _suppressQoLSeededWorldgen = sourceSeedState == null || !sourceSeedState.IsSeeded;

                bool seedApplied = false;
                if (!_suppressQoLSeededWorldgen)
                {
                    try
                    {
                        ApplySourceSeedState(sourceSeedState);
                        seedApplied = true;
                    }
                    catch (Exception ex)
                    {
                        _suppressQoLSeededWorldgen = true;
                        LogWarning("QoL source seed could not be applied id=" + transferId + ": " +
                                   Unwrap(ex).Message);
                    }
                }

                LogInfo("QoL migration guard enabled id=" + transferId +
                        " previousSlot=" + FormatSlot(_previousSaveFile) +
                        " forcedSlot=save.sv seededWorldgen=" +
                        (_suppressQoLSeededWorldgen ? "suppressed" : "source") +
                        " sourceSeed=" + (seedApplied ? sourceSeedState.Seed.ToString() : "<none>"));
            }
            catch (Exception ex)
            {
                _migrationLoadActive = false;
                _suppressQoLSeededWorldgen = true;
                if (_currentSaveFileField != null)
                {
                    try
                    {
                        _currentSaveFileField.SetValue(null, _previousSaveFile ?? "save.sv");
                    }
                    catch
                    {
                        // Preserve the original compatibility failure below.
                    }
                }
                _currentSaveFileField = null;
                _previousSaveFile = null;
                LogWarning("QoL migration guard could not be enabled id=" + transferId + ": " +
                           Unwrap(ex).Message);
            }
        }

        public static void EndTransferredMultiplayerLoad(string transferId)
        {
            EndTransferredMultiplayerLoad(transferId, true);
        }

        private static void EndTransferredMultiplayerLoad(string transferId, bool log)
        {
            bool wasActive = _migrationLoadActive;
            _migrationLoadActive = false;
            _suppressQoLSeededWorldgen = true;

            if (_currentSaveFileField != null)
            {
                try
                {
                    object current = _currentSaveFileField.GetValue(null);
                    if (string.Equals(current as string, "save.sv", StringComparison.OrdinalIgnoreCase))
                        _currentSaveFileField.SetValue(null, _previousSaveFile ?? "save.sv");
                }
                catch (Exception ex)
                {
                    LogWarning("QoL save-slot selection could not be restored id=" + transferId + ": " +
                               Unwrap(ex).Message);
                }
            }

            _currentSaveFileField = null;
            _previousSaveFile = null;
            if (wasActive && log)
                LogInfo("QoL migration guard released id=" + transferId);
        }

        private static void EnsureSeedGuardsPatched()
        {
            if (_patchesAttempted)
                return;
            _patchesAttempted = true;

            try
            {
                Type seedManager = FindType(QoLSeedManagerTypeName);
                Type seededRunPatcher = FindType(QoLSeededRunPatcherTypeName);
                if (seedManager == null || seededRunPatcher == null)
                    throw new TypeLoadException("QoL Unknown seeded-run compatibility types were not found.");

                MethodInfo ensureSeed = seedManager.GetMethod("EnsureNonZeroSeedForLoadedRun", StaticFlags,
                    null, Type.EmptyTypes, null);
                MethodInfo shouldUseSeed = seededRunPatcher.GetMethod("ShouldUseQoLSeededWorldgen", StaticFlags,
                    null, Type.EmptyTypes, null);
                MethodInfo suppressSeedMigration = typeof(QoLHostTransferCompatibility).GetMethod(
                    nameof(SuppressQoLSeedMigration), StaticFlags);
                MethodInfo suppressSeededWorldgen = typeof(QoLHostTransferCompatibility).GetMethod(
                    nameof(SuppressQoLSeededWorldgen), StaticFlags);

                if (ensureSeed == null || shouldUseSeed == null ||
                    suppressSeedMigration == null || suppressSeededWorldgen == null)
                    throw new MissingMethodException("QoL Unknown seeded-run compatibility surface changed.");

                Harmony.Patch(ensureSeed, prefix: new HarmonyMethod(suppressSeedMigration));
                Harmony.Patch(shouldUseSeed, prefix: new HarmonyMethod(suppressSeededWorldgen));
            }
            catch (Exception ex)
            {
                // Slot isolation is the critical fix and remains active even if
                // a future QoL version changes its optional seeded-run surface.
                LogWarning("QoL seeded-worldgen guard is unavailable: " + Unwrap(ex).Message);
            }
        }

        private static bool SuppressQoLSeedMigration(ref bool __result)
        {
            if (!_migrationLoadActive || !_suppressQoLSeededWorldgen)
                return true;
            __result = false;
            return false;
        }

        private static bool SuppressQoLSeededWorldgen(ref bool __result)
        {
            if (!_migrationLoadActive || !_suppressQoLSeededWorldgen)
                return true;
            __result = false;
            return false;
        }

        private static void ClearStaleQoLLoadState()
        {
            Type savePatcher = FindType(QoLSavePatcherTypeName);
            if (savePatcher != null)
            {
                SetStaticFieldToNull(savePatcher, "PendingLoadPosition");
                SetStaticFieldToNull(savePatcher, "PendingRadLineY");
                SetStaticFieldToNull(savePatcher, "PendingRadLineActive");
                SetStaticFieldToNull(savePatcher, "PendingIGTSeconds");
                SetStaticFieldToNull(savePatcher, "PendingRTASeconds");
            }

            Type multiplayerLoad = FindType(QoLMultiplayerLoadTypeName);
            MethodInfo clearLoad = multiplayerLoad == null
                ? null
                : multiplayerLoad.GetMethod("ClearMultiplayerSlotLoad", StaticFlags,
                    null, Type.EmptyTypes, null);
            if (clearLoad != null)
                clearLoad.Invoke(null, null);
        }

        private static void ApplySourceSeedState(QoLSeedState sourceSeedState)
        {
            if (sourceSeedState == null || !sourceSeedState.IsSeeded || sourceSeedState.Seed == 0)
                throw new InvalidOperationException("The source QoL seed state is not usable.");

            Type seedManager = FindType(QoLSeedManagerTypeName);
            MethodInfo setNetworkSeed = seedManager == null
                ? null
                : seedManager.GetMethod("SetNetworkSeed", StaticFlags, null,
                    new[] { typeof(int), typeof(string) }, null);
            if (setNetworkSeed == null)
                throw new MissingMethodException(QoLSeedManagerTypeName, "SetNetworkSeed");
            setNetworkSeed.Invoke(null, new object[] { sourceSeedState.Seed, sourceSeedState.Input ?? "" });
        }

        private static void SetStaticFieldToNull(Type type, string fieldName)
        {
            FieldInfo field = type.GetField(fieldName, StaticFlags);
            if (field != null)
                field.SetValue(null, null);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                    return type;
            }
            return null;
        }

        private static string FormatSlot(string slot)
        {
            return string.IsNullOrEmpty(slot) ? "<empty>" : slot;
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException && exception.InnerException != null)
                exception = exception.InnerException;
            return exception;
        }

        private static void LogInfo(string message)
        {
            if (Plugin.Logger != null)
                Plugin.Logger.LogInfo("[HostTransfer] " + message);
        }

        private static void LogWarning(string message)
        {
            if (Plugin.Logger != null)
                Plugin.Logger.LogWarning("[HostTransfer] " + message);
        }
    }
}
