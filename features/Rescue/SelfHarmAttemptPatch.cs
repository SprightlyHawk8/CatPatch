using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace CatPatchspace
{
    [HarmonyPatch(typeof(SelfHarmer), nameof(SelfHarmer.AttemptHarm))]
    internal static class SelfHarmAttemptPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var selfHarm = AccessTools.Method(typeof(SelfHarmer), "SelfHarm");
            var suicide = AccessTools.Method(typeof(SelfHarmer), "Suicide");
            var rescueSelfHarm = AccessTools.Method(typeof(SelfHarmerSelfHarmCatPatch), "CreateRoutine");
            var rescueSuicide = AccessTools.Method(typeof(SelfHarmerSuicideCatPatch), "CreateRoutine");
            var result = new List<CodeInstruction>(instructions);
            int harmCalls = 0;
            int suicideCalls = 0;

            // Together patches AttemptHarm before CatPatch loads. Mono can inline the
            // tiny iterator factories into that caller before our factory prefixes
            // exist. Rewrite the caller itself so it always enters rescue tracking.
            // Keep AttemptHarm's branches and Together's authority/plushie relay intact.
            foreach (var instruction in result)
            {
                if (instruction.Calls(selfHarm))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = rescueSelfHarm;
                    harmCalls++;
                }
                else if (instruction.Calls(suicide))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = rescueSuicide;
                    suicideCalls++;
                }
            }

            if (harmCalls != 1 || suicideCalls != 1)
                throw new InvalidOperationException("Self-harm rescue expected one SelfHarm and one Suicide call in AttemptHarm.");

            return result;
        }
    }
}
