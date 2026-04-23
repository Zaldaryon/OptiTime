using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;

namespace OptiTime
{
    public class GuiManagerOptimization
    {
        // Cached lists to avoid ToList() allocations
        private static List<GuiDialog> cachedLoadedGuis = new List<GuiDialog>();
        private static List<GuiDialog> cachedOpenedGuis = new List<GuiDialog>();
        private static List<GuiDialog> cachedReversedGuis = new List<GuiDialog>();
        private static int cleanupCounter = 0;
        private static readonly System.Collections.Generic.HashSet<GuiComposer> suppressedPostRenderExceptions = new System.Collections.Generic.HashSet<GuiComposer>();

        // Cached reflection results to avoid repeated lookups
        private static Type screenManagerType = null;
        private static FieldInfo frameProfilerField = null;
        private static object frameProfilerInstance = null;
        private static System.Reflection.MethodInfo markMethodWithObject = null;
        private static System.Reflection.MethodInfo markMethodString = null;
        private static System.Reflection.MethodInfo onEscapePressedMethod = null;
        private static System.Reflection.MethodInfo requestFocusMethod = null;
        private static System.Reflection.MethodInfo onMouseMoveOverMethod = null;
        private static Type guiManagerType = null;
        private static FieldInfo debugPrintField = null;
        private static bool mouseMoveCoalescingEnabled = false;
        private static int mouseMoveCoalesceIntervalMs = 8;
        private static readonly System.Threading.Lock mouseMoveLock = new();
        private static MouseEvent pendingMouseMoveEvent = null;
        private static bool hasPendingMouseMove = false;
        private static long lastMouseMoveQueuedMs = 0;
        private static long lastMouseMoveProcessedMs = 0;

        static GuiManagerOptimization()
        {
            try
            {
                // Cache all reflection lookups at startup
                screenManagerType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ScreenManager");
                if (screenManagerType != null)
                {
                    frameProfilerField = AccessTools.Field(screenManagerType, "FrameProfiler");
                    if (frameProfilerField != null)
                    {
                        frameProfilerInstance = frameProfilerField.GetValue(null);
                        if (frameProfilerInstance != null)
                        {
                            var profilerType = frameProfilerInstance.GetType();
                            markMethodWithObject = AccessTools.Method(profilerType, "Mark", new Type[] { typeof(string), typeof(object) });
                            markMethodString = AccessTools.Method(profilerType, "Mark", new Type[] { typeof(string) });
                        }
                    }
                }

                guiManagerType = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiManager");
                if (guiManagerType != null)
                {
                    debugPrintField = AccessTools.Field(guiManagerType, "DEBUG_PRINT_INTERACTIONS");
                    onEscapePressedMethod = AccessTools.Method(guiManagerType, "OnEscapePressed");
                    requestFocusMethod = AccessTools.Method(guiManagerType, "RequestFocus");
                    onMouseMoveOverMethod = AccessTools.Method(guiManagerType, "OnMouseMoveOver", new Type[] { typeof(GuiDialog) });
                }
            }
            catch { }
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
            suppressedPostRenderExceptions.Clear();
            cleanupCounter = 0;

            // Don't clear reflection caches - they're reusable
            pendingMouseMoveEvent = null;
            hasPendingMouseMove = false;
            mouseMoveCoalesceIntervalMs = 8;
            mouseMoveCoalescingEnabled = false;
            lastMouseMoveQueuedMs = 0;
            lastMouseMoveProcessedMs = 0;
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

        // Helper to update cached lists when needed
        private static void UpdateCachedLoadedGuis(List<GuiDialog> loadedGuis)
        {
            cachedLoadedGuis.Clear();
            cachedLoadedGuis.AddRange(loadedGuis);

            // Trim excess capacity every 1000 frames to prevent unbounded growth
            if (++cleanupCounter >= 1000)
            {
                cachedLoadedGuis.TrimExcess();
                cachedOpenedGuis.TrimExcess();
                cachedReversedGuis.TrimExcess();
                cleanupCounter = 0;
            }
        }

        private static void UpdateCachedOpenedGuis(List<GuiDialog> openedGuis)
        {
            cachedOpenedGuis.Clear();
            cachedOpenedGuis.AddRange(openedGuis);
        }

        private static void UpdateCachedReversedGuis(List<GuiDialog> openedGuis)
        {
            cachedReversedGuis.Clear();
            for (int i = openedGuis.Count - 1; i >= 0; i--)
            {
                cachedReversedGuis.Add(openedGuis[i]);
            }
        }

        private static bool ProcessMouseMoveInternal(object __instance, MouseEvent args)
        {
            try
            {
                if (args == null)
                    return false;

                var instance = __instance as dynamic;
                var game = instance.game;
                var loadedGuis = game.LoadedGuis as List<GuiDialog>;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                instance.didHoverSlotEventTrigger = false;

                UpdateCachedLoadedGuis(loadedGuis);

                foreach (GuiDialog nowMouseOverDialog in cachedLoadedGuis)
                {
                    if (nowMouseOverDialog.ShouldReceiveMouseEvents())
                    {
                        nowMouseOverDialog.OnMouseMove(args);
                        if (args.Handled)
                        {
                            onMouseMoveOverMethod?.Invoke(instance, new object[] { nowMouseOverDialog });
                            return false;
                        }
                    }
                }

                onMouseMoveOverMethod?.Invoke(instance, new object[] { null });
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void ProcessPendingMouseMove(object instance)
        {
            if (!mouseMoveCoalescingEnabled || !hasPendingMouseMove)
                return;

            long nowMs = Environment.TickCount64;
            MouseEvent pending = null;

            lock (mouseMoveLock)
            {
                if (!hasPendingMouseMove)
                    return;

                if (nowMs - lastMouseMoveQueuedMs < mouseMoveCoalesceIntervalMs)
                    return;

                pending = pendingMouseMoveEvent;
                pendingMouseMoveEvent = null;
                hasPendingMouseMove = false;
                lastMouseMoveProcessedMs = nowMs;
            }

            if (pending != null)
            {
                ProcessMouseMoveInternal(instance, pending);
            }
        }

        // Prefix for OnBeforeRenderFrame3D - replace Reverse() with indexed loop
        public static bool OnBeforeRenderFrame3D_Prefix(object __instance, float deltaTime)
        {
            try
            {
                var instance = __instance as dynamic;
                var openedGuis = instance.game.OpenedGuis as List<GuiDialog>;

                if (openedGuis == null || openedGuis.Count == 0)
                    return false;

                for (int i = openedGuis.Count - 1; i >= 0; i--)
                {
                    GuiDialog guiDialog = openedGuis[i];
                    if (guiDialog.ShouldReceiveRenderEvents())
                        guiDialog.OnBeforeRenderFrame3D(deltaTime);
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        // Prefix for OnRenderFrameGUI - replace Reverse() with indexed loop
        public static bool OnRenderFrameGUI_Prefix(object __instance, float deltaTime)
        {
            try
            {
                var instance = __instance as dynamic;
                var game = instance.game;
                var openedGuis = game.OpenedGuis as List<GuiDialog>;

                if (openedGuis == null)
                    return true;

                if (mouseMoveCoalescingEnabled)
                {
                    ProcessPendingMouseMove(instance);
                }

                // Skip rendering if player not loaded yet (prevents crash during initialization)
                if (game.EntityPlayer == null)
                    return true;

                if (ProfilingHelper.Enabled)
                {
                    ProfilingHelper.Mark("opt-gui-render", $"opened={openedGuis.Count}", countOnly: true);
                }

                game.GlPushMatrix();
                string mouseCursor = null;

                for (int i = openedGuis.Count - 1; i >= 0; i--)
                {
                    GuiDialog guiDialog = openedGuis[i];
                    if (guiDialog.ShouldReceiveRenderEvents())
                    {
                        guiDialog.OnRenderGUI(deltaTime);
                        game.Platform.CheckGlError(guiDialog.DebugName);
                        game.GlTranslate(0.0, 0.0, (double)guiDialog.ZSize);
                        if (guiDialog.MouseOverCursor != null)
                            mouseCursor = guiDialog.MouseOverCursor;

                        // Profiler marking (using cached reflection)
                        if (markMethodWithObject != null && frameProfilerInstance != null)
                        {
                            markMethodWithObject.Invoke(frameProfilerInstance, new object[] { "rendGui", guiDialog.DebugName });
                        }
                    }
                }

                game.Platform.UseMouseCursor(mouseCursor ?? "normal");
                game.GlPopMatrix();

                // Final profiler mark (using cached reflection)
                if (markMethodString != null && frameProfilerInstance != null)
                {
                    markMethodString.Invoke(frameProfilerInstance, new object[] { "rendGuiDone" });
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        // Prefix for OnFinalizeFrame - replace ToList() with direct iteration
        public static bool OnFinalizeFrame_Prefix(object __instance, float dt)
        {
            try
            {
                var instance = __instance as dynamic;
                var loadedGuis = instance.game.LoadedGuis as List<GuiDialog>;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                UpdateCachedLoadedGuis(loadedGuis);

                foreach (GuiDialog guiDialog in cachedLoadedGuis)
                {
                    try
                    {
                        guiDialog.OnFinalizeFrame(dt);

                        // Profiler marking (using cached reflection)
                        if (markMethodWithObject != null && frameProfilerInstance != null)
                        {
                            markMethodWithObject.Invoke(frameProfilerInstance, new object[] { "gdm-finFr-", guiDialog.DebugName });
                        }
                    }
                    catch (Exception ex)
                    {
                        if (instance?.game?.Logger != null)
                        {
                            instance.game.Logger.Warning(
                                $"[OptiTime] GuiManager input optimization skipped one dialog during OnFinalizeFrame due to exception: {guiDialog?.GetType()?.Name} :: {ex.Message}");
                        }
                    }
                }

                return false;
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

            if (!suppressedPostRenderExceptions.Contains(__instance))
            {
                suppressedPostRenderExceptions.Add(__instance);
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
                var instance = __instance as dynamic;
                var game = instance.game;
                var openedGuis = game.OpenedGuis as List<GuiDialog>;
                int keyCode = args.KeyCode;

                if (openedGuis == null || openedGuis.Count == 0)
                    return false;

                UpdateCachedOpenedGuis(openedGuis);

                foreach (GuiDialog guiDialog in cachedOpenedGuis)
                {
                    if (guiDialog.CaptureAllInputs())
                    {
                        guiDialog.OnKeyDown(args);
                        if (args.Handled)
                            return false;
                    }
                }

                if (keyCode == 50 && game.DialogsOpened > 0)
                {
                    onEscapePressedMethod?.Invoke(instance, null);
                    args.Handled = true;
                }
                else
                {
                    foreach (GuiDialog guiDialog in cachedOpenedGuis)
                    {
                        if (guiDialog.ShouldReceiveKeyboardEvents())
                        {
                            guiDialog.OnKeyDown(args);
                            if (args.Handled)
                                break;
                        }
                    }
                }

                return false;
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
                var instance = __instance as dynamic;
                var loadedGuis = instance.game.LoadedGuis as List<GuiDialog>;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                UpdateCachedLoadedGuis(loadedGuis);

                foreach (GuiDialog guiDialog in cachedLoadedGuis)
                {
                    if (guiDialog.ShouldReceiveKeyboardEvents())
                    {
                        guiDialog.OnKeyUp(args);
                        if (args.Handled)
                            break;
                    }
                }

                return false;
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
                var instance = __instance as dynamic;
                var loadedGuis = instance.game.LoadedGuis as List<GuiDialog>;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                UpdateCachedLoadedGuis(loadedGuis);

                foreach (GuiDialog guiDialog in cachedLoadedGuis)
                {
                    if (guiDialog.ShouldReceiveKeyboardEvents())
                    {
                        guiDialog.OnKeyPress(args);
                        if (args.Handled)
                            break;
                    }
                }

                return false;
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
                var instance = __instance as dynamic;
                var game = instance.game;
                var loadedGuis = game.LoadedGuis as List<GuiDialog>;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                UpdateCachedLoadedGuis(loadedGuis);

                foreach (GuiDialog dialog in cachedLoadedGuis)
                {
                    if (dialog.ShouldReceiveMouseEvents())
                    {
                        dialog.OnMouseDown(args);
                        if (args.Handled)
                        {
                            bool debugPrint = debugPrintField != null && (debugPrintField.GetValue(null) as bool? ?? false);
                            if (debugPrint)
                                game.Logger.Debug("[GuiManager] OnMouseDown handled by {0}", dialog.GetType().Name);

                            requestFocusMethod?.Invoke(instance, new object[] { dialog });
                            break;
                        }
                    }
                }

                return false;
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
                var instance = __instance as dynamic;
                var game = instance.game;
                var loadedGuis = game.LoadedGuis as List<GuiDialog>;

                if (loadedGuis == null || loadedGuis.Count == 0)
                    return false;

                UpdateCachedLoadedGuis(loadedGuis);

                foreach (GuiDialog guiDialog in cachedLoadedGuis)
                {
                    if (guiDialog.ShouldReceiveMouseEvents())
                    {
                        guiDialog.OnMouseUp(args);
                        if (args.Handled)
                        {
                            bool debugPrint = debugPrintField != null && (debugPrintField.GetValue(null) as bool? ?? false);
                            if (debugPrint)
                                game.Logger.Debug("[GuiManager] OnMouseUp handled by {0}", guiDialog.GetType().Name);
                            break;
                        }
                    }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        // Prefix for OnMouseMove - replace ToList()
        public static bool OnMouseMove_Prefix(object __instance, MouseEvent args)
        {
            if (!mouseMoveCoalescingEnabled)
            {
                return ProcessMouseMoveInternal(__instance, args);
            }

            long nowMs = Environment.TickCount64;
            bool processNow = false;
            MouseEvent coalescedEvent = null;

            lock (mouseMoveLock)
            {
                lastMouseMoveQueuedMs = nowMs;
                pendingMouseMoveEvent = args;
                hasPendingMouseMove = true;

                if (lastMouseMoveProcessedMs == 0 || nowMs - lastMouseMoveProcessedMs >= mouseMoveCoalesceIntervalMs)
                {
                    processNow = true;
                    coalescedEvent = args;
                    pendingMouseMoveEvent = null;
                    hasPendingMouseMove = false;
                    lastMouseMoveProcessedMs = nowMs;
                }
            }

            if (!processNow)
                return false;

            return ProcessMouseMoveInternal(__instance, coalescedEvent);
        }

        // Prefix for ComposeInteractiveElements - add null safety for mod compatibility
        public static void ComposeInteractiveElements_Prefix(object __instance)
        {
            try
            {
                var instance = __instance as dynamic;
                if (instance.inventory == null || instance.inventory.DirtySlots == null)
                    return;

                var dirtySlots = instance.inventory.DirtySlots;
                if (dirtySlots.Count == 0)
                    return;

                // Remove invalid dirty slots to prevent crashes in other mods' patches
                var slotsToRemove = new List<int>();
                foreach (int dirtySlot in dirtySlots)
                {
                    // Check if slot exists in inventory
                    if (instance.inventory[dirtySlot] == null)
                    {
                        slotsToRemove.Add(dirtySlot);
                        continue;
                    }

                    // Check if slot is in available slots
                    if (instance.availableSlots != null && instance.availableSlots.IndexOfKey(dirtySlot) < 0)
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
