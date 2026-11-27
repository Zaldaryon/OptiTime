using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Xunit;

namespace OptiTime.Tests
{
    public sealed class GuiManagerOptimizationTests : IDisposable
    {
        private sealed class FakeGuiDialog : GuiDialog
        {
            private readonly Action<float> onRenderGui;
            private readonly Action<float> onBeforeRender3D;
            private readonly Action<MouseEvent> onMouseMove;
            private readonly Action<MouseEvent> onMouseDown;
            private bool receiveRender = true;
            private bool receiveMouse = true;
            private bool receiveKeyboard = true;

            public int RenderGuiCalls { get; private set; }
            public int BeforeRender3DCalls { get; private set; }
            public int EscapeCalls { get; private set; }
            public int FocusCalls { get; private set; }

            public FakeGuiDialog(
                string name,
                Action<float> onRenderGui = null,
                Action<float> onBeforeRender3D = null,
                Action<MouseEvent> onMouseMove = null,
                Action<MouseEvent> onMouseDown = null)
                : base(null)
            {
                DebugNameFake = name;
                this.onRenderGui = onRenderGui;
                this.onBeforeRender3D = onBeforeRender3D;
                this.onMouseMove = onMouseMove;
                this.onMouseDown = onMouseDown;
                opened = true;
                focused = true;
            }

            public string DebugNameFake { get; }
            public override string DebugName => DebugNameFake;
            public override string ToggleKeyCombinationCode => null;

            public void SetOpenedState(bool isOpened)
            {
                opened = isOpened;
                receiveRender = isOpened;
                receiveMouse = isOpened;
            }

            public override bool ShouldReceiveRenderEvents() => receiveRender && opened;
            public override bool ShouldReceiveMouseEvents() => receiveMouse && opened;
            public override bool ShouldReceiveKeyboardEvents() => receiveKeyboard && focused;

            public override void OnBeforeRenderFrame3D(float deltaTime)
            {
                BeforeRender3DCalls++;
                onBeforeRender3D?.Invoke(deltaTime);
            }

            public override void OnRenderGUI(float deltaTime)
            {
                RenderGuiCalls++;
                onRenderGui?.Invoke(deltaTime);
            }

            public override void OnMouseMove(MouseEvent args)
            {
                onMouseMove?.Invoke(args);
            }

            public override void OnMouseDown(MouseEvent args)
            {
                onMouseDown?.Invoke(args);
            }

            public override bool OnEscapePressed()
            {
                EscapeCalls++;
                return false;
            }

            public override void Focus()
            {
                FocusCalls++;
                base.Focus();
            }
        }

        public GuiManagerOptimizationTests()
        {
            GuiManagerOptimization.Cleanup();
        }

        public void Dispose()
        {
            GuiManagerOptimization.Cleanup();
        }

        private static (ClientMain Game, GuiManager Manager) CreateHarness()
        {
            var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
            AccessTools.Property(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.CurrentMouseCursor))
                .SetValue(platform, "normal");
            AccessTools.Field(typeof(ClientPlatformWindows), "logger")
                .SetValue(platform, new NullLogger());

            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            game.Platform = platform;
            game.MvMatrix = new StackMatrix4(1024);
            game.MvMatrix.Push(Mat4d.Create());
            game.PMatrix = new StackMatrix4(1024);
            game.PMatrix.Push(Mat4d.Create());

            AccessTools.Field(typeof(ClientMain), "OpenedGuis").SetValue(game, new List<GuiDialog>());
            AccessTools.Field(typeof(ClientMain), "LoadedGuis").SetValue(game, new List<GuiDialog>());

            var worldData = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
            worldData.EntityPlayer = new EntityPlayer();
            var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
            AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(player, worldData);
            game.player = player;

            var manager = (GuiManager)RuntimeHelpers.GetUninitializedObject(typeof(GuiManager));
            AccessTools.Field(typeof(ClientSystem), "game").SetValue(manager, game);

            return (game, manager);
        }

        [Fact]
        public void OnRenderFrameGUI_ExceptionInDialog_PreservesMatrixBalanceAndDoesNotReenterVanilla()
        {
            var (game, manager) = CreateHarness();
            var opened = (List<GuiDialog>)AccessTools.Field(typeof(ClientMain), "OpenedGuis").GetValue(game);

            var throwing = new FakeGuiDialog("Throwing", _ =>
            {
                game.GlPushMatrix();
                throw new InvalidOperationException("boom");
            });
            var healthy = new FakeGuiDialog("Healthy");
            opened.Add(healthy);
            opened.Add(throwing);

            int initialDepth = game.MvMatrix.Count;

            for (int frame = 0; frame < 1100; frame++)
            {
                bool runVanilla = GuiManagerOptimization.OnRenderFrameGUI_Prefix(manager, 0.016f);
                Assert.False(runVanilla);
                Assert.Equal(initialDepth, game.MvMatrix.Count);
            }

            Assert.Equal(1100, throwing.RenderGuiCalls);
            Assert.Equal(1100, healthy.RenderGuiCalls);
        }

        [Fact]
        public void RenderPrefixes_UseSnapshotWhenDialogsCloseMultipleDialogsDuringIteration()
        {
            var (game, manager) = CreateHarness();
            var opened = (List<GuiDialog>)AccessTools.Field(typeof(ClientMain), "OpenedGuis").GetValue(game);

            var renderOrder = new List<string>();
            var beforeOrder = new List<string>();
            FakeGuiDialog d0 = null, d1 = null, d2 = null, d3 = null;

            d0 = new FakeGuiDialog("D0",
                _ => renderOrder.Add("D0"),
                _ => beforeOrder.Add("D0"));
            d1 = new FakeGuiDialog("D1",
                _ => renderOrder.Add("D1"),
                _ => beforeOrder.Add("D1"));
            d2 = new FakeGuiDialog("D2",
                _ => renderOrder.Add("D2"),
                _ => beforeOrder.Add("D2"));
            d3 = new FakeGuiDialog("D3",
                _ =>
                {
                    renderOrder.Add("D3");
                    opened.Remove(d3);
                    d3.SetOpenedState(false);
                    opened.Remove(d1);
                    d1.SetOpenedState(false);
                },
                _ =>
                {
                    beforeOrder.Add("D3");
                    opened.Remove(d3);
                    d3.SetOpenedState(false);
                    opened.Remove(d1);
                    d1.SetOpenedState(false);
                });

            opened.AddRange(new GuiDialog[] { d0, d1, d2, d3 });
            Assert.False(GuiManagerOptimization.OnBeforeRenderFrame3D_Prefix(manager, 0.016f));
            Assert.Equal(new[] { "D3", "D2", "D0" }, beforeOrder);

            // Restore list and closed state for GUI render pass
            opened.Clear();
            d1.SetOpenedState(true);
            d3.SetOpenedState(true);
            opened.AddRange(new GuiDialog[] { d0, d1, d2, d3 });

            Assert.False(GuiManagerOptimization.OnRenderFrameGUI_Prefix(manager, 0.016f));
            Assert.Equal(new[] { "D3", "D2", "D0" }, renderOrder);
            Assert.Equal(1, game.MvMatrix.Count);

            var cachedReversed = (List<GuiDialog>)AccessTools.Field(typeof(GuiManagerOptimization), "cachedReversedGuis").GetValue(null);
            Assert.Empty(cachedReversed);
        }

        [Fact]
        public void OnMouseMove_Coalescing_AccumulatesDeltaXAndDeltaYAcrossBursts()
        {
            var (game, manager) = CreateHarness();
            var loaded = (List<GuiDialog>)AccessTools.Field(typeof(ClientMain), "LoadedGuis").GetValue(game);

            var observed = new List<MouseEvent>();
            var sink = new FakeGuiDialog("Sink", onMouseMove: args =>
            {
                observed.Add(args);
                args.Handled = true;
            });
            loaded.Add(sink);

            var config = new OptiTimeConfig
            {
                GuiManagerMouseMoveCoalescingEnabled = true,
                GuiManagerMouseMoveCoalesceIntervalMs = 50
            };
            GuiManagerOptimization.Configure(config);

            // First event processes immediately when interval elapsed or lastMouseMoveProcessedMs == 0
            GuiManagerOptimization.OnMouseMove_Prefix(manager, new MouseEvent(100, 100, 1, 2));
            Assert.Single(observed);

            // Burst queued within the 50 ms coalescing window
            GuiManagerOptimization.OnMouseMove_Prefix(manager, new MouseEvent(110, 104, 10, 4));
            GuiManagerOptimization.OnMouseMove_Prefix(manager, new MouseEvent(125, 102, 15, -2));
            GuiManagerOptimization.OnMouseMove_Prefix(manager, new MouseEvent(320, 240, -3, 7));

            // Simulate elapsed interval past 50 ms so the GUI render flush dispatches immediately
            long expiredMs = Environment.TickCount64 - 100;
            AccessTools.Field(typeof(GuiManagerOptimization), "lastMouseMoveQueuedMs").SetValue(null, expiredMs);
            AccessTools.Field(typeof(GuiManagerOptimization), "lastMouseMoveProcessedMs").SetValue(null, expiredMs);

            GuiManagerOptimization.OnRenderFrameGUI_Prefix(manager, 0.016f);

            Assert.Equal(2, observed.Count);
            Assert.Equal(320, observed[1].X);
            Assert.Equal(240, observed[1].Y);
            Assert.Equal(22, observed[1].DeltaX);
            Assert.Equal(9, observed[1].DeltaY);
        }

        [Fact]
        public void InputPaths_UseDirectDelegatesWithZeroReflectionObjectArrayAllocations()
        {
            var (game, manager) = CreateHarness();
            var loaded = (List<GuiDialog>)AccessTools.Field(typeof(ClientMain), "LoadedGuis").GetValue(game);
            var opened = (List<GuiDialog>)AccessTools.Field(typeof(ClientMain), "OpenedGuis").GetValue(game);

            GuiManagerOptimization.Configure(new OptiTimeConfig
            {
                GuiManagerMouseMoveCoalescingEnabled = false
            });

            var dialog = new FakeGuiDialog(
                "InputTarget",
                onMouseMove: args => args.Handled = true,
                onMouseDown: args => args.Handled = true);
            loaded.Add(dialog);
            opened.Add(dialog);

            // Warmup static constructors/profiler check and verify RequestFocus delegate works
            var moveArgs = new MouseEvent(50, 60, 2, 3);
            var downArgs = new MouseEvent(50, 60, EnumMouseButton.Left, 0);
            var escArgs = new KeyEvent { KeyCode = 50 };

            dialog.SetOpenedState(true);
            GuiManagerOptimization.OnMouseDown_Prefix(manager, downArgs);
            Assert.Equal(1, dialog.FocusCalls);

            GuiManagerOptimization.OnMouseMove_Prefix(manager, moveArgs);
            GuiManagerOptimization.OnKeyDown_Prefix(manager, escArgs);

            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++)
            {
                moveArgs.Handled = false;
                escArgs.Handled = false;
                GuiManagerOptimization.OnMouseMove_Prefix(manager, moveArgs);
                GuiManagerOptimization.OnKeyDown_Prefix(manager, escArgs);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

            // Only cached list iterations + bound delegates: zero object[] per Invoke
            Assert.True(allocated < 512, $"Expected near-zero allocation across 500 input bursts, got {allocated} bytes");
        }
    }
}
