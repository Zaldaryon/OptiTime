using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using OptiTime.Diagnostics;

namespace OptiTime
{
    public class GuiManagerOptimization
    {
        // Cached lists to avoid ToList() allocations and preserve snapshot safety
        private static readonly List<GuiDialog> cachedLoadedGuis = new List<GuiDialog>();
        private static readonly List<GuiDialog> cachedOpenedGuis = new List<GuiDialog>();
        private static readonly List<GuiDialog> cachedReversedGuis = new List<GuiDialog>();
        private static bool loadedGuisInUse;
        private static bool openedGuisInUse;
        private static bool reversedGuisInUse;
        private static readonly HashSet<string> suppressedPostRenderExceptions = new HashSet<string>();

        // Cached field access: replaces dynamic dispatch
        private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> gameRef =
            AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
        private static readonly AccessTools.FieldRef<ClientMain, List<GuiDialog>> openedGuisRef =
            AccessTools.FieldRefAccess<ClientMain, List<GuiDialog>>("OpenedGuis");
        private static readonly AccessTools.FieldRef<ClientMain, List<GuiDialog>> loadedGuisRef =
            AccessTools.FieldRefAccess<ClientMain, List<GuiDialog>>("LoadedGuis");
        private static readonly System.Func<ClientMain, int> dialogsOpenedRef =
            (System.Func<ClientMain, int>)AccessTools.PropertyGetter(typeof(ClientMain), "DialogsOpened")?.CreateDelegate(typeof(System.Func<ClientMain, int>));
        private static readonly AccessTools.FieldRef<GuiManager, bool> didHoverSlotEventTriggerRef =
            AccessTools.FieldRefAccess<GuiManager, bool>("didHoverSlotEventTrigger");
        private static readonly AccessTools.FieldRef<GuiElementItemSlotGridBase, IInventory> inventoryRef =
            AccessTools.FieldRefAccess<GuiElementItemSlotGridBase, IInventory>("inventory");
        private static readonly AccessTools.FieldRef<GuiElementItemSlotGridBase, Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot>> availableSlotsRef =
            AccessTools.FieldRefAccess<GuiElementItemSlotGridBase, Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot>>("availableSlots");
        private static readonly Action<GuiManager> onEscapePressedDelegate;
        private static readonly Action<GuiManager, GuiDialog> requestFocusDelegate;
        private static readonly Action<GuiManager, GuiDialog> onMouseMoveOverDelegate;
        private static bool mouseMoveCoalescingEnabled;
        private static int mouseMoveCoalesceIntervalMs = 8;
        private static readonly System.Threading.Lock mouseMoveLock = new();
        private static MouseEvent pendingMouseMoveEvent;
        private static int pendingMouseDeltaX;
        private static int pendingMouseDeltaY;
        private static int pendingMouseMoveCount;
        private static long lastMouseMoveQueuedMs;
        private static long lastMouseMoveProcessedMs;

        static GuiManagerOptimization()
        {
            try
            {
                Type managerType = typeof(GuiManager);
                MethodInfo escapeMethod = AccessTools.Method(managerType, "OnEscapePressed");
                if (escapeMethod != null)
                    onEscapePressedDelegate = (Action<GuiManager>)escapeMethod.CreateDelegate(typeof(Action<GuiManager>));

                MethodInfo focusMethod = AccessTools.Method(managerType, "RequestFocus", new[] { typeof(GuiDialog) });
                if (focusMethod != null)
                    requestFocusDelegate = (Action<GuiManager, GuiDialog>)focusMethod.CreateDelegate(typeof(Action<GuiManager, GuiDialog>));

                MethodInfo mouseOverMethod = AccessTools.Method(managerType, "OnMouseMoveOver", new[] { typeof(GuiDialog) });
                if (mouseOverMethod != null)
                    onMouseMoveOverDelegate = (Action<GuiManager, GuiDialog>)mouseOverMethod.CreateDelegate(typeof(Action<GuiManager, GuiDialog>));
            }
            catch
            {
            }
        }

        // Cleanup method to prevent memory leaks
        public static void Cleanup()
        {
            cachedLoadedGuis.Clear();
            cachedOpenedGuis.Clear();
            cachedReversedGuis.Clear();
            cachedLoadedGuis.TrimExcess();
            cachedOpenedGuis.TrimExcess();
            cachedReversedGuis.TrimExcess();
            loadedGuisInUse = false;
            openedGuisInUse = false;
            reversedGuisInUse = false;
            suppressedPostRenderExceptions.Clear();

            // Don't clear reflection/delegate caches - they're reusable
            lock (mouseMoveLock)
            {
                pendingMouseMoveEvent = null;
                pendingMouseDeltaX = 0;
                pendingMouseDeltaY = 0;
                pendingMouseMoveCount = 0;
                lastMouseMoveQueuedMs = 0;
                lastMouseMoveProcessedMs = 0;
            }
            mouseMoveCoalesceIntervalMs = 8;
            mouseMoveCoalescingEnabled = false;
        }

        public static void Configure(OptiTimeConfig config)
        {
            if (config == null)
            {
                mouseMoveCoalescingEnabled = false;
                mouseMoveCoalesceIntervalMs = 8;
                return;
            }

            mouseMoveCoalescingEnabled = config.GuiManagerMouseMoveCoalescingEnabled;
            mouseMoveCoalesceIntervalMs = Math.Clamp(config.GuiManagerMouseMoveCoalesceIntervalMs, 1, 50);
        }

        private static List<GuiDialog> RentLoadedGuis(List<GuiDialog> loadedGuis, out bool rentedShared)
        {
            List<GuiDialog> snapshot;
            if (!loadedGuisInUse)
            {
                loadedGuisInUse = true;
                rentedShared = true;
                snapshot = cachedLoadedGuis;
                snapshot.Clear();
            }
            else
            {
                rentedShared = false;
                snapshot = new List<GuiDialog>(loadedGuis.Count);
            }

            snapshot.AddRange(loadedGuis);
            return snapshot;
        }

        private static void ReleaseLoadedGuis(List<GuiDialog> snapshot, bool rentedShared)
        {
            snapshot.Clear();
            if (rentedShared)
                loadedGuisInUse = false;
        }

        private static List<GuiDialog> RentOpenedGuis(List<GuiDialog> openedGuis, out bool rentedShared)
        {
            List<GuiDialog> snapshot;
            if (!openedGuisInUse)
            {
                openedGuisInUse = true;
                rentedShared = true;
                snapshot = cachedOpenedGuis;
                snapshot.Clear();
            }
            else
            {
                rentedShared = false;
                snapshot = new List<GuiDialog>(openedGuis.Count);
            }

            snapshot.AddRange(openedGuis);
            return snapshot;
        }

        private static void ReleaseOpenedGuis(List<GuiDialog> snapshot, bool rentedShared)
        {
            snapshot.Clear();
            if (rentedShared)
                openedGuisInUse = false;
        }

        private static List<GuiDialog> RentReversedGuis(List<GuiDialog> openedGuis, out bool rentedShared)
        {
            List<GuiDialog> snapshot;
            if (!reversedGuisInUse)
            {
                reversedGuisInUse = true;
                rentedShared = true;
                snapshot = cachedReversedGuis;
                snapshot.Clear();
            }
            else
            {
                rentedShared = false;
                snapshot = new List<GuiDialog>(openedGuis.Count);
            }

            for (int i = openedGuis.Count - 1; i >= 0; i--)
                snapshot.Add(openedGuis[i]);

            return snapshot;
        }

        private static void ReleaseReversedGuis(List<GuiDialog> snapshot, bool rentedShared)
        {
            snapshot.Clear();
            if (rentedShared)
                reversedGuisInUse = false;
        }

        private static bool ProcessMouseMoveInternal(GuiManager manager, MouseEvent args)
        {
            try
            {
                if (manager == null || args == null)
                    return false;

                var game = gameRef(manager);
                if (game == null)
                    return false;

                var loadedGuis = loadedGuisRef(game);
                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                didHoverSlotEventTriggerRef(manager) = false;

                List<GuiDialog> snapshot = RentLoadedGuis(loadedGuis, out bool rentedShared);
                try
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        GuiDialog nowMouseOverDialog = snapshot[i];
                        if (nowMouseOverDialog != null && nowMouseOverDialog.ShouldReceiveMouseEvents())
                        {
                            nowMouseOverDialog.OnMouseMove(args);
                            if (args.Handled)
                            {
                                onMouseMoveOverDelegate?.Invoke(manager, nowMouseOverDialog);
                                return false;
                            }
                        }
                    }

                    onMouseMoveOverDelegate?.Invoke(manager, null);
                    return false;
                }
                finally
                {
                    ReleaseLoadedGuis(snapshot, rentedShared);
                }
            }
            catch
            {
                return true;
            }
        }

        private static MouseEvent BuildMergedMouseMoveEventLocked()
        {
            MouseEvent latest = pendingMouseMoveEvent;
            int totalDeltaX = pendingMouseDeltaX;
            int totalDeltaY = pendingMouseDeltaY;
            int count = pendingMouseMoveCount;

            pendingMouseMoveEvent = null;
            pendingMouseDeltaX = 0;
            pendingMouseDeltaY = 0;
            pendingMouseMoveCount = 0;

            if (latest == null)
                return null;

            return count <= 1
                ? latest
                : new MouseEvent(latest.X, latest.Y, totalDeltaX, totalDeltaY, latest.Button, latest.Modifiers);
        }

        private static void ProcessPendingMouseMove(GuiManager manager)
        {
            if (!mouseMoveCoalescingEnabled || pendingMouseMoveEvent == null)
                return;

            long nowMs = Environment.TickCount64;
            MouseEvent pending;

            lock (mouseMoveLock)
            {
                if (pendingMouseMoveEvent == null)
                    return;

                if (nowMs - lastMouseMoveQueuedMs < mouseMoveCoalesceIntervalMs)
                    return;

                pending = BuildMergedMouseMoveEventLocked();
                lastMouseMoveProcessedMs = nowMs;
            }

            if (pending != null)
            {
                ProcessMouseMoveInternal(manager, pending);
            }
        }

        // Prefix for OnBeforeRenderFrame3D - iterate a reversed snapshot without LINQ allocations
        public static bool OnBeforeRenderFrame3D_Prefix(object __instance, float deltaTime)
        {
            try
            {
                var manager = __instance as GuiManager;
                if (manager == null)
                    return true;

                var game = gameRef(manager);
                if (game == null)
                    return true;

                var openedGuis = openedGuisRef(game);
                if (openedGuis == null || openedGuis.Count == 0)
                    return false;

                List<GuiDialog> snapshot = RentReversedGuis(openedGuis, out bool rentedShared);
                try
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        GuiDialog guiDialog = snapshot[i];
                        if (guiDialog == null || !guiDialog.ShouldReceiveRenderEvents())
                            continue;

                        try
                        {
                            guiDialog.OnBeforeRenderFrame3D(deltaTime);
                        }
                        catch (Exception ex)
                        {
                            game.Logger?.Warning(
                                "[OptiTime] GuiManager skipped dialog '{0}' during OnBeforeRenderFrame3D due to exception: {1}",
                                guiDialog.DebugName ?? guiDialog.GetType().Name,
                                ex.Message);
                        }
                    }

                    return false;
                }
                finally
                {
                    ReleaseReversedGuis(snapshot, rentedShared);
                }
            }
            catch
            {
                return true;
            }
        }

        // Prefix for OnRenderFrameGUI - snapshot OpenedGuis, guard GL matrix stack in finally, and isolate per-dialog errors
        public static bool OnRenderFrameGUI_Prefix(object __instance, float deltaTime)
        {
            var manager = __instance as GuiManager;
            if (manager == null)
                return true;

            ClientMain game;
            List<GuiDialog> openedGuis;
            try
            {
                game = gameRef(manager);
                if (game == null)
                    return true;

                openedGuis = openedGuisRef(game);
                if (openedGuis == null)
                    return true;

                if (mouseMoveCoalescingEnabled)
                {
                    ProcessPendingMouseMove(manager);
                }

                // Skip rendering if player not loaded yet (prevents crash during initialization)
                if (game.EntityPlayer == null)
                    return true;
            }
            catch
            {
                return true;
            }

            if (ProfilingHelper.Enabled)
            {
                ProfilingHelper.Mark("opt-gui-render", $"opened={openedGuis.Count}", countOnly: true);
            }

            List<GuiDialog> snapshot = RentReversedGuis(openedGuis, out bool rentedShared);
            int prePushDepth = game.MvMatrix?.Count ?? 0;
            try
            {
                game.GlPushMatrix();
                int dialogBaseDepth = game.MvMatrix?.Count ?? 0;
                string mouseCursor = null;

                for (int i = 0; i < snapshot.Count; i++)
                {
                    GuiDialog guiDialog = snapshot[i];
                    if (guiDialog == null || !guiDialog.ShouldReceiveRenderEvents())
                        continue;

                    try
                    {
                        guiDialog.OnRenderGUI(deltaTime);
                        game.Platform?.CheckGlError(guiDialog.DebugName);
                        game.GlTranslate(0.0, 0.0, (double)guiDialog.ZSize);
                        if (guiDialog.MouseOverCursor != null)
                            mouseCursor = guiDialog.MouseOverCursor;

                        ScreenManager.FrameProfiler?.Mark("rendGui", guiDialog.DebugName);
                    }
                    catch (Exception ex)
                    {
                        while (game.MvMatrix != null && game.MvMatrix.Count > dialogBaseDepth)
                            game.GlPopMatrix();

                        game.Logger?.Warning(
                            "[OptiTime] GuiManager skipped dialog '{0}' during OnRenderFrameGUI due to exception: {1}",
                            guiDialog.DebugName ?? guiDialog.GetType().Name,
                            ex.Message);
                    }
                }

                game.Platform?.UseMouseCursor(mouseCursor ?? "normal");
            }
            catch (Exception ex)
            {
                game.Logger?.Warning(
                    "[OptiTime] GuiManager caught outer error during OnRenderFrameGUI: {0}",
                    ex.Message);
            }
            finally
            {
                while (game.MvMatrix != null && game.MvMatrix.Count > prePushDepth)
                    game.GlPopMatrix();

                ReleaseReversedGuis(snapshot, rentedShared);
            }

            ScreenManager.FrameProfiler?.Mark("rendGuiDone");
            ModuleGuiMgr.OnRender(true);
            return false;
        }

        // Prefix for OnFinalizeFrame - replace ToList() with direct iteration
        public static bool OnFinalizeFrame_Prefix(object __instance, float dt)
        {
            try
            {
                var manager = __instance as GuiManager;
                if (manager == null)
                    return true;

                var game = gameRef(manager);
                var loadedGuis = game != null ? loadedGuisRef(game) : null;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                List<GuiDialog> snapshot = RentLoadedGuis(loadedGuis, out bool rentedShared);
                try
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        GuiDialog guiDialog = snapshot[i];
                        if (guiDialog == null)
                            continue;

                        try
                        {
                            guiDialog.OnFinalizeFrame(dt);
                            ScreenManager.FrameProfiler?.Mark("gdm-finFr-", guiDialog.DebugName);
                        }
                        catch (Exception ex)
                        {
                            game.Logger?.Warning(
                                $"[OptiTime] GuiManager input optimization skipped one dialog during OnFinalizeFrame due to exception: {guiDialog.GetType().Name} :: {ex.Message}");
                        }
                    }

                    return false;
                }
                finally
                {
                    ReleaseLoadedGuis(snapshot, rentedShared);
                }
            }
            catch
            {
                return true;
            }
        }

        public static Exception GuiComposer_PostRender_Finalizer(GuiComposer __instance, Exception __exception)
        {
            if (__exception == null || __instance == null)
                return null;

            if (!IsSelfReferenceBoundsIssue(__exception))
                return __exception;

            if (!suppressedPostRenderExceptions.Contains(__instance.DialogName ?? "unknown"))
            {
                suppressedPostRenderExceptions.Add(__instance.DialogName ?? "unknown");
                var api = __instance.Api;
                if (api?.Logger != null)
                {
                    api.Logger.Warning(
                        $"[OptiTime] GuiComposer.PostRender self-referencing ElementBounds detected for dialog '{__instance.DialogName ?? "unknown"}'. " +
                        "The dialog frame has been skipped once to avoid game crash. Please report this to the mod author if it keeps happening.");
                }
            }

            return null;
        }

        private static bool IsSelfReferenceBoundsIssue(Exception exception)
        {
            if (exception == null)
                return false;

            string message = exception.Message ?? string.Empty;
            if (message.IndexOf("self reference itself in child bounds", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if ((exception.StackTrace ?? string.Empty).IndexOf("MarkDirtyRecursive", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (exception.InnerException != null)
                return IsSelfReferenceBoundsIssue(exception.InnerException);

            return false;
        }

        // Prefix for OnKeyDown - replace ToList() with cached list
        public static bool OnKeyDown_Prefix(object __instance, KeyEvent args)
        {
            try
            {
                var manager = __instance as GuiManager;
                if (manager == null)
                    return true;

                var game = gameRef(manager);
                var openedGuis = game != null ? openedGuisRef(game) : null;
                int keyCode = args.KeyCode;

                if (openedGuis == null || openedGuis.Count == 0)
                    return false;

                List<GuiDialog> snapshot = RentOpenedGuis(openedGuis, out bool rentedShared);
                try
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        GuiDialog guiDialog = snapshot[i];
                        if (guiDialog != null && guiDialog.CaptureAllInputs())
                        {
                            guiDialog.OnKeyDown(args);
                            if (args.Handled)
                                return false;
                        }
                    }

                    if (keyCode == 50 && (dialogsOpenedRef?.Invoke(game) ?? 0) > 0)
                    {
                        onEscapePressedDelegate?.Invoke(manager);
                        args.Handled = true;
                    }
                    else
                    {
                        for (int i = 0; i < snapshot.Count; i++)
                        {
                            GuiDialog guiDialog = snapshot[i];
                            if (guiDialog != null && guiDialog.ShouldReceiveKeyboardEvents())
                            {
                                guiDialog.OnKeyDown(args);
                                if (args.Handled)
                                    break;
                            }
                        }
                    }

                    return false;
                }
                finally
                {
                    ReleaseOpenedGuis(snapshot, rentedShared);
                }
            }
            catch
            {
                return true;
            }
        }

        // Prefix for OnKeyUp - replace ToList()
        public static bool OnKeyUp_Prefix(object __instance, KeyEvent args)
        {
            try
            {
                var game = gameRef(__instance as ClientSystem);
                var loadedGuis = game != null ? loadedGuisRef(game) : null;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                List<GuiDialog> snapshot = RentLoadedGuis(loadedGuis, out bool rentedShared);
                try
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        GuiDialog guiDialog = snapshot[i];
                        if (guiDialog != null && guiDialog.ShouldReceiveKeyboardEvents())
                        {
                            guiDialog.OnKeyUp(args);
                            if (args.Handled)
                                break;
                        }
                    }

                    return false;
                }
                finally
                {
                    ReleaseLoadedGuis(snapshot, rentedShared);
                }
            }
            catch
            {
                return true;
            }
        }

        // Prefix for OnKeyPress - replace ToList()
        public static bool OnKeyPress_Prefix(object __instance, KeyEvent args)
        {
            try
            {
                var game = gameRef(__instance as ClientSystem);
                var loadedGuis = game != null ? loadedGuisRef(game) : null;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                List<GuiDialog> snapshot = RentLoadedGuis(loadedGuis, out bool rentedShared);
                try
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        GuiDialog guiDialog = snapshot[i];
                        if (guiDialog != null && guiDialog.ShouldReceiveKeyboardEvents())
                        {
                            guiDialog.OnKeyPress(args);
                            if (args.Handled)
                                break;
                        }
                    }

                    return false;
                }
                finally
                {
                    ReleaseLoadedGuis(snapshot, rentedShared);
                }
            }
            catch
            {
                return true;
            }
        }

        // Prefix for OnMouseDown - replace ToList()
        public static bool OnMouseDown_Prefix(object __instance, MouseEvent args)
        {
            try
            {
                var manager = __instance as GuiManager;
                if (manager == null)
                    return true;

                var game = gameRef(manager);
                var loadedGuis = game != null ? loadedGuisRef(game) : null;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                List<GuiDialog> snapshot = RentLoadedGuis(loadedGuis, out bool rentedShared);
                try
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        GuiDialog dialog = snapshot[i];
                        if (dialog != null && dialog.ShouldReceiveMouseEvents())
                        {
                            dialog.OnMouseDown(args);
                            if (args.Handled)
                            {
                                if (GuiManager.DEBUG_PRINT_INTERACTIONS)
                                    game.Logger?.Debug("[GuiManager] OnMouseDown handled by {0}", dialog.GetType().Name);

                                requestFocusDelegate?.Invoke(manager, dialog);
                                break;
                            }
                        }
                    }

                    return false;
                }
                finally
                {
                    ReleaseLoadedGuis(snapshot, rentedShared);
                }
            }
            catch
            {
                return true;
            }
        }

        // Prefix for OnMouseUp - replace ToList()
        public static bool OnMouseUp_Prefix(object __instance, MouseEvent args)
        {
            try
            {
                var game = gameRef(__instance as ClientSystem);
                var loadedGuis = game != null ? loadedGuisRef(game) : null;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                List<GuiDialog> snapshot = RentLoadedGuis(loadedGuis, out bool rentedShared);
                try
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        GuiDialog guiDialog = snapshot[i];
                        if (guiDialog != null && guiDialog.ShouldReceiveMouseEvents())
                        {
                            guiDialog.OnMouseUp(args);
                            if (args.Handled)
                            {
                                if (GuiManager.DEBUG_PRINT_INTERACTIONS)
                                    game.Logger?.Debug("[GuiManager] OnMouseUp handled by {0}", guiDialog.GetType().Name);
                                break;
                            }
                        }
                    }

                    return false;
                }
                finally
                {
                    ReleaseLoadedGuis(snapshot, rentedShared);
                }
            }
            catch
            {
                return true;
            }
        }

        // Prefix for OnMouseMove - accumulate coalesced deltas and dispatch cleanly
        public static bool OnMouseMove_Prefix(object __instance, MouseEvent args)
        {
            var manager = __instance as GuiManager;
            if (manager == null || args == null)
                return false;

            if (!mouseMoveCoalescingEnabled)
            {
                return ProcessMouseMoveInternal(manager, args);
            }

            long nowMs = Environment.TickCount64;
            MouseEvent coalescedEvent = null;

            lock (mouseMoveLock)
            {
                lastMouseMoveQueuedMs = nowMs;
                pendingMouseMoveEvent = args;
                pendingMouseDeltaX += args.DeltaX;
                pendingMouseDeltaY += args.DeltaY;
                pendingMouseMoveCount++;

                if (lastMouseMoveProcessedMs == 0 || nowMs - lastMouseMoveProcessedMs >= mouseMoveCoalesceIntervalMs)
                {
                    coalescedEvent = BuildMergedMouseMoveEventLocked();
                    lastMouseMoveProcessedMs = nowMs;
                }
            }

            if (coalescedEvent == null)
                return false;

            bool result = ProcessMouseMoveInternal(manager, coalescedEvent);
            if (!ReferenceEquals(coalescedEvent, args) && coalescedEvent.Handled)
            {
                args.Handled = true;
            }
            return result;
        }

        // Prefix for ComposeInteractiveElements - add null safety for mod compatibility
        public static void ComposeInteractiveElements_Prefix(object __instance)
        {
            try
            {
                var slotGrid = __instance as GuiElementItemSlotGridBase;
                if (slotGrid == null)
                    return;

                var inventory = inventoryRef(slotGrid);
                if (inventory == null || inventory.DirtySlots == null)
                    return;

                var dirtySlots = inventory.DirtySlots;
                if (dirtySlots.Count == 0)
                    return;

                var availableSlots = availableSlotsRef(slotGrid);

                // Remove invalid dirty slots to prevent crashes in other mods' patches
                var slotsToRemove = new List<int>();
                foreach (int dirtySlot in dirtySlots)
                {
                    // Check if slot exists in inventory
                    if (inventory[dirtySlot] == null)
                    {
                        slotsToRemove.Add(dirtySlot);
                        continue;
                    }

                    // Check if slot is in available slots
                    if (availableSlots != null && availableSlots.IndexOfKey(dirtySlot) < 0)
                    {
                        slotsToRemove.Add(dirtySlot);
                    }
                }

                // Clean up invalid slots
                foreach (int slot in slotsToRemove)
                {
                    dirtySlots.Remove(slot);
                }
            }
            catch
            {
                // Silently fail - let vanilla code handle it
            }
        }
    }
}
