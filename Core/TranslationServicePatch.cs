using HarmonyLib;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Config;

namespace OptiTime
{
    /// <summary>
    /// Eliminates a thread-safety bug in <see cref="Vintagestory.API.Config.TranslationService.HasTranslation"/>.
    ///
    /// Vanilla declares <c>notFound</c> as a plain <see cref="HashSet{T}"/> at
    /// <c>TranslationService.cs:26</c> and writes to it from the main thread at
    /// <c>TranslationService.cs:555</c> as well as from background threads via
    /// <c>CreativeTab.CreateSearchCache</c> running on <c>TyronThreadPool</c>. The set
    /// is referenced exactly once in vanilla — at the call site that gates the
    /// <c>"Lang key not found"</c> verbose log message. It is a log-deduplication set,
    /// NOT a translation-result cache (translation results are determined by
    /// <c>entryCache</c>, <c>wildcardCache</c>, and <c>regexCache</c> at lines 540/550/553).
    ///
    /// The transpiler rewrites the <c>HashSet&lt;string&gt;.Add</c> call to
    /// <see cref="SafeAdd"/>, which writes to a static <see cref="ConcurrentDictionary{TKey, TValue}"/>.
    /// The instance <c>HashSet</c> argument is discarded.
    ///
    /// Behavioural notes (intentional, not bugs):
    /// - Scope change: vanilla deduplicates per <c>TranslationService</c> instance (one set per
    ///   loaded language). The patch deduplicates globally across all instances because
    ///   <see cref="safeNotFound"/> is static. With multiple language packs loaded, this
    ///   reduces duplicate "Lang key not found" log lines from one-per-pack to one-total.
    /// - <see cref="safeNotFound"/> grows monotonically until <see cref="Cleanup"/> runs. In
    ///   practice it is bounded by the small set of unique missing keys observed.
    ///
    /// Upstream: race condition should ideally be fixed in vanilla (TranslationService.notFound
    /// becoming a ConcurrentDictionary or being protected by a lock). Once vanilla ships such
    /// a fix and the mod's <c>modinfo.json</c> game floor is raised past it, this patch can
    /// be retired.
    /// </summary>
    internal static class TranslationServicePatch
    {
        private static readonly ConcurrentDictionary<string, byte> safeNotFound = new();

        internal static void Cleanup()
        {
            safeNotFound.Clear();
        }

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
