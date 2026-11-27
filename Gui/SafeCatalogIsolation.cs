using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace OptiTime
{
    /// <summary>
    /// Isolates per-item collectible failures on client background threads in
    /// CreativeTab.CreateSearchCache and ModSystemSurvivalHandbook.onCreatePagesAsync
    /// so a single broken modded item or shutdown race does not crash the client or
    /// abort handbook loading.
    /// </summary>
    internal static class SafeCatalogIsolation
    {
        private const int MaxWarningsPerPath = 16;

        private static System.Func<ICoreClientAPI, ItemStack, object> pageFactory;
        private static Action<string> logger;
        private static int creativeWarningCount;
        private static int handbookWarningCount;

        internal static void SetLogger(Action<string> log) => logger = log;

        internal static void Cleanup()
        {
            pageFactory = null;
            logger = null;
            Interlocked.Exchange(ref creativeWarningCount, 0);
            Interlocked.Exchange(ref handbookWarningCount, 0);
        }

        internal static bool IsClientDisposed(ICoreAPI api)
        {
            if (api == null)
            {
                return true;
            }

            if (api is ClientCoreAPI clientCoreApi && clientCoreApi.disposed)
            {
                return true;
            }

            if (api is ICoreClientAPI clientApi)
            {
                try
                {
                    if (clientApi.IsShuttingDown)
                    {
                        return true;
                    }
                }
                catch (NullReferenceException)
                {
                    // Test harnesses can construct uninitialized ClientCoreAPI instances with game == null.
                }
            }

            return false;
        }

        internal static bool CreateSearchCache_Prefix(
            CreativeTab __instance,
            IWorldAccessor world,
            ref Dictionary<int, string> __result)
        {
            if (__instance == null || world?.Api == null || world.Api.Side != EnumAppSide.Client)
            {
                return true;
            }

            IInventory inventory = __instance.Inventory;
            if (inventory == null)
            {
                return true;
            }

            int count = inventory.Count;
            var searchCache = new Dictionary<int, string>(Math.Max(0, count));
            var searchCacheNames = new Dictionary<int, string>(Math.Max(0, count));

            for (int i = 0; i < count; i++)
            {
                if (IsClientDisposed(world.Api))
                {
                    break;
                }

                ItemSlot itemSlot = inventory[i];
                ItemStack itemstack = itemSlot?.Itemstack;
                if (itemstack == null)
                {
                    continue;
                }

                string name;
                try
                {
                    name = itemstack.GetName() ?? string.Empty;
                }
                catch (Exception ex)
                {
                    if (IsClientDisposed(world.Api))
                    {
                        break;
                    }

                    string codeFallback = itemstack.Collectible?.Code?.ToString() ?? string.Empty;
                    string normalizedFallback = codeFallback.ToSearchFriendly().ToLowerInvariant();
                    searchCacheNames[i] = normalizedFallback;
                    searchCache[i] = normalizedFallback;
                    LogCreativeWarning(world, itemstack, ex);
                    continue;
                }

                searchCacheNames[i] = name.ToSearchFriendly().ToLowerInvariant();

                try
                {
                    string extra = (itemstack.Collectible as ISearchTextProvider)?.GetSearchText(world, itemSlot)
                        ?? (itemstack.GetDescription(world, itemSlot) ?? string.Empty).ToSearchFriendly().ToLowerInvariant();
                    searchCache[i] = name + " " + extra;
                }
                catch (Exception ex)
                {
                    if (IsClientDisposed(world.Api))
                    {
                        searchCacheNames.Remove(i);
                        break;
                    }

                    searchCache[i] = name;
                    LogCreativeWarning(world, itemstack, ex);
                }
            }

            __instance.SearchCacheNames = searchCacheNames;
            __instance.SearchCache = searchCache;
            __result = searchCache;
            return false;
        }

        internal static object TryCreateHandbookItemStackPage(
            ICoreClientAPI capi,
            ItemStack stack,
            out bool abortLoop)
        {
            if (capi != null && capi.Side != EnumAppSide.Client)
            {
                abortLoop = false;
                return pageFactory(capi, stack);
            }

            if (IsClientDisposed(capi))
            {
                abortLoop = true;
                return null;
            }

            try
            {
                abortLoop = false;
                return pageFactory(capi, stack);
            }
            catch (Exception ex)
            {
                if (capi != null && capi.Side != EnumAppSide.Client)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }

                if (IsClientDisposed(capi))
                {
                    abortLoop = true;
                    return null;
                }

                abortLoop = false;
                LogHandbookWarning(capi, stack, ex);
                return null;
            }
        }

        internal static IEnumerable<CodeInstruction> TranspileCreatePagesAsync(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator il)
        {
            var codes = new List<CodeInstruction>(instructions);
            int ctorIdx = -1;
            ConstructorInfo itemStackPageCtor = null;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj &&
                    codes[i].operand is ConstructorInfo ctor &&
                    ctor.DeclaringType?.FullName == "Vintagestory.GameContent.GuiHandbookItemStackPage")
                {
                    if (ctorIdx >= 0)
                    {
                        logger?.Invoke("[OptiTime] Handbook page exception isolation skipped: multiple page constructors found.");
                        return codes;
                    }

                    ctorIdx = i;
                    itemStackPageCtor = ctor;
                }
            }

            if (ctorIdx < 5 || itemStackPageCtor == null)
            {
                logger?.Invoke("[OptiTime] Handbook page exception isolation skipped: page constructor anchor not found.");
                return codes;
            }

            MethodInfo getIsShuttingDown = AccessTools.PropertyGetter(typeof(ICoreClientAPI), nameof(ICoreClientAPI.IsShuttingDown));
            if (!codes[ctorIdx - 5].Calls(getIsShuttingDown) ||
                (codes[ctorIdx - 4].opcode != OpCodes.Brtrue && codes[ctorIdx - 4].opcode != OpCodes.Brtrue_S) ||
                !(codes[ctorIdx - 4].operand is Label exitLoopLabel))
            {
                logger?.Invoke("[OptiTime] Handbook page exception isolation skipped: loop shutdown anchor not found.");
                return codes;
            }

            int addIdx = -1;
            int searchLimit = Math.Min(codes.Count - 1, ctorIdx + 12);
            for (int i = ctorIdx + 1; i < searchLimit; i++)
            {
                if ((codes[i].opcode == OpCodes.Callvirt || codes[i].opcode == OpCodes.Call) &&
                    codes[i].operand is MethodInfo mi &&
                    mi.Name == "Add" &&
                    mi.DeclaringType != null &&
                    mi.DeclaringType.IsGenericType &&
                    mi.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    addIdx = i;
                    break;
                }
            }

            if (addIdx < 0 || addIdx + 1 >= codes.Count)
            {
                logger?.Invoke("[OptiTime] Handbook page exception isolation skipped: List.Add anchor not found.");
                return codes;
            }

            EnsurePageFactory(itemStackPageCtor);

            MethodInfo isClientDisposedMethod = AccessTools.Method(typeof(SafeCatalogIsolation), nameof(IsClientDisposed), new[] { typeof(ICoreAPI) });
            MethodInfo tryCreateMethod = AccessTools.Method(
                typeof(SafeCatalogIsolation),
                nameof(TryCreateHandbookItemStackPage),
                new[] { typeof(ICoreClientAPI), typeof(ItemStack), typeof(bool).MakeByRefType() });

            codes[ctorIdx - 5].opcode = OpCodes.Call;
            codes[ctorIdx - 5].operand = isClientDisposedMethod;

            LocalBuilder abortLocal = il.DeclareLocal(typeof(bool));
            Label hasPageLabel = il.DefineLabel();
            Label nextIterationLabel = il.DefineLabel();
            codes[addIdx + 1].labels.Add(nextIterationLabel);

            CodeInstruction originalCtor = codes[ctorIdx];
            var firstReplacement = new CodeInstruction(OpCodes.Ldloca, abortLocal);
            firstReplacement.labels.AddRange(originalCtor.labels);
            firstReplacement.blocks.AddRange(originalCtor.blocks);

            var castInstruction = new CodeInstruction(OpCodes.Castclass, itemStackPageCtor.DeclaringType);
            castInstruction.labels.Add(hasPageLabel);

            var replacement = new List<CodeInstruction>
            {
                firstReplacement,
                new CodeInstruction(OpCodes.Call, tryCreateMethod),
                new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Brtrue, hasPageLabel),
                new CodeInstruction(OpCodes.Pop),
                new CodeInstruction(OpCodes.Ldloc, abortLocal),
                new CodeInstruction(OpCodes.Brtrue, exitLoopLabel),
                new CodeInstruction(OpCodes.Br, nextIterationLabel),
                castInstruction
            };

            codes.RemoveAt(ctorIdx);
            codes.InsertRange(ctorIdx, replacement);
            WidenShortBranches(codes);
            return codes;
        }

        private static void EnsurePageFactory(ConstructorInfo ctor)
        {
            if (Volatile.Read(ref pageFactory) != null)
            {
                return;
            }

            ParameterExpression capiParam = Expression.Parameter(typeof(ICoreClientAPI), "capi");
            ParameterExpression stackParam = Expression.Parameter(typeof(ItemStack), "stack");
            Expression body = Expression.Convert(Expression.New(ctor, capiParam, stackParam), typeof(object));
            var compiled = Expression.Lambda<System.Func<ICoreClientAPI, ItemStack, object>>(body, capiParam, stackParam).Compile();
            Interlocked.CompareExchange(ref pageFactory, compiled, null);
        }

        private static void WidenShortBranches(List<CodeInstruction> codes)
        {
            for (int i = 0; i < codes.Count; i++)
            {
                codes[i].opcode = WidenBranchOpcode(codes[i].opcode);
            }
        }

        private static OpCode WidenBranchOpcode(OpCode opcode)
        {
            if (opcode == OpCodes.Br_S) return OpCodes.Br;
            if (opcode == OpCodes.Brtrue_S) return OpCodes.Brtrue;
            if (opcode == OpCodes.Brfalse_S) return OpCodes.Brfalse;
            if (opcode == OpCodes.Beq_S) return OpCodes.Beq;
            if (opcode == OpCodes.Bne_Un_S) return OpCodes.Bne_Un;
            if (opcode == OpCodes.Bge_S) return OpCodes.Bge;
            if (opcode == OpCodes.Bge_Un_S) return OpCodes.Bge_Un;
            if (opcode == OpCodes.Bgt_S) return OpCodes.Bgt;
            if (opcode == OpCodes.Bgt_Un_S) return OpCodes.Bgt_Un;
            if (opcode == OpCodes.Ble_S) return OpCodes.Ble;
            if (opcode == OpCodes.Ble_Un_S) return OpCodes.Ble_Un;
            if (opcode == OpCodes.Blt_S) return OpCodes.Blt;
            if (opcode == OpCodes.Blt_Un_S) return OpCodes.Blt_Un;
            if (opcode == OpCodes.Leave_S) return OpCodes.Leave;
            return opcode;
        }

        private static void LogCreativeWarning(IWorldAccessor world, ItemStack stack, Exception ex)
        {
            if (Interlocked.Increment(ref creativeWarningCount) > MaxWarningsPerPath)
            {
                return;
            }

            string code = stack?.Collectible?.Code?.ToString() ?? "unknown";
            string msg = $"[OptiTime] Creative search skipped details for '{code}' ({ex.GetType().Name}: {ex.Message}); using name only.";
            try
            {
                if (world?.Logger != null)
                {
                    world.Logger.Warning(msg);
                    return;
                }
            }
            catch
            {
            }

            logger?.Invoke(msg);
        }

        private static void LogHandbookWarning(ICoreClientAPI capi, ItemStack stack, Exception ex)
        {
            if (Interlocked.Increment(ref handbookWarningCount) > MaxWarningsPerPath)
            {
                return;
            }

            string code = stack?.Collectible?.Code?.ToString() ?? "unknown";
            string msg = $"[OptiTime] Handbook skipped broken item page '{code}' ({ex.GetType().Name}: {ex.Message}).";
            try
            {
                if (capi?.Logger != null)
                {
                    capi.Logger.Warning(msg);
                    return;
                }
            }
            catch
            {
            }

            logger?.Invoke(msg);
        }
    }
}
