using HarmonyLib;
using System;
using System.Text;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace OptiTime
{
    public static class RegistryPlaceholderOptimization
    {
        private const int MaxValidationCalls = 64;

        private static bool initialized;
        private static bool available;
        private static int validationCalls;
        private static int runtimeDisabled;
        private static Action<string> logger;

        public static string InitializationError { get; private set; }

        public static void SetLogger(Action<string> loggerCallback)
        {
            logger = loggerCallback;
        }

        public static bool TryInitialize()
        {
            if (initialized) return available;
            initialized = true;

            try
            {
                validationCalls = 0;
                runtimeDisabled = 0;
                available = true;
                InitializationError = null;
            }
            catch (Exception ex)
            {
                available = false;
                InitializationError = ex.Message;
            }

            return available;
        }

        public static void Cleanup()
        {
            initialized = false;
            available = false;
            validationCalls = 0;
            runtimeDisabled = 0;
            InitializationError = null;
            logger = null;
        }

        // Defensive single-pass replacement for RegistryObject.FillPlaceHolder(string, OrderedDictionary<string, string>).
        public static bool FillPlaceHolder_Prefix(string input, OrderedDictionary<string, string> searchReplace, ref string __result)
        {
            if (!available || Volatile.Read(ref runtimeDisabled) != 0) return true;
            if (searchReplace == null) return true;
            if (string.IsNullOrEmpty(input) || searchReplace.Count == 0)
            {
                __result = input;
                return false;
            }

            if (input.IndexOf('{') < 0)
            {
                __result = input;
                return false;
            }

            if (!CanOptimize(searchReplace)) return true;

            try
            {
                string optimized = ReplacePlaceholdersSinglePass(input, searchReplace);

                int validationSample = Interlocked.Increment(ref validationCalls);
                if (validationSample <= MaxValidationCalls)
                {
                    string baseline = ComputeVanillaBaseline(input, searchReplace);
                    if (!string.Equals(optimized, baseline, StringComparison.Ordinal))
                    {
                        DisableAfterError("Validation mismatch against vanilla placeholder replacement.");
                        __result = baseline;
                        return false;
                    }
                }

                __result = optimized;
                return false;
            }
            catch (Exception ex)
            {
                DisableAfterError($"{ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }

        private static string ReplacePlaceholdersSinglePass(string input, OrderedDictionary<string, string> searchReplace)
        {
            ReadOnlySpan<char> inputSpan = input.AsSpan();
            if (inputSpan.IndexOf('{') < 0) return input;

            StringBuilder sb = new StringBuilder(input.Length);
            int pos = 0;

            while (pos < inputSpan.Length)
            {
                int startOffset = inputSpan[pos..].IndexOf('{');
                if (startOffset < 0)
                {
                    sb.Append(inputSpan[pos..]);
                    break;
                }

                int start = pos + startOffset;
                sb.Append(inputSpan[pos..start]);

                int closeOffset = inputSpan[(start + 1)..].IndexOf('}');
                if (closeOffset < 0)
                {
                    sb.Append(inputSpan[start..]);
                    break;
                }

                int close = start + 1 + closeOffset;
                ReadOnlySpan<char> placeholder = inputSpan[(start + 1)..close];

                if (placeholder.IndexOf('{') >= 0)
                {
                    // Keep searching from the next position to preserve inner-placeholder behavior.
                    sb.Append('{');
                    pos = start + 1;
                    continue;
                }

                if (TryResolvePlaceholder(placeholder, searchReplace, out string replacement))
                {
                    sb.Append(replacement);
                }
                else
                {
                    sb.Append(inputSpan[start..(close + 1)]);
                }

                pos = close + 1;
            }

            return sb.ToString();
        }

        private static bool TryResolvePlaceholder(ReadOnlySpan<char> placeholder, OrderedDictionary<string, string> searchReplace, out string value)
        {
            foreach (var kvp in searchReplace)
            {
                ReadOnlySpan<char> key = kvp.Key.AsSpan();
                int partStart = 0;

                for (int i = 0; i <= placeholder.Length; i++)
                {
                    if (i < placeholder.Length && placeholder[i] != '|')
                    {
                        continue;
                    }

                    if (placeholder[partStart..i].SequenceEqual(key))
                    {
                        value = kvp.Value;
                        return true;
                    }

                    partStart = i + 1;
                }
            }

            value = null;
            return false;
        }

        private static bool CanOptimize(OrderedDictionary<string, string> searchReplace)
        {
            foreach (var kvp in searchReplace)
            {
                if (kvp.Key == null || kvp.Value == null) return false;
                if (kvp.Key.Length == 0) return false;
                if (ContainsRegexMetaChars(kvp.Key)) return false;
            }

            return true;
        }

        private static bool ContainsRegexMetaChars(string key)
        {
            for (int i = 0; i < key.Length; i++)
            {
                switch (key[i])
                {
                    case '\\':
                    case '.':
                    case '^':
                    case '$':
                    case '|':
                    case '?':
                    case '*':
                    case '+':
                    case '(':
                    case ')':
                    case '[':
                    case ']':
                    case '{':
                    case '}':
                        return true;
                }
            }

            return false;
        }

        private static string ComputeVanillaBaseline(string input, OrderedDictionary<string, string> searchReplace)
        {
            string result = input;
            foreach (var kvp in searchReplace)
            {
                result = RegistryObject.FillPlaceHolder(result, kvp.Key, kvp.Value);
            }
            return result;
        }

        private static void DisableAfterError(string reason)
        {
            if (Interlocked.CompareExchange(ref runtimeDisabled, 1, 0) != 0) return;

            available = false;
            InitializationError = reason;

            try
            {
                logger?.Invoke($"[OptiTime] Registry placeholder optimization disabled at runtime: {reason}");
            }
            catch
            {
                // Fail-open logging path.
            }
        }
    }
}
