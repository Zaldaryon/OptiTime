using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class SearchAndHandbookExceptionIsolationTests : IDisposable
    {
        private readonly List<string> tempDirectories = new();
        private readonly List<OptiTimeMod> bootedMods = new();

        static SearchAndHandbookExceptionIsolationTests()
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

            foreach (string relative in new[] { "VintagestoryLib.dll", "Mods/VSEssentials.dll", "Mods/VSSurvivalMod.dll" })
            {
                string path = Path.Combine(gameDirectory, relative);
                if (File.Exists(path) && !AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal)))
                {
                    Assembly.LoadFrom(path);
                }
            }
        }

        public void Dispose()
        {
            foreach (OptiTimeMod mod in bootedMods)
            {
                try
                {
                    mod.Dispose();
                }
                catch
                {
                }
            }
            bootedMods.Clear();

            AccessTools.Property(typeof(OptiTimeMod), nameof(OptiTimeMod.Config)).SetValue(null, null);

            foreach (string dir in tempDirectories)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void CreativeSearchCache_IsolatesThrowingCollectiblesAndFallsBackToName()
        {
            var logger = CreateLogger();
            BootModSession(logger.Instance, new Dictionary<string, object>());

            var clientApi = CreateUninitializedClientApi();
            IWorldAccessor world = CreateWorld(clientApi, logger.Instance);

            var inv = new InventoryGeneric(4, "creative-test", null);
            inv[0].Itemstack = new ItemStack(new MockCollectible("test:healthy", "Copper Ingot", "Smelted metal"));
            inv[1].Itemstack = new ItemStack(new ThrowingDescriptionCollectible("test:broken-desc", "Broken Widget"));
            inv[2].Itemstack = new ItemStack(new ThrowingSearchTextCollectible("test:broken-search", "Broken Search Item"));
            inv[3].Itemstack = new ItemStack(new ThrowingNameCollectible("test:broken-name"));

            var tab = new CreativeTab("test", inv);
            Dictionary<int, string> searchCache = tab.CreateSearchCache(world);

            Assert.NotNull(searchCache);
            Assert.NotNull(tab.SearchCacheNames);
            Assert.Equal(4, searchCache.Count);
            Assert.Equal(4, tab.SearchCacheNames.Count);

            Assert.Contains("copper ingot", searchCache[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("smelted metal", searchCache[0], StringComparison.OrdinalIgnoreCase);

            Assert.Equal("Broken Widget", searchCache[1]);
            Assert.Equal("broken widget", tab.SearchCacheNames[1]);

            Assert.Equal("Broken Search Item", searchCache[2]);
            Assert.Equal("broken search item", tab.SearchCacheNames[2]);

            Assert.Equal("test:broken-name", searchCache[3]);
            Assert.Equal("test:broken-name", tab.SearchCacheNames[3]);

            Assert.Contains(logger.Warnings, w => w.Contains("[OptiTime]", StringComparison.Ordinal));
        }

        [Fact]
        public void CreativeSearchCache_ExitsCleanlyWithoutWarningWhenClientApiDisposesMidLoop()
        {
            var logger = CreateLogger();
            BootModSession(logger.Instance, new Dictionary<string, object>());

            var clientApi = CreateUninitializedClientApi();
            IWorldAccessor world = CreateWorld(clientApi, logger.Instance);
            bool thirdEvaluated = false;

            var inv = new InventoryGeneric(3, "creative-dispose", null);
            inv[0].Itemstack = new ItemStack(new MockCollectible("test:first", "First Item", "Valid description"));
            inv[1].Itemstack = new ItemStack(new DisposeTriggeringCollectible("test:disposing", "Disposing Item", () => clientApi.disposed = true));
            inv[2].Itemstack = new ItemStack(new CallbackCollectible("test:third", "Third Item", () => thirdEvaluated = true));

            var tab = new CreativeTab("test", inv);
            Dictionary<int, string> searchCache = tab.CreateSearchCache(world);

            Assert.NotNull(searchCache);
            Assert.NotNull(tab.SearchCacheNames);
            Assert.False(thirdEvaluated);
            Assert.Equal(tab.SearchCacheNames.Keys.OrderBy(k => k), searchCache.Keys.OrderBy(k => k));
            Assert.True(searchCache.ContainsKey(0));
            Assert.False(searchCache.ContainsKey(1));
            Assert.DoesNotContain(logger.Warnings, w => w.Contains("[OptiTime]", StringComparison.Ordinal));
        }

        [Fact]
        public void CreativeSearchCache_LeavesNonClientSideUntouched()
        {
            var logger = CreateLogger();
            BootModSession(logger.Instance, new Dictionary<string, object>());

            var serverApi = DispatchProxy.Create<ICoreAPI, FakeServerApiProxy>();
            IWorldAccessor serverWorld = CreateWorld(serverApi, logger.Instance);

            var inv = new InventoryGeneric(1, "server-test", null);
            inv[0].Itemstack = new ItemStack(new MockCollectible("test:healthy", "Copper Ingot", "Smelted metal"));

            var tab = new CreativeTab("test", inv);
            Assert.ThrowsAny<InvalidCastException>(() => tab.CreateSearchCache(serverWorld));
        }

        [Fact]
        public void HandbookCreatePagesAsync_SkipsThrowingItemStackPageAndCompletesRemainingPages()
        {
            var logger = CreateLogger();
            var objectCache = new Dictionary<string, object>();
            ICoreClientAPI clientApi = BootModSession(logger.Instance, objectCache);

            Type recipeType = AccessTools.TypeByName("Vintagestory.GameContent.CookingRecipe");
            Assert.NotNull(recipeType);
            objectCache["handbookpierecipes"] = Array.CreateInstance(recipeType, 0);

            var healthy0 = new ItemStack(new MockCollectible("test:hb-0", "Healthy Zero", "First info"));
            var broken1 = new ItemStack(new ThrowingDescriptionCollectible("test:hb-1", "Broken One"));
            var healthy2 = new ItemStack(new MockCollectible("test:hb-2", "Healthy Two", "Third info"));
            objectCache["handbookallstacks"] = new[] { healthy0, broken1, healthy2 };

            object handbookSystem = CreateHandbookSystem(clientApi);
            MethodInfo onCreatePagesAsync = AccessTools.Method(handbookSystem.GetType(), "onCreatePagesAsync");
            Assert.NotNull(onCreatePagesAsync);

            var pages = (IList)onCreatePagesAsync.Invoke(handbookSystem, null);
            Assert.NotNull(pages);
            Assert.Equal(2, pages.Count);

            FieldInfo stackField = AccessTools.Field(AccessTools.TypeByName("Vintagestory.GameContent.GuiHandbookItemStackPage"), "Stack");
            PropertyInfo visibleProp = AccessTools.Property(AccessTools.TypeByName("Vintagestory.GameContent.GuiHandbookPage"), "Visible");

            Assert.Same(healthy0, stackField.GetValue(pages[0]));
            Assert.True((bool)visibleProp.GetValue(pages[0]));
            Assert.Same(healthy2, stackField.GetValue(pages[1]));
            Assert.True((bool)visibleProp.GetValue(pages[1]));
            Assert.Contains(logger.Warnings, w => w.Contains("[OptiTime]", StringComparison.Ordinal));
        }

        [Fact]
        public void HandbookCreatePagesAsync_ExitsCleanlyWithoutWarningWhenClientShutsDownMidConstructor()
        {
            var logger = CreateLogger();
            var objectCache = new Dictionary<string, object>();
            bool shuttingDown = false;
            ICoreClientAPI clientApi = BootModSession(logger.Instance, objectCache, () => shuttingDown);

            Type recipeType = AccessTools.TypeByName("Vintagestory.GameContent.CookingRecipe");
            Assert.NotNull(recipeType);
            objectCache["handbookpierecipes"] = Array.CreateInstance(recipeType, 0);

            bool thirdEvaluated = false;
            var healthy0 = new ItemStack(new MockCollectible("test:hb-s0", "Healthy Zero", "First info"));
            var disposing1 = new ItemStack(new DisposeTriggeringCollectible("test:hb-s1", "Shutdown Item", () => shuttingDown = true));
            var third2 = new ItemStack(new CallbackCollectible("test:hb-s2", "Third Two", () => thirdEvaluated = true));
            objectCache["handbookallstacks"] = new[] { healthy0, disposing1, third2 };

            object handbookSystem = CreateHandbookSystem(clientApi);
            MethodInfo onCreatePagesAsync = AccessTools.Method(handbookSystem.GetType(), "onCreatePagesAsync");
            Assert.NotNull(onCreatePagesAsync);

            var pages = (IList)onCreatePagesAsync.Invoke(handbookSystem, null);
            Assert.NotNull(pages);
            Assert.Single(pages);
            Assert.False(thirdEvaluated);
            Assert.DoesNotContain(logger.Warnings, w => w.Contains("[OptiTime]", StringComparison.Ordinal));
        }

        private ICoreClientAPI BootModSession(
            ILogger logger,
            Dictionary<string, object> objectCache,
            Func<bool> isShuttingDown = null)
        {
            string dataPath = CreateTempDirectory();

            var assetManager = new FakeAssetManager();
            if (!Lang.AvailableLanguages.ContainsKey("en"))
            {
                Lang.AvailableLanguages["en"] = new TranslationService("en", logger, assetManager);
            }
            Lang.ChangeLanguage("en");

            Type recipeRegistryType = AccessTools.TypeByName("Vintagestory.GameContent.RecipeRegistrySystem");
            Assert.NotNull(recipeRegistryType);
            object recipeRegistry = Activator.CreateInstance(recipeRegistryType);

            var modLoader = DispatchProxy.Create<IModLoader, FakeModLoaderProxy>();
            ((FakeModLoaderProxy)(object)modLoader).RecipeRegistry = recipeRegistry;

            var eventApi = DispatchProxy.Create<IClientEventAPI, FakeEventApiProxy>();

            var clientApi = DispatchProxy.Create<ICoreClientAPI, FakeCoreClientApiProxy>();
            var clientProxy = (FakeCoreClientApiProxy)(object)clientApi;
            clientProxy.DataBasePath = dataPath;
            clientProxy.Assets = assetManager;
            clientProxy.Logger = logger;
            clientProxy.ModLoader = modLoader;
            clientProxy.Event = eventApi;
            clientProxy.ObjectCache = objectCache;
            clientProxy.IsShuttingDownCallback = isShuttingDown;

            IClientWorldAccessor world = CreateWorld(clientApi, logger);
            clientProxy.World = world;

            var modSystem = new OptiTimeMod();
            bootedMods.Add(modSystem);
            modSystem.Start(clientApi);
            modSystem.AssetsLoaded(clientApi);
            modSystem.StartClientSide(clientApi);

            return clientApi;
        }

        private static object CreateHandbookSystem(ICoreClientAPI clientApi)
        {
            Type handbookType = AccessTools.TypeByName("Vintagestory.GameContent.ModSystemSurvivalHandbook");
            Assert.NotNull(handbookType);
            object instance = Activator.CreateInstance(handbookType);
            AccessTools.Field(handbookType, "capi").SetValue(instance, clientApi);
            return instance;
        }

        private static ClientCoreAPI CreateUninitializedClientApi()
        {
            return (ClientCoreAPI)RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI));
        }

        private static IClientWorldAccessor CreateWorld(ICoreAPI api, ILogger logger)
        {
            var world = DispatchProxy.Create<IClientWorldAccessor, FakeWorldProxy>();
            var proxy = (FakeWorldProxy)(object)world;
            proxy.Api = api;
            proxy.Logger = logger;
            return world;
        }

        private string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "optitime-33-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            tempDirectories.Add(dir);
            return dir;
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
                {
                    return candidate;
                }
            }

            return null;
        }

        private class MockCollectible : Item
        {
            private readonly string heldName;
            private readonly string heldDescription;

            public MockCollectible(string code, string name, string description)
            {
                Code = new AssetLocation(code);
                heldName = name;
                heldDescription = description;
            }

            public override string GetHeldItemName(ItemStack itemStack) => heldName;

            public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
            {
                dsc.Append(heldDescription);
            }
        }

        private sealed class ThrowingDescriptionCollectible : MockCollectible
        {
            public ThrowingDescriptionCollectible(string code, string name)
                : base(code, name, string.Empty)
            {
            }

            public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
            {
                throw new InvalidOperationException("Simulated broken mod GetHeldItemInfo");
            }
        }

        private sealed class ThrowingSearchTextCollectible : MockCollectible, ISearchTextProvider
        {
            public ThrowingSearchTextCollectible(string code, string name)
                : base(code, name, "Unused description")
            {
            }

            public string GetSearchText(IWorldAccessor world, ItemSlot inSlot)
            {
                throw new InvalidOperationException("Simulated broken mod GetSearchText");
            }
        }

        private sealed class ThrowingNameCollectible : Item
        {
            public ThrowingNameCollectible(string code)
            {
                Code = new AssetLocation(code);
            }

            public override string GetHeldItemName(ItemStack itemStack)
            {
                throw new InvalidOperationException("Simulated broken mod GetHeldItemName");
            }
        }

        private sealed class DisposeTriggeringCollectible : MockCollectible
        {
            private readonly Action onEvaluate;

            public DisposeTriggeringCollectible(string code, string name, Action onEvaluate)
                : base(code, name, string.Empty)
            {
                this.onEvaluate = onEvaluate;
            }

            public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
            {
                onEvaluate();
                throw new NullReferenceException("Simulated shutdown race after Dispose");
            }
        }

        private sealed class CallbackCollectible : MockCollectible
        {
            private readonly Action onEvaluate;

            public CallbackCollectible(string code, string name, Action onEvaluate)
                : base(code, name, "Valid trailing item")
            {
                this.onEvaluate = onEvaluate;
            }

            public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
            {
                onEvaluate();
                base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
            }
        }

        private static RecordingLogger CreateLogger()
        {
            var logger = DispatchProxy.Create<ILogger, RecordingLoggerProxy>();
            return new RecordingLogger(logger, (RecordingLoggerProxy)(object)logger);
        }

        private sealed class RecordingLogger
        {
            public RecordingLogger(ILogger logger, RecordingLoggerProxy proxy)
            {
                Instance = logger;
                Warnings = proxy.Warnings;
            }

            public ILogger Instance { get; }
            public List<string> Warnings { get; }
        }

        public class RecordingLoggerProxy : DispatchProxy
        {
            public List<string> Warnings { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(ILogger.Warning) && args?.Length >= 1 && args[0] is string msg)
                {
                    object[] fmtArgs = args.Length > 1 ? args[1] as object[] : null;
                    Warnings.Add(fmtArgs != null && fmtArgs.Length > 0 ? string.Format(msg, fmtArgs) : msg);
                }
                else if (targetMethod.Name == nameof(ILogger.Log) && args?.Length >= 2 && args[0] is EnumLogType logType && logType == EnumLogType.Warning && args[1] is string logMsg)
                {
                    object[] fmtArgs = args.Length > 2 ? args[2] as object[] : null;
                    Warnings.Add(fmtArgs != null && fmtArgs.Length > 0 ? string.Format(logMsg, fmtArgs) : logMsg);
                }

                return targetMethod.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        private sealed class FakeAssetManager : IAssetManager
        {
            public Dictionary<AssetLocation, IAsset> AllAssets { get; } = new();
            public List<IAssetOrigin> Origins { get; } = new();

            public bool Exists(AssetLocation location) => AllAssets.ContainsKey(location);
            public void Add(AssetLocation path, IAsset asset) => AllAssets[path] = asset;
            public IAsset Get(AssetLocation location) => AllAssets[location];
            public IAsset TryGet(AssetLocation location, bool loadAsset = true) => null;
            public List<IAsset> GetMany(string pathBegins, string domain = null, bool loadAsset = true) => new();
            public List<IAsset> GetManyInCategory(string categoryCode, string pathBegins, string domain = null, bool loadAsset = true) => new();
            public Dictionary<AssetLocation, T> GetMany<T>(ILogger logger, string pathBegins, string domain = null) => new();
            public List<AssetLocation> GetLocations(string pathBegins, string domain = null) => new();
            public T Get<T>(AssetLocation location) => default;
            public int Reload(AssetLocation baseLocation) => 0;
            public int Reload(AssetCategory category) => 0;
            public void AddPathOrigin(string domain, string fullPath) { }
            public void AddModOrigin(string domain, string fullPath) { }
            public void AddModOrigin(string domain, string fullPath, string pathForReservedCharsCheck) { }
            public void RemoveModOrigin(string domain, string fullpath) { }
        }

        public class FakeWorldProxy : DispatchProxy
        {
            public ICoreAPI Api { get; set; }
            public ILogger Logger { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name switch
                {
                    "get_Api" => Api,
                    "get_Logger" => Logger,
                    "get_Side" => Api?.Side ?? EnumAppSide.Client,
                    _ => targetMethod.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null
                };
            }
        }

        public class FakeModLoaderProxy : DispatchProxy
        {
            public object RecipeRegistry { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IModLoader.GetModSystem) &&
                    targetMethod.IsGenericMethod &&
                    targetMethod.GetGenericArguments()[0].FullName == "Vintagestory.GameContent.RecipeRegistrySystem")
                {
                    return RecipeRegistry;
                }

                if (targetMethod.ReturnType == typeof(bool))
                {
                    return false;
                }

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

        public class FakeServerApiProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_Side")
                {
                    return EnumAppSide.Server;
                }

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
            public IClientWorldAccessor World { get; set; }
            public Dictionary<string, object> ObjectCache { get; set; }
            public Func<bool> IsShuttingDownCallback { get; set; }

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
                    case "get_World":
                        return World;
                    case "get_ObjectCache":
                        return ObjectCache;
                    case "get_IsShuttingDown":
                        return IsShuttingDownCallback?.Invoke() ?? false;
                    case nameof(ICoreClientAPI.RegisterCommand):
                        return true;
                }

                return targetMethod.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }
    }
}
