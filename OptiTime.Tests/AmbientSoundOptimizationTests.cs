using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class AmbientSoundOptimizationTests : IDisposable
    {
        private static readonly FieldInfo EntityPosBackingField = AccessTools.Field(typeof(Entity), "<Pos>k__BackingField");
        private static readonly FieldInfo ClientMainPlayerField = AccessTools.Field(typeof(ClientMain), "player");
        private static readonly FieldInfo ClientPlayerWorldDataField = AccessTools.Field(typeof(ClientPlayer), "worlddata");
        private static readonly FieldInfo ClientSystemGameField = AccessTools.Field(typeof(ClientSystem), "game");
        private static readonly FieldInfo AmbientSoundsField = AccessTools.Field(typeof(SystemPlayerSounds), "ambientSounds");

        static AmbientSoundOptimizationTests()
        {
            string gameDirectory = FindGameDirectory();
            if (gameDirectory == null)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                string name = new AssemblyName(args.Name).Name + ".dll";
                foreach (string directory in new[] { gameDirectory, Path.Combine(gameDirectory, "Lib"), Path.Combine(gameDirectory, "Mods") })
                {
                    string candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                    {
                        return Assembly.LoadFrom(candidate);
                    }
                }

                return null;
            };
        }

        public AmbientSoundOptimizationTests()
        {
            AmbientSoundOptimization.Cleanup();
        }

        public void Dispose()
        {
            AmbientSoundOptimization.Cleanup();
        }

        [Fact]
        public void UnthrottledTicksReturnTrueForNativeExecutionAndThrottledTicksRespectThresholdAndFallback()
        {
            (SystemPlayerSounds system, EntityPlayer player, RecordingLoadedSound sound) = CreateHarness(0.0, 0.0, 0.0);

            // First tick at origin must not be throttled and must return true so vanilla OnGameTick runs natively.
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
            Assert.Equal(0, sound.SetPositionCalls);
            system.OnGameTick(0.02f);
            Assert.Equal(1, sound.SetPositionCalls);

            // Next 9 stationary ticks are throttled (return false).
            for (int tick = 0; tick < 9; tick++)
            {
                Assert.False(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
            }

            // 10th tick triggers fallback update and returns true for vanilla OnGameTick.
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
            Assert.Equal(1, sound.SetPositionCalls);
            system.OnGameTick(0.02f);
            Assert.Equal(2, sound.SetPositionCalls);

            // Sub-threshold movement (< 0.3 blocks; 0.2^2 = 0.04 < 0.09) remains throttled.
            player.Pos.X = 0.2;
            Assert.False(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));

            // Movement reaching >= 0.3 blocks from last recorded position triggers immediately and resets fallback cadence.
            player.Pos.X = 0.3;
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
            for (int tick = 0; tick < 9; tick++)
            {
                Assert.False(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
            }
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
        }

        [Fact]
        public void DimensionChangeNaNAndNullStateFallbackSafelyToVanilla()
        {
            (SystemPlayerSounds system, EntityPlayer player, _) = CreateHarness(10.0, 64.0, 20.0);

            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
            Assert.False(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));

            // Dimension change alters InternalY by 32768 and must force an immediate update.
            player.Pos.Dimension = 1;
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
            Assert.False(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));

            // NaN coordinate must never falsely throttle.
            player.Pos.X = double.NaN;
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));

            // Null instance / player / pos fall back cleanly to vanilla.
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(null));
            var emptySystem = (SystemPlayerSounds)RuntimeHelpers.GetUninitializedObject(typeof(SystemPlayerSounds));
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(emptySystem));
        }

        [Fact]
        public void CleanupResetsStateForSameCoordinateWorldReloadAndIsWiredToLeaveWorld()
        {
            (SystemPlayerSounds system, _, _) = CreateHarness(100.0, 110.0, 200.0);

            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
            Assert.False(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));

            AmbientSoundOptimization.Cleanup();

            // After Cleanup (e.g. world reload at identical coordinates), first tick must run unthrottled.
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));

            string modSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Core", "OptiTimeMod.cs"));
            int leaveWorldIndex = modSource.IndexOf("private void OnLeaveWorld()", StringComparison.Ordinal);
            Assert.True(leaveWorldIndex >= 0, "OnLeaveWorld handler not found in Core/OptiTimeMod.cs");
            int leaveWorldEnd = modSource.IndexOf("}", leaveWorldIndex, StringComparison.Ordinal);
            string leaveWorldBody = modSource.Substring(leaveWorldIndex, leaveWorldEnd - leaveWorldIndex + 1);
            Assert.Contains("AmbientSoundOptimization.Cleanup();", leaveWorldBody, StringComparison.Ordinal);
        }

        [Fact]
        public void ThrottledAndUnthrottledTicksAllocateZeroBytes()
        {
            (SystemPlayerSounds system, EntityPlayer player, _) = CreateHarness(50.0, 70.0, 80.0);

            // Warm up JIT and static initialization on both unthrottled and throttled paths.
            for (int i = 0; i < 64; i++)
            {
                player.Pos.X = 50.0 + i;
                Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
                Assert.False(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));
            }

            // Reset fallbackCounter to 0 via an unthrottled tick before stationary measurement.
            player.Pos.X = 200.0;
            Assert.True(AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system));

            // Stationary ticks (throttled + periodic fallback): zero bytes allocated.
            int stationaryUnthrottled = 0;
            long stationaryBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                if (AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system))
                {
                    stationaryUnthrottled++;
                }
            }
            long stationaryAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, stationaryAfter - stationaryBefore);
            Assert.Equal(500, stationaryUnthrottled);

            // Moving ticks (unthrottled every tick): zero bytes allocated.
            int movingUnthrottled = 0;
            double baseX = player.Pos.X + 1.0;
            long movingBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                player.Pos.X = baseX + i;
                if (AmbientSoundOptimization.ThrottleAmbientSoundUpdates(system))
                {
                    movingUnthrottled++;
                }
            }
            long movingAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, movingAfter - movingBefore);
            Assert.Equal(5000, movingUnthrottled);
        }

        private static (SystemPlayerSounds System, EntityPlayer Player, RecordingLoadedSound Sound) CreateHarness(double x, double y, double z)
        {
            var player = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
            EntityPosBackingField.SetValue(player, new EntityPos(x, y, z));
            player.EntityId = 1;

            var worldData = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
            worldData.EntityPlayer = player;

            var clientPlayer = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
            ClientPlayerWorldDataField.SetValue(clientPlayer, worldData);

            var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            ClientMainPlayerField.SetValue(game, clientPlayer);

            var recordingSound = new RecordingLoadedSound();
            var ambientSound = new AmbientSound
            {
                AssetLoc = new AssetLocation("game", "sounds/environment/wind.ogg"),
                SectionPos = new FastVec3i(0, 0, 0),
                Sound = recordingSound,
                BoundingBoxes = new List<Cuboidi> { new Cuboidi(0, 0, 0, 16, 16, 16) }
            };

            var sounds = new Dictionary<AmbientSound, AmbientSound>
            {
                [ambientSound] = ambientSound
            };

            var system = (SystemPlayerSounds)RuntimeHelpers.GetUninitializedObject(typeof(SystemPlayerSounds));
            ClientSystemGameField.SetValue(system, game);
            AmbientSoundsField.SetValue(system, sounds);

            return (system, player, recordingSound);
        }

        private sealed class RecordingLoadedSound : ILoadedSound
        {
            public int SetPositionCalls;
            public Vec3f LastPosition;

            public SoundParams Params => null;
            public bool IsDisposed => false;
            public bool IsPlaying => true;
            public bool IsFadingIn => false;
            public bool IsFadingOut => false;
            public bool HasStopped => false;
            public bool HasReverbStopped(long elapsedMilliseconds) => false;
            public bool IsPaused => false;
            public bool IsReady => true;
            public float SoundLengthSeconds => 1f;
            public float PlaybackPosition { get; set; }
            public int Channels => 1;

            public void Start() { }
            public void Stop() { }
            public void Pause() { }
            public void SetLooping(bool on) { }
            public void SetPosition(float x, float y, float z) { SetPositionCalls++; LastPosition = new Vec3f(x, y, z); }
            public void SetPosition(Vec3f position) { SetPositionCalls++; LastPosition = position; }
            public void SetPitch(float val) { }
            public void SetPitchOffset(float val) { }
            public void SetVolume(float val) { }
            public void SetVolume() { }
            public void Toggle(bool on) { }
            public void FadeIn(float seconds, Action<ILoadedSound> onFaded) { }
            public void FadeOut(float seconds, Action<ILoadedSound> onFaded) { }
            public void FadeTo(double newVolume, float duration, Action<ILoadedSound> onFaded) { }
            public void FadeOutAndStop(float seconds) { }
            public void SetReverb(float reverbDecayTime) { }
            public void SetLowPassfiltering(float value) { }
            public void Dispose() { }
        }

        private static string FindRepositoryRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "OptiTime.csproj")))
                {
                    return dir;
                }

                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate OptiTime repository root.");
        }

        private static string FindGameDirectory()
        {
            string env = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "VintagestoryLib.dll")))
            {
                return env;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Path.Combine(home, ".local", "share", "vintagestory");
            return File.Exists(Path.Combine(local, "VintagestoryLib.dll")) ? local : null;
        }
    }
}
