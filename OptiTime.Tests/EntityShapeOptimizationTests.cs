using System.Collections.Generic;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public class EntityShapeOptimizationTests
    {
        [Theory]
        [InlineData(false, false, true, true)]   // a reshape of a meshed creature is budgeted
        [InlineData(true, false, true, false)]   // shape not marked modified: an explicit call always runs
        [InlineData(false, true, true, false)]   // players are never deferred
        [InlineData(false, false, false, false)] // the first mesh is never deferred
        public void IsBudgeted_OnlyCoversReshapesThatWillBeAskedAgain(bool shapeFresh, bool isPlayer, bool hasMesh, bool expected)
        {
            Assert.Equal(expected, EntityShapeOptimization.IsBudgeted(shapeFresh, isPlayer, hasMesh));
        }

        [Theory]
        [InlineData(0, false, 0u, true)]
        [InlineData(1, false, 0u, false)]
        [InlineData(1, true, 29u, false)]
        [InlineData(1, true, 30u, true)]   // waited long enough: runs whatever the budget
        [InlineData(5, true, 400u, true)]
        public void MayStartNow_HonoursTheBudgetAndTheWaitLimit(int startedThisFrame, bool waiting, uint waitedFrames, bool expected)
        {
            Assert.Equal(expected, EntityShapeOptimization.MayStartNow(startedThisFrame, waiting, waitedFrames));
        }

        [Fact]
        public void EightSimultaneousReshapesRunOnePerFrameAndAllFinish()
        {
            EntityShapeOptimization.Cleanup();
            try
            {
                var pending = new HashSet<long> { 1, 2, 3, 4, 5, 6, 7, 8 };
                var perFrame = new List<int>();
                for (int frame = 0; frame < 20 && pending.Count > 0; frame++)
                {
                    EntityShapeOptimization.BeginFrame();
                    int started = 0;
                    foreach (long id in new List<long>(pending))
                    {
                        if (EntityShapeOptimization.TryStartReshape(id))
                        {
                            pending.Remove(id);
                            started++;
                        }
                    }
                    perFrame.Add(started);
                }

                Assert.Empty(pending);
                Assert.Equal(new[] { 1, 1, 1, 1, 1, 1, 1, 1 }, perFrame);
                Assert.Equal(0, EntityShapeOptimization.WaitingCount);
            }
            finally
            {
                EntityShapeOptimization.Cleanup();
            }
        }

        [Fact]
        public void AReshapeStarvedByAnotherEntityRunsAfterTheWaitLimit()
        {
            EntityShapeOptimization.Cleanup();
            try
            {
                int waitedFrames = -1;
                for (int frame = 0; frame < 100; frame++)
                {
                    EntityShapeOptimization.BeginFrame();
                    // Entity 1 asks first every frame and always takes the budget.
                    Assert.True(EntityShapeOptimization.TryStartReshape(1));
                    if (EntityShapeOptimization.TryStartReshape(2))
                    {
                        waitedFrames = frame;
                        break;
                    }
                }

                Assert.Equal((int)EntityShapeOptimization.MaxWaitFrames, waitedFrames);
            }
            finally
            {
                EntityShapeOptimization.Cleanup();
            }
        }

        [Fact]
        public void EntitiesThatStopAskingArePruned()
        {
            EntityShapeOptimization.Cleanup();
            try
            {
                EntityShapeOptimization.BeginFrame();
                Assert.True(EntityShapeOptimization.TryStartReshape(1));
                Assert.False(EntityShapeOptimization.TryStartReshape(2));
                Assert.Equal(1, EntityShapeOptimization.WaitingCount);

                // Entity 2 unloads and never asks again.
                for (int frame = 0; frame < 1300; frame++) EntityShapeOptimization.BeginFrame();

                Assert.Equal(0, EntityShapeOptimization.WaitingCount);
            }
            finally
            {
                EntityShapeOptimization.Cleanup();
            }
        }
    }
}
