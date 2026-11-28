using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using OptiTime.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class SessionConflictAndParticleTests : IDisposable
    {
        private static readonly string[] OptiTimeShaderPaths =
        {
            "shaders/bilateralblur.fsh",
            "shaders/blur.fsh",
            "shaders/blur.vsh",
            "shaders/chunkliquid.fsh",
            "shaders/chunkopaque.fsh",
            "shaders/chunkshadowmap.fsh",
            "shaders/godrays.fsh",
            "shaders/ssao.fsh"
        };

        private readonly List<string> tempDirectories = new();

        public SessionConflictAndParticleTests()
        {
            ParticleOptimization.Cleanup();
        }

        public void Dispose()
        {
            AccessTools.Property(typeof(OptiTimeMod), nameof(OptiTimeMod.Config)).SetValue(null, null);
            ParticleOptimization.Cleanup();
            foreach (string dir in tempDirectories)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void ConflictAndPlatformHandlersKeepPersistedConfigIntact()
        {
            string modSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Core", "OptiTimeMod.cs"));

            string detectSection = Section(modSource, "private void DetectAndHandleConflicts", "private void HandleProfileCommand");
            Assert.DoesNotContain(".Save(", detectSection);
            Assert.Contains("ForceOffForSession", detectSection);

            string disableHelper = Section(modSource, "private void DisableOptimizationForConflict", "private void InitializeOptimizations");
            Assert.DoesNotContain(".Save(", disableHelper);
            Assert.Contains("ForceOffForSession", disableHelper);

            string startClientSection = Section(modSource, "public override void StartClientSide", "private void OnLevelFinalize");
            string nonWindowsSection = Section(startClientSection, "if (Environment.OSVersion.Platform != PlatformID.Win32NT)", "else");
            Assert.DoesNotContain(".Save(", nonWindowsSection);
            Assert.Contains("ForceOffForSession", nonWindowsSection);

            string guiConflictSection = Section(startClientSection, "bool conflict =", "if (config.GuiManagerInputNoLinqEnabled)");
            Assert.DoesNotContain(".Save(", guiConflictSection);
            Assert.Contains("ForceOffForSession", guiConflictSection);

            string guiInputConflictSection = Section(startClientSection, "bool inputConflict =", "if (config.HandbookOptimizations)");
            Assert.DoesNotContain(".Save(", guiInputConflictSection);
            Assert.Contains("ForceOffForSession", guiInputConflictSection);

            var config = new OptiTimeConfig
            {
                ParticleViewDistanceScalingEnabled = true,
                GuiManagerNoLinqEnabled = true,
                GuiManagerInputNoLinqEnabled = true,
                EntityAnimationOptimizations = true,
                HandbookOptimizations = true,
                ShaderOptimizations = true,
                PreciseFramePacingEnabled = true,
                BlurOptimizationEnabled = true
            };

            string[] targets =
            {
                nameof(OptiTimeConfig.ParticleViewDistanceScalingEnabled),
                nameof(OptiTimeConfig.GuiManagerNoLinqEnabled),
                nameof(OptiTimeConfig.GuiManagerInputNoLinqEnabled),
                nameof(OptiTimeConfig.EntityAnimationOptimizations),
                nameof(OptiTimeConfig.HandbookOptimizations),
                nameof(OptiTimeConfig.ShaderOptimizations),
                nameof(OptiTimeConfig.PreciseFramePacingEnabled),
                nameof(OptiTimeConfig.BlurOptimizationEnabled)
            };

            foreach (string name in targets)
            {
                config.ForceOffForSession(name, "Session conflict");
                Assert.False(config.GetOptimizationValue(name), $"{name} should be false for the active session.");
                Assert.True(config.IsConflictDisabled(name), $"{name} should report conflict disabled.");
                Assert.True(config.IsForcedOffForSession(name), $"{name} should report forced off for session.");
            }

            OptiTimeConfig persisted = config.PersistedView();
            foreach (string name in targets)
            {
                Assert.True(persisted.GetOptimizationValue(name), $"{name} must remain true in PersistedView().");
            }
        }

        [Fact]
        public void CoriaenderShadersKeepsBlurActiveAcrossConsecutiveLaunchesAndRestoresAfterRemoval()
        {
            string gameDir = FindGameDirectory();
            Assert.NotNull(gameDir);

            string dataPath = CreateTempDirectory();
            var enabledMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "coriaendershaders" };

            // Launch 1: CoriaenderShaders present, ShaderOptimizations=true, BlurOptimizationEnabled=true
            using (var session1 = CreateFakeSession(gameDir, dataPath, enabledMods, extraOrigins: Array.Empty<FakeAssetOrigin>()))
            {
                session1.Boot();
                Assert.True(IsOptiTimeShaderActive(session1.Assets, "shaders/blur.fsh"));
                Assert.True(IsOptiTimeShaderActive(session1.Assets, "shaders/blur.vsh"));
                foreach (string worldShader in OptiTimeShaderPaths.Where(p => !p.StartsWith("shaders/blur.", StringComparison.OrdinalIgnoreCase)))
                {
                    Assert.False(IsOptiTimeShaderActive(session1.Assets, worldShader), $"{worldShader} should be hidden under CoriaenderShaders.");
                }
                Assert.True(OptiTimeMod.Config.IsConflictDisabled(nameof(OptiTimeConfig.ShaderOptimizations)));
            }

            OptiTimeConfig savedAfterLaunch1 = ReadSavedConfig(dataPath);
            Assert.True(savedAfterLaunch1.ShaderOptimizations);
            Assert.True(savedAfterLaunch1.BlurOptimizationEnabled);

            // Launch 2: CoriaenderShaders still present, loading OptiTime.json saved by Launch 1
            using (var session2 = CreateFakeSession(gameDir, dataPath, enabledMods, extraOrigins: Array.Empty<FakeAssetOrigin>()))
            {
                session2.Boot();
                Assert.True(IsOptiTimeShaderActive(session2.Assets, "shaders/blur.fsh"));
                Assert.True(IsOptiTimeShaderActive(session2.Assets, "shaders/blur.vsh"));
                foreach (string worldShader in OptiTimeShaderPaths.Where(p => !p.StartsWith("shaders/blur.", StringComparison.OrdinalIgnoreCase)))
                {
                    Assert.False(IsOptiTimeShaderActive(session2.Assets, worldShader), $"{worldShader} should stay hidden on Launch 2 under CoriaenderShaders.");
                }
            }

            // Launch 3: CoriaenderShaders removed, loading the same OptiTime.json
            enabledMods.Clear();
            using (var session3 = CreateFakeSession(gameDir, dataPath, enabledMods, extraOrigins: Array.Empty<FakeAssetOrigin>()))
            {
                session3.Boot();
                foreach (string shaderPath in OptiTimeShaderPaths)
                {
                    Assert.True(IsOptiTimeShaderActive(session3.Assets, shaderPath), $"{shaderPath} should recover once CoriaenderShaders is removed.");
                }
                Assert.True(OptiTimeMod.Config.ShaderOptimizations);
                Assert.False(OptiTimeMod.Config.IsConflictDisabled(nameof(OptiTimeConfig.ShaderOptimizations)));
            }
        }

        [Fact]
        public void UniversalShaderUnderneathCheckHidesModifiedBaseShadersAndKeepsBlurPairAtomic()
        {
            string gameDir = FindGameDirectory();
            Assert.NotNull(gameDir);

            // Subcase A: all 4 underlying shaders match vanilla -> all 4 OptiTime overrides stay active
            string dataA = CreateTempDirectory();
            using (var sessionA = CreateFakeSession(gameDir, dataA, new HashSet<string>(), extraOrigins: Array.Empty<FakeAssetOrigin>()))
            {
                sessionA.Boot();
                foreach (string shaderPath in OptiTimeShaderPaths)
                {
                    Assert.True(IsOptiTimeShaderActive(sessionA.Assets, shaderPath), $"Vanilla underneath should keep {shaderPath} active.");
                }
            }

            // Subcase B: an external origin underneath modifies shaders/chunkliquid.fsh
            string dataB = CreateTempDirectory();
            var modifiedLiquidOrigin = new FakeAssetOrigin("/mods/customshaderpack/assets");
            modifiedLiquidOrigin.AddShader("shaders/chunkliquid.fsh", Encoding.UTF8.GetBytes("// custom pack chunkliquid\n"));
            using (var sessionB = CreateFakeSession(gameDir, dataB, new HashSet<string>(), extraOrigins: new[] { modifiedLiquidOrigin }))
            {
                sessionB.Boot();
                Assert.False(IsOptiTimeShaderActive(sessionB.Assets, "shaders/chunkliquid.fsh"));
                Assert.Same(modifiedLiquidOrigin, sessionB.Assets.AllAssets[new AssetLocation("game", "shaders/chunkliquid.fsh")].Origin);
                Assert.True(IsOptiTimeShaderActive(sessionB.Assets, "shaders/chunkshadowmap.fsh"));
                Assert.True(IsOptiTimeShaderActive(sessionB.Assets, "shaders/blur.fsh"));
                Assert.True(IsOptiTimeShaderActive(sessionB.Assets, "shaders/blur.vsh"));
            }

            // Subcase C: an external origin underneath modifies ONLY shaders/blur.fsh -> both blur.fsh and blur.vsh must hide atomically
            string dataC = CreateTempDirectory();
            var modifiedBlurOrigin = new FakeAssetOrigin("/mods/customblurpack/assets");
            modifiedBlurOrigin.AddShader("shaders/blur.fsh", Encoding.UTF8.GetBytes("// custom blur fragment\n"));
            using (var sessionC = CreateFakeSession(gameDir, dataC, new HashSet<string>(), extraOrigins: new[] { modifiedBlurOrigin }))
            {
                sessionC.Boot();
                Assert.False(IsOptiTimeShaderActive(sessionC.Assets, "shaders/blur.fsh"));
                Assert.False(IsOptiTimeShaderActive(sessionC.Assets, "shaders/blur.vsh"));
                Assert.False(OptiTimeMod.Config.BlurOptimizationEnabled);
                Assert.True(OptiTimeMod.Config.IsConflictDisabled(nameof(OptiTimeConfig.BlurOptimizationEnabled)));
                Assert.True(OptiTimeMod.Config.PersistedView().BlurOptimizationEnabled);
                Assert.True(IsOptiTimeShaderActive(sessionC.Assets, "shaders/chunkliquid.fsh"));
                Assert.True(IsOptiTimeShaderActive(sessionC.Assets, "shaders/chunkshadowmap.fsh"));
            }
        }

        [Fact]
        public void ParticleOptimizationsAndViewDistanceScalingOperateIndependently()
        {
            var pool = (ParticlePoolQuads)RuntimeHelpers.GetUninitializedObject(typeof(ParticlePoolQuads));
            AccessTools.Field(typeof(ParticlePoolQuads), "poolSize").SetValue(pool, 100);
            pool.QuantityAlive = 100;

            var props = new SimpleParticleProperties
            {
                IgnoreUserConfig = false,
                MinPos = new Vintagestory.API.MathTools.Vec3d(0, 64, 0)
            };

            var module = new ModuleParticles();
            try
            {
                ParticleOptimization.UpdateViewDistance(512);

                // Subcase A: ParticleOptimizations=false must disable ALL culling even if scaling flag is true
                var disabledConfig = new OptiTimeConfig
                {
                    ParticleOptimizations = false,
                    ParticleViewDistanceScalingEnabled = true
                };
                AccessTools.Property(typeof(OptiTimeMod), nameof(OptiTimeMod.Config)).SetValue(null, disabledConfig);
                OptiTimeMod.ApplyRuntimeConfig();
                module.Enable();
                module.Reset();

                for (int i = 0; i < 200; i++)
                {
                    int result = -1;
                    bool runVanilla = ParticleOptimization.SpawnParticlesPrefix(pool, props, ref result);
                    Assert.True(runVanilla);
                }

                long requestedDisabled = ReadModuleCounter("spawnsRequested");
                Assert.Equal(0, requestedDisabled);

                // Subcase B: ParticleOptimizations=true, ParticleViewDistanceScalingEnabled=false
                // Must disable view-distance rejection (byVD == 0) while keeping occupancy culling active (byOccupancy > 0)
                var cullingOnlyConfig = new OptiTimeConfig
                {
                    ParticleOptimizations = true,
                    ParticleViewDistanceScalingEnabled = false
                };
                AccessTools.Property(typeof(OptiTimeMod), nameof(OptiTimeMod.Config)).SetValue(null, cullingOnlyConfig);
                OptiTimeMod.ApplyRuntimeConfig();
                module.Reset();

                for (int i = 0; i < 400; i++)
                {
                    int result = -1;
                    ParticleOptimization.SpawnParticlesPrefix(pool, props, ref result);
                }

                long requestedEnabled = ReadModuleCounter("spawnsRequested");
                long culledByVd = ReadModuleCounter("spawnsCulledViewDist");
                long culledByOccupancy = ReadModuleCounter("spawnsCulledOccupancy");

                Assert.Equal(400, requestedEnabled);
                Assert.Equal(0, culledByVd);
                Assert.True(culledByOccupancy > 0, "Occupancy culling must remain active when view-distance scaling is disabled.");
            }
            finally
            {
                module.Disable();
                ParticleOptimization.Cleanup();
            }
        }

        private static long ReadModuleCounter(string fieldName)
        {
            FieldInfo field = typeof(ModuleParticles).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            return (long)field.GetValue(null);
        }

        private string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "optitime-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            tempDirectories.Add(dir);
            return dir;
        }

        private static OptiTimeConfig ReadSavedConfig(string dataPath)
        {
            string path = Path.Combine(dataPath, "ModConfig", "OptiTime.json");
            Assert.True(File.Exists(path), $"Expected saved config at {path}");
            return JsonConvert.DeserializeObject<OptiTimeConfig>(File.ReadAllText(path));
        }

        private static bool IsOptiTimeShaderActive(IAssetManager assets, string shaderPath)
        {
            if (!assets.AllAssets.TryGetValue(new AssetLocation("game", shaderPath), out IAsset asset))
                return false;
            return OptiTimeMod.IsModOriginPath(asset.Origin?.OriginPath, "OptiTime-1.5.18.zip", "optitime");
        }

        private static FakeClientSession CreateFakeSession(
            string gameDir,
            string dataPath,
            HashSet<string> enabledMods,
            FakeAssetOrigin[] extraOrigins)
        {
            var vanillaOrigin = new FakeAssetOrigin(Path.Combine(gameDir, "assets", "game"));
            var optitimeOrigin = new FakeAssetOrigin("/tmp/mods/OptiTime-1.5.18.zip/assets");

            foreach (string path in OptiTimeShaderPaths)
            {
                string gameShaderPath = Path.Combine(gameDir, "assets", "game", path);
                vanillaOrigin.AddShader(path, File.ReadAllBytes(gameShaderPath));

                string modShaderPath = Path.Combine(FindRepositoryRoot(), "assets", "game", path);
                optitimeOrigin.AddShader(path, File.ReadAllBytes(modShaderPath));
            }
            vanillaOrigin.AddShader("shaders/chunkshadowmap.vsh", File.ReadAllBytes(Path.Combine(gameDir, "assets", "game", "shaders", "chunkshadowmap.vsh")));

            var origins = new List<IAssetOrigin> { vanillaOrigin };
            origins.AddRange(extraOrigins);
            origins.Add(optitimeOrigin);

            var assetManager = new FakeAssetManager(origins);
            assetManager.Reload(AssetCategory.shaders);
            assetManager.Reload(AssetCategory.shaderincludes);

            var logger = new NullLogger();
            if (!Lang.AvailableLanguages.ContainsKey("en"))
            {
                Lang.AvailableLanguages["en"] = new TranslationService("en", logger, assetManager);
            }
            Lang.ChangeLanguage("en");

            var modLoader = DispatchProxy.Create<IModLoader, FakeModLoaderProxy>();
            ((FakeModLoaderProxy)(object)modLoader).EnabledMods = enabledMods;

            var eventApi = DispatchProxy.Create<IClientEventAPI, FakeEventApiProxy>();

            var clientApi = DispatchProxy.Create<ICoreClientAPI, FakeCoreClientApiProxy>();
            var clientProxy = (FakeCoreClientApiProxy)(object)clientApi;
            clientProxy.DataBasePath = dataPath;
            clientProxy.Assets = assetManager;
            clientProxy.Logger = logger;
            clientProxy.ModLoader = modLoader;
            clientProxy.Event = eventApi;

            var fakeMod = (FakeMod)RuntimeHelpers.GetUninitializedObject(typeof(FakeMod));
            fakeMod.SetInfo("OptiTime-1.5.18.zip", "optitime");

            var modSystem = new OptiTimeMod();
            PropertyInfo modProp = typeof(ModSystem).GetProperty(nameof(ModSystem.Mod));
            modProp.SetValue(modSystem, fakeMod);

            return new FakeClientSession(modSystem, clientApi, assetManager);
        }

        private sealed class FakeClientSession : IDisposable
        {
            private readonly OptiTimeMod mod;
            private readonly ICoreClientAPI api;

            public FakeClientSession(OptiTimeMod mod, ICoreClientAPI api, IAssetManager assets)
            {
                this.mod = mod;
                this.api = api;
                Assets = assets;
            }

            public IAssetManager Assets { get; }

            public void Boot()
            {
                mod.Start(api);
                mod.AssetsLoaded(api);
                mod.StartClientSide(api);
            }

            public void Dispose()
            {
                mod.Dispose();
            }
        }

        private sealed class FakeMod : Mod
        {
            private string fileName;
            private ModInfo info;

            public void SetInfo(string modFileName, string modId)
            {
                fileName = modFileName;
                info = new ModInfo
                {
                    Type = EnumModType.Code,
                    Name = "OptiTime",
                    ModID = modId,
                    Version = "1.5.18"
                };
                typeof(Mod).GetProperty(nameof(FileName))?.SetValue(this, fileName);
                typeof(Mod).GetProperty(nameof(Info))?.SetValue(this, info);
            }
        }

        private sealed class FakeAsset : IAsset
        {
            public FakeAsset(AssetLocation location, IAssetOrigin origin, byte[] data)
            {
                Location = location;
                Origin = origin;
                Data = data;
            }

            public string Name => Path.GetFileName(Location.Path);
            public AssetLocation Location { get; }
            public IAssetOrigin Origin { get; set; }
            public byte[] Data { get; set; }
            public bool IsPatched { get; set; }
            public bool IsLoaded() => Data != null;
            public string ToText() => Encoding.UTF8.GetString(Data);
            public T ToObject<T>(JsonSerializerSettings settings = null) => JsonConvert.DeserializeObject<T>(ToText(), settings);
            public BitmapRef ToBitmap(ICoreClientAPI capi) => null;
        }

        private sealed class FakeAssetOrigin : IAssetOrigin
        {
            private readonly Dictionary<string, byte[]> shaders = new(StringComparer.OrdinalIgnoreCase);

            public FakeAssetOrigin(string originPath)
            {
                OriginPath = originPath;
            }

            public string OriginPath { get; }

            public void AddShader(string path, byte[] data)
            {
                shaders[path] = data;
            }

            public void LoadAsset(IAsset asset)
            {
                if (asset != null && shaders.TryGetValue(asset.Location.Path, out byte[] data))
                    asset.Data = data;
            }

            public bool TryLoadAsset(IAsset asset)
            {
                if (asset != null && shaders.TryGetValue(asset.Location.Path, out byte[] data))
                {
                    asset.Data = data;
                    return true;
                }
                return false;
            }

            public List<IAsset> GetAssets(AssetCategory category, bool shouldLoad = true)
            {
                if (category != AssetCategory.shaders && category != AssetCategory.shaderincludes)
                    return new List<IAsset>();

                var list = new List<IAsset>(shaders.Count);
                foreach ((string path, byte[] data) in shaders)
                {
                    var loc = new AssetLocation("game", path);
                    if (loc.Category == category)
                    {
                        list.Add(new FakeAsset(loc, this, shouldLoad ? data : null));
                    }
                }
                return list;
            }

            public List<IAsset> GetAssets(AssetLocation baseLocation, bool shouldLoad = true) => new();
            public bool IsAllowedToAffectGameplay() => true;
        }

        private sealed class FakeAssetManager : IAssetManager
        {
            public FakeAssetManager(List<IAssetOrigin> origins)
            {
                Origins = origins;
            }

            public Dictionary<AssetLocation, IAsset> AllAssets { get; } = new();
            public List<IAssetOrigin> Origins { get; }

            public bool Exists(AssetLocation location) => AllAssets.ContainsKey(location);
            public void Add(AssetLocation path, IAsset asset) => AllAssets[path] = asset;
            public IAsset Get(AssetLocation location) => AllAssets[location];
            public IAsset TryGet(AssetLocation location, bool loadAsset = true) => AllAssets.TryGetValue(location, out IAsset asset) ? asset : null;
            public List<IAsset> GetMany(string pathBegins, string domain = null, bool loadAsset = true) => new();
            public List<IAsset> GetManyInCategory(string categoryCode, string pathBegins, string domain = null, bool loadAsset = true) => new();
            public Dictionary<AssetLocation, T> GetMany<T>(ILogger logger, string pathBegins, string domain = null) => new();
            public List<AssetLocation> GetLocations(string pathBegins, string domain = null) => new();
            public T Get<T>(AssetLocation location) => AllAssets[location].ToObject<T>();
            public int Reload(AssetLocation baseLocation) => 0;
            public void AddPathOrigin(string domain, string fullPath) { }
            public void AddModOrigin(string domain, string fullPath) { }
            public void AddModOrigin(string domain, string fullPath, string pathForReservedCharsCheck) { }
            public void RemoveModOrigin(string domain, string fullpath) { }

            public int Reload(AssetCategory category)
            {
                foreach (AssetLocation key in AllAssets.Keys.Where(k => k.Category == category).ToList())
                    AllAssets.Remove(key);

                int count = 0;
                foreach (IAssetOrigin origin in Origins)
                {
                    foreach (IAsset asset in origin.GetAssets(category, true))
                    {
                        AllAssets[asset.Location] = asset;
                        count++;
                    }
                }
                return count;
            }
        }

        public class FakeModLoaderProxy : DispatchProxy
        {
            public HashSet<string> EnabledMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IModLoader.IsModEnabled) && args?.Length == 1 && args[0] is string modId)
                    return EnabledMods.Contains(modId);

                if (targetMethod.ReturnType == typeof(bool))
                    return false;

                return targetMethod.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        public class FakeEventApiProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        public class FakeCoreClientApiProxy : DispatchProxy
        {
            public string DataBasePath { get; set; }
            public IAssetManager Assets { get; set; }
            public ILogger Logger { get; set; }
            public IModLoader ModLoader { get; set; }
            public IClientEventAPI Event { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case "get_Side":
                        return EnumAppSide.Client;
                    case "get_DataBasePath":
                        return DataBasePath;
                    case "get_Assets":
                        return Assets;
                    case "get_Logger":
                        return Logger;
                    case "get_ModLoader":
                        return ModLoader;
                    case "get_Event":
                        return Event;
                    case nameof(ICoreClientAPI.StoreModConfig):
                    {
                        string path = Path.Combine(DataBasePath, "ModConfig", (string)args[1]);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, JsonConvert.SerializeObject(args[0], Formatting.Indented));
                        return null;
                    }
                    case nameof(ICoreClientAPI.LoadModConfig):
                    {
                        string path = Path.Combine(DataBasePath, "ModConfig", (string)args[0]);
                        if (!File.Exists(path))
                            return null;
                        Type targetType = targetMethod.IsGenericMethod ? targetMethod.GetGenericArguments()[0] : typeof(object);
                        return JsonConvert.DeserializeObject(File.ReadAllText(path), targetType);
                    }
                    case nameof(ICoreClientAPI.RegisterCommand):
                        return true;
                }

                if (targetMethod.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType)
                    return Activator.CreateInstance(targetMethod.ReturnType);
                return null;
            }
        }

        private static string Section(string source, string start, string end)
        {
            int from = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, $"Could not find {start}");
            int to = source.IndexOf(end, from, StringComparison.Ordinal);
            Assert.True(to > from, $"Could not find {end} after {start}");
            return source.Substring(from, to - from);
        }

        private static string FindGameDirectory()
        {
            foreach (string candidate in new[]
            {
                Environment.GetEnvironmentVariable("VINTAGE_STORY"),
                Environment.GetEnvironmentVariable("VINTAGE_STORY_HOME"),
                Environment.GetEnvironmentVariable("VINTAGE_STORY_PATH"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "VintageStory"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "vintagestory")
            })
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(Path.Combine(candidate, "VintagestoryAPI.dll")))
                    return candidate;
            }
            return null;
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OptiTime.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not find the OptiTime repository root.");
        }
    }
}
