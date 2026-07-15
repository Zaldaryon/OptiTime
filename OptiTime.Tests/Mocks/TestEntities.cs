using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace OptiTime.Tests.Mocks
{
    // Concrete Entity subclass for tests. The base Entity ctor touches several VS systems
    // (atlas, animation manager, behaviour init) that cannot run outside a live game, so we
    // bypass it via RuntimeHelpers.GetUninitializedObject and re-seed only the fields the
    // EntityInterpolationOptimization patch actually reads.
    internal class TestEntity : Entity
    {
        private bool _alive = true;
        public override bool Alive
        {
            get => _alive;
            set => _alive = value;
        }
    }

    // Same base, but with IProjectile to exercise the projectile-exclusion guard.
    internal class TestProjectileEntity : Entity, IProjectile
    {
        private bool _alive = true;
        public override bool Alive
        {
            get => _alive;
            set => _alive = value;
        }

        // IProjectile members — stubs sufficient for the `entity is IProjectile` type test.
        public Entity FiredBy { get; set; }
        public float Damage { get; set; }
        public int DamageTier { get; set; }
        public EnumDamageType DamageType { get; set; }
        public bool IgnoreInvFrames { get; set; }
        public ItemStack ProjectileStack { get; set; }
        public ItemStack WeaponStack { get; set; }
        public float DropOnImpactChance { get; set; }
        public bool DamageStackOnImpact { get; set; }
        public bool Collectible { get; set; }
        public bool EntityHit => false;
        public float Weight { get; set; }
        public bool Stuck { get; set; }

        public void PreInitialize() { }
        public void SetFromConfig(IProjectileJsonConfig config) { }
    }

    internal static class MockFactory
    {
        // Backing field for `public EntityPos Pos { get; private set; } = new EntityPos();`
        // C# compiler-generated name pattern: "<PropertyName>k__BackingField".
        private static readonly FieldInfo posBackingField =
            typeof(Entity).GetField("<Pos>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);

        // Attributes is a public field with `= new SyncedTreeAttribute()` initializer,
        // skipped by GetUninitializedObject.
        private static readonly FieldInfo attributesField =
            typeof(Entity).GetField("Attributes",
                BindingFlags.Public | BindingFlags.Instance);

        public static TestEntity NewTestEntity(long id = 1)
        {
            var entity = (TestEntity)RuntimeHelpers.GetUninitializedObject(typeof(TestEntity));
            posBackingField?.SetValue(entity, new EntityPos());
            attributesField?.SetValue(entity, new SyncedTreeAttribute());
            entity.EntityId = id;
            return entity;
        }

        public static TestProjectileEntity NewProjectile(long id = 2)
        {
            var entity = (TestProjectileEntity)RuntimeHelpers.GetUninitializedObject(typeof(TestProjectileEntity));
            posBackingField?.SetValue(entity, new EntityPos());
            attributesField?.SetValue(entity, new SyncedTreeAttribute());
            entity.EntityId = id;
            return entity;
        }

        public static EntityBehaviorInterpolatePosition NewBehavior(Entity entity)
        {
            var b = (EntityBehaviorInterpolatePosition)
                RuntimeHelpers.GetUninitializedObject(typeof(EntityBehaviorInterpolatePosition));

            // EntityBehavior base has `public Entity entity;` — set it.
            var entityField = typeof(EntityBehavior).GetField("entity",
                BindingFlags.Public | BindingFlags.Instance);
            entityField.SetValue(b, entity);

            // EBI's positionQueue is `Queue<PositionSnapshot>` with a field initializer; init manually.
            var positionQueueField = typeof(EntityBehaviorInterpolatePosition)
                .GetField("positionQueue", BindingFlags.Public | BindingFlags.Instance);
            positionQueueField?.SetValue(b, new System.Collections.Generic.Queue<PositionSnapshot>());

            return b;
        }
    }
}
