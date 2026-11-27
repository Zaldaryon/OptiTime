using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using OptiTime.Tests.Mocks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Xunit;

namespace OptiTime.Tests
{
    public class EntityAnimationOptimizationTests
    {
        private const double CloseSq = 64.0 * 64.0;
        private const double MediumSq = 128.0 * 128.0;

        [Theory]
        [InlineData(0.0, 0, 0)]
        [InlineData(63.9, 0, 0)]
        [InlineData(68.0, 0, 0)]   // past the close range but inside the 1.1x entry margin
        [InlineData(70.5, 0, 1)]
        [InlineData(68.0, 1, 1)]   // inside the margin an entity already in tier 1 stays there
        [InlineData(63.9, 1, 0)]
        [InlineData(135.0, 1, 1)]
        [InlineData(140.9, 1, 2)]
        [InlineData(135.0, 2, 2)]
        [InlineData(127.9, 2, 1)]
        [InlineData(10.0, 2, 0)]
        public void ResolveLodTier_EntersPastTheMarginAndLeavesBelowTheThreshold(double distance, int previousTier, int expected)
        {
            Assert.Equal(expected, EntityAnimationOptimization.ResolveLodTier(distance * distance, previousTier, CloseSq, MediumSq));
        }

        [Fact]
        public void ResolveLodTier_DoesNotFlipWhileWalkingAlongTheBoundary()
        {
            int tier = 0;
            int changes = 0;
            for (int step = 0; step < 200; step++)
            {
                double distance = 64.0 + 2.0 * Math.Sin(step * 0.3);
                int next = EntityAnimationOptimization.ResolveLodTier(distance * distance, tier, CloseSq, MediumSq);
                if (next != tier) changes++;
                tier = next;
            }

            Assert.Equal(0, changes);
        }

        [Fact]
        public void ShouldComputePose_PosesEveryFrameInTierZeroAndThrottlesFartherTiers()
        {
            const int frames = 600;
            int[] computed = new int[3];
            for (uint frame = 0; frame < frames; frame++)
            {
                for (int tier = 0; tier < 3; tier++)
                {
                    if (EntityAnimationOptimization.ShouldComputePose(tier, frame, entityId: 12345)) computed[tier]++;
                }
            }

            Assert.Equal(frames, computed[0]);
            Assert.Equal(frames / 2, computed[1]);
            Assert.Equal(frames / 3, computed[2]);
        }

        [Fact]
        public void ShouldComputePose_StaggersEntitiesAcrossFrames()
        {
            for (uint frame = 0; frame < 30; frame++)
            {
                int posedInTier1 = 0;
                int posedInTier2 = 0;
                for (long entityId = 100; entityId < 106; entityId++)
                {
                    if (EntityAnimationOptimization.ShouldComputePose(1, frame, entityId)) posedInTier1++;
                    if (EntityAnimationOptimization.ShouldComputePose(2, frame, entityId)) posedInTier2++;
                }

                Assert.Equal(3, posedInTier1);
                Assert.Equal(2, posedInTier2);
            }
        }

        [Fact]
        public void UpdateViewDistance_NeverPutsTheCloseRangeBelow48Blocks()
        {
            try
            {
                EntityAnimationOptimization.UpdateViewDistance(64);
                Assert.Equal(48.0 * 48.0, EntityAnimationOptimization.CloseDistanceSq);
                Assert.True(EntityAnimationOptimization.MediumDistanceSq > EntityAnimationOptimization.CloseDistanceSq * EntityAnimationOptimization.HysteresisFactorSq);

                EntityAnimationOptimization.UpdateViewDistance(512);
                Assert.Equal(128.0 * 128.0, EntityAnimationOptimization.CloseDistanceSq);
                Assert.Equal(256.0 * 256.0, EntityAnimationOptimization.MediumDistanceSq);
            }
            finally
            {
                EntityAnimationOptimization.UpdateViewDistance(256);
            }
        }

        [Fact]
        public void IsPoseThrottleExempt_KeepsProjectilesAndPlayersAtFullRate()
        {
            Assert.True(EntityAnimationOptimization.IsPoseThrottleExempt(MockFactory.NewProjectile(id: 7)));

            var player = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
            Assert.True(EntityAnimationOptimization.IsPoseThrottleExempt(player));
        }

        [Fact]
        public void TickAnimation_ThrottledFrameAdvancesTimeButSkipsThePose()
        {
            var (manager, animator) = AnimationRecorder.Create();

            EntityAnimationOptimization.TickAnimation(manager, computePose: false, dt: 0.016f);

            Assert.Equal(new[] { 0.016f }, animator.Recorder.FrameDts);
            Assert.Equal(new[] { false }, animator.Recorder.CalculateMatricesDuringFrame);
            Assert.True(animator.CalculateMatrices);
        }

        [Fact]
        public void TickAnimation_PosedFrameLeavesTheAnimatorUntouched()
        {
            var (manager, animator) = AnimationRecorder.Create();

            EntityAnimationOptimization.TickAnimation(manager, computePose: true, dt: 0.02f);

            Assert.Equal(new[] { 0.02f }, animator.Recorder.FrameDts);
            Assert.Equal(new[] { true }, animator.Recorder.CalculateMatricesDuringFrame);
            Assert.True(animator.CalculateMatrices);
        }

        [Fact]
        public void TickAnimation_RestoresThePoseFlagWhenTheAnimationThrows()
        {
            var (manager, animator) = AnimationRecorder.Create();
            animator.Recorder.ThrowOnFrame = true;

            Assert.Throws<InvalidOperationException>(() =>
                EntityAnimationOptimization.TickAnimation(manager, computePose: false, dt: 0.016f));

            Assert.True(animator.CalculateMatrices);
        }

        [Fact]
        public void TickAnimation_LeavesAPoseFlagThatSomeoneElseTurnedOff()
        {
            var (manager, animator) = AnimationRecorder.Create();
            animator.CalculateMatrices = false;

            EntityAnimationOptimization.TickAnimation(manager, computePose: false, dt: 0.016f);
            EntityAnimationOptimization.TickAnimation(manager, computePose: true, dt: 0.016f);

            Assert.False(animator.CalculateMatrices);
        }

        [Fact]
        public void TickAnimation_ToleratesEntitiesWithoutAnimation()
        {
            EntityAnimationOptimization.TickAnimation(null, computePose: false, dt: 0.016f);
        }

        /// <summary>
        /// Interface fakes for <see cref="IAnimationManager"/> and <see cref="IAnimator"/>: the manager
        /// records every OnClientFrame call and the animator's CalculateMatrices value at that moment.
        /// Members the optimization does not use return defaults.
        /// </summary>
        public class AnimationRecorder : DispatchProxy
        {
            public bool CalculateMatrices = true;
            public bool ThrowOnFrame;
            public readonly List<float> FrameDts = new();
            public readonly List<bool> CalculateMatricesDuringFrame = new();
            public AnimationRecorder Shared;
            public object AnimatorProxy;

            public static (IAnimationManager Manager, RecordingAnimator Animator) Create()
            {
                var state = new AnimationRecorder();
                var animator = DispatchProxy.Create<IAnimator, AnimationRecorder>();
                var animatorRecorder = (AnimationRecorder)(object)animator;
                animatorRecorder.Shared = state;

                var manager = DispatchProxy.Create<IAnimationManager, AnimationRecorder>();
                var managerRecorder = (AnimationRecorder)(object)manager;
                managerRecorder.Shared = state;
                managerRecorder.AnimatorProxy = animator;

                return (manager, new RecordingAnimator(animator, state));
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case "get_CalculateMatrices":
                        return Shared.CalculateMatrices;
                    case "set_CalculateMatrices":
                        Shared.CalculateMatrices = (bool)args[0];
                        return null;
                    case "get_Animator":
                        return AnimatorProxy;
                    case "OnClientFrame":
                        Shared.FrameDts.Add((float)args[0]);
                        Shared.CalculateMatricesDuringFrame.Add(Shared.CalculateMatrices);
                        if (Shared.ThrowOnFrame) throw new InvalidOperationException("animation failure");
                        return null;
                    default:
                        return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                            ? Activator.CreateInstance(targetMethod.ReturnType)
                            : null;
                }
            }
        }

        public sealed class RecordingAnimator
        {
            private readonly IAnimator animator;

            public RecordingAnimator(IAnimator animator, AnimationRecorder recorder)
            {
                this.animator = animator;
                Recorder = recorder;
            }

            public AnimationRecorder Recorder { get; }

            public bool CalculateMatrices
            {
                get => animator.CalculateMatrices;
                set => animator.CalculateMatrices = value;
            }
        }
    }
}
