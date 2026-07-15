using OptiTime.Tests.Mocks;
using Vintagestory.API.Common;
using Xunit;

namespace OptiTime.Tests
{
    public class EntityInterpolationOptimizationTests
    {
        [Fact]
        public void Prefix_Projectile_ReturnsTrueToRunVanilla()
        {
            var projectile = MockFactory.NewProjectile(id: 201);
            var behavior = MockFactory.NewBehavior(projectile);
            EnumHandling handled = EnumHandling.PassThrough;
            bool result = EntityInterpolationOptimization.Prefix_OnReceivedServerPos(
                behavior, isTeleport: false, ref handled);

            Assert.True(result);
            Assert.Equal(EnumHandling.PassThrough, handled);
        }

        [Fact]
        public void F5_HardTeleport_WhenQueueCountAbove50()
        {
            var entity = MockFactory.NewTestEntity(id: 400);
            entity.Pos.X = 100;
            entity.Pos.Y = 64;
            entity.Pos.Z = 100;
            entity.Alive = true;

            var behavior = MockFactory.NewBehavior(entity);

            // Fill queue with 55 entries (> HardTeleportThreshold=50)
            for (int i = 0; i < 55; i++)
            {
                behavior.positionQueue.Enqueue(new PositionSnapshot
                {
                    x = 100 + i, y = 64, z = 100, interval = 1f / 15f
                });
            }
            behavior.queueCount = 55;
            behavior.pN = new PositionSnapshot { x = 100, y = 64, z = 100, interval = 1f / 15f };

            EnumHandling handled = EnumHandling.PassThrough;
            EntityInterpolationOptimization.Prefix_OnReceivedServerPos(behavior, isTeleport: false, ref handled);

            // After hard teleport (PopQueue(true)), queue should be nearly empty (recursive drain)
            Assert.True(behavior.queueCount <= 1,
                $"Expected queue ≤1 after hard teleport, got {behavior.queueCount}");
            Assert.Equal(EnumHandling.PreventSubsequent, handled);
        }

        [Fact]
        public void F5_Accelerate_WhenQueueCount15To50()
        {
            var entity = MockFactory.NewTestEntity(id: 401);
            entity.Pos.X = 50;
            entity.Pos.Y = 64;
            entity.Pos.Z = 50;
            entity.Alive = true;

            var behavior = MockFactory.NewBehavior(entity);
            behavior.targetSpeed = 0.6f;

            // Fill queue with 20 entries (between 15 and 50)
            for (int i = 0; i < 20; i++)
            {
                behavior.positionQueue.Enqueue(new PositionSnapshot
                {
                    x = 50 + i, y = 64, z = 50, interval = 1f / 15f
                });
            }
            behavior.queueCount = 20;
            behavior.pN = new PositionSnapshot { x = 50, y = 64, z = 50, interval = 1f / 15f };

            EnumHandling handled = EnumHandling.PassThrough;
            EntityInterpolationOptimization.Prefix_OnReceivedServerPos(behavior, isTeleport: false, ref handled);

            // queueCount after PushQueue = 21, which is > AccelerateThreshold(15)
            // speed = 1.0 + (21 - 15) * 0.15 = 1.9
            Assert.True(behavior.targetSpeed > 1.0f,
                $"Expected targetSpeed > 1.0, got {behavior.targetSpeed}");
            Assert.True(behavior.targetSpeed <= 4.0f,
                $"Expected targetSpeed ≤ 4.0 (capped), got {behavior.targetSpeed}");
            Assert.Equal(EnumHandling.PreventSubsequent, handled);
        }

        [Fact]
        public void F5_NormalQueue_NoAcceleration()
        {
            var entity = MockFactory.NewTestEntity(id: 402);
            entity.Pos.X = 10;
            entity.Pos.Y = 64;
            entity.Pos.Z = 10;
            entity.Alive = true;

            var behavior = MockFactory.NewBehavior(entity);
            behavior.targetSpeed = 0.6f;

            // Fill queue with 5 entries (< AccelerateThreshold=15)
            for (int i = 0; i < 5; i++)
            {
                behavior.positionQueue.Enqueue(new PositionSnapshot
                {
                    x = 10 + i, y = 64, z = 10, interval = 1f / 15f
                });
            }
            behavior.queueCount = 5;
            behavior.pN = new PositionSnapshot { x = 10, y = 64, z = 10, interval = 1f / 15f };

            float originalSpeed = behavior.targetSpeed;
            EnumHandling handled = EnumHandling.PassThrough;
            EntityInterpolationOptimization.Prefix_OnReceivedServerPos(behavior, isTeleport: false, ref handled);

            // queueCount after PushQueue = 6, which is < AccelerateThreshold(15)
            // targetSpeed should be unchanged
            Assert.Equal(originalSpeed, behavior.targetSpeed);
            Assert.Equal(EnumHandling.PreventSubsequent, handled);
        }

        [Fact]
        public void Prefix_Teleport_ClearsQueueAndResets()
        {
            var entity = MockFactory.NewTestEntity(id: 403);
            entity.Pos.X = 500;
            entity.Pos.Y = 64;
            entity.Pos.Z = 500;
            entity.Pos.Yaw = 1.5f;
            entity.Pos.Pitch = 0.1f;
            entity.Pos.Roll = 0f;
            entity.Alive = true;

            var behavior = MockFactory.NewBehavior(entity);
            // Pre-fill queue
            for (int i = 0; i < 10; i++)
            {
                behavior.positionQueue.Enqueue(new PositionSnapshot
                {
                    x = i, y = 64, z = i, interval = 1f / 15f
                });
            }
            behavior.queueCount = 10;
            behavior.pN = new PositionSnapshot { x = 0, y = 64, z = 0, interval = 1f / 15f };

            EnumHandling handled = EnumHandling.PassThrough;
            EntityInterpolationOptimization.Prefix_OnReceivedServerPos(behavior, isTeleport: true, ref handled);

            // After teleport: dtAccum=0, queue cleared, yaw/pitch synced
            Assert.Equal(0f, behavior.dtAccum);
            Assert.Equal(1.5f, behavior.currentYaw);
            Assert.Equal(0.1f, behavior.currentPitch);
            Assert.Equal(EnumHandling.PreventSubsequent, handled);
        }
    }
}
