using HarmonyLib;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Config;

namespace OptiTime
{
    /// <summary>
    /// Fixes a vanilla thread-safety bug in TranslationService.HasTranslation.
    /// The notFound field is a plain HashSet&lt;string&gt; but gets written from
    /// background threads (CreativeTab.CreateSearchCache via TyronThreadPool)
    /// while the main thread reads/writes it concurrently.
    /// Transpiler replaces HashSet.Add with a ConcurrentDictionary.TryAdd wrapper.
    /// </summary>
    internal static class TranslationServicePatch
    {
        private static readonly ConcurrentDictionary<string, byte> safeNotFound = new();

        internal static bool SafeAdd(HashSet<string> _, string key)
        {
            return safeNotFound.TryAdd(key, 0);
        }

        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var hashSetAdd = AccessTools.Method(typeof(HashSet<string>), nameof(HashSet<string>.Add));
            var safeAddMethod = AccessTools.Method(typeof(TranslationServicePatch), nameof(SafeAdd));

            foreach (var instr in instructions)
            {
                if (instr.Calls(hashSetAdd))
                {
                    yield return new CodeInstruction(OpCodes.Call, safeAddMethod);
                }
                else
                {
                    yield return instr;
                }
            }
        }
    }
}
