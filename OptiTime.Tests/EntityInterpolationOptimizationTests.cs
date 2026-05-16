using OptiTime.Tests.Mocks;
using Xunit;

namespace OptiTime.Tests
{
    public class EntityInterpolationOptimizationTests
    {
        // Regression test for commit db6fe6f: "fix: prevent dead entity interpolation overshoot".
        // Before the fix, Postfix_OnRenderFrame would extrapolate (F1) the position of an
        // entity that has just died, sliding it through terrain along its last velocity.
        // The fix added an early return when !entity.Alive, also clearing F1 state.
        // This test reproduces the original bug scenario and asserts the fix holds.
        [Fact]
        public void Postfix_DeadEntity_DoesNotExtrapolate()
        {
            var entity = MockFactory.NewTestEntity(id: 100);
            entity.Pos.X = 0;
            entity.Pos.Y = 10;
            entity.Pos.Z = 0;
            entity.Alive = false;

            var behavior = MockFactory.NewBehavior(entity);
            // Set up the last-known and next snapshots such that velocity is downward —
            // pre-fix code would extrapolate Y downward when wait != 0.
            behavior.pL = new PositionSnapshot { x = 0, y = 11, z = 0, interval = 1f / 30f };
            behavior.pN = new PositionSnapshot { x = 0, y = 10, z = 0, interval = 1f / 30f };
            behavior.wait = 1;

            EntityInterpolationOptimization.Postfix_OnRenderFrame(behavior, dt: 0.016f);

            Assert.Equal(0.0, entity.Pos.X);
            Assert.Equal(10.0, entity.Pos.Y);
            Assert.Equal(0.0, entity.Pos.Z);
        }

        // Regression test for commit c43ae2e: "fix: exclude projectiles from entity
        // interpolation optimization".
        // Projectiles use BehaviorPassivePhysics which competes with OptiTime's
        // extrapolation; the patch must early-return for `entity is IProjectile`.
        // Pre-fix Postfix_OnRenderFrame would extrapolate the projectile, leading to
        // visible underground / overshoot artifacts on impact.
        [Fact]
        public void Postfix_Projectile_DoesNotExtrapolate()
        {
            var projectile = MockFactory.NewProjectile(id: 200);
            projectile.Pos.X = 5;
            projectile.Pos.Y = 5;
            projectile.Pos.Z = 5;
            projectile.Alive = true;

            var behavior = MockFactory.NewBehavior(projectile);
            behavior.pL = new PositionSnapshot { x = 5, y = 6, z = 5, interval = 1f / 30f };
            behavior.pN = new PositionSnapshot { x = 5, y = 5, z = 5, interval = 1f / 30f };
            behavior.wait = 1;

            EntityInterpolationOptimization.Postfix_OnRenderFrame(behavior, dt: 0.016f);

            // Position must be unchanged — projectile guard returned before any write.
            Assert.Equal(5.0, projectile.Pos.X);
            Assert.Equal(5.0, projectile.Pos.Y);
            Assert.Equal(5.0, projectile.Pos.Z);
        }

        // Same projectile guard, but on the prefix path. The prefix's expected behaviour
        // is to return true (run vanilla) for projectiles. That keeps vanilla's queue
        // bookkeeping intact for the projectile while skipping OptiTime's extrapolation
        // state for it.
        [Fact]
        public void Prefix_Projectile_ReturnsTrueToRunVanilla()
        {
            var projectile = MockFactory.NewProjectile(id: 201);
            // Attributes is null on this uninitialized entity — but the prefix's projectile
            // branch returns before touching Attributes, so this is a faithful repro.

            var behavior = MockFactory.NewBehavior(projectile);
            Vintagestory.API.Common.EnumHandling handled = Vintagestory.API.Common.EnumHandling.PassThrough;
            bool result = EntityInterpolationOptimization.Prefix_OnReceivedServerPos(
                behavior, isTeleport: false, ref handled);

            Assert.True(result);
            Assert.Equal(Vintagestory.API.Common.EnumHandling.PassThrough, handled);
        }
    }
}
