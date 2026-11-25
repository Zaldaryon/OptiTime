using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;

namespace OptiTime
{
    /// <summary>
    /// Wraps an asset origin and hides some of its game assets. Reloading an asset category then rebuilds
    /// the asset dictionary and the category index through the public asset manager, with the hidden files
    /// resolved to whatever origin sits underneath. Only public API is involved, unlike patching the
    /// manager's private dictionaries.
    /// </summary>
    internal sealed class FilteredAssetOrigin : IAssetOrigin
    {
        private readonly IAssetOrigin inner;
        private readonly HashSet<string> hiddenGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public FilteredAssetOrigin(IAssetOrigin inner)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public string OriginPath => inner.OriginPath;

        public IAssetOrigin Inner => inner;

        public bool Hide(string gamePath)
        {
            return hiddenGamePaths.Add(gamePath);
        }

        public void LoadAsset(IAsset asset) => inner.LoadAsset(asset);

        public bool TryLoadAsset(IAsset asset) => inner.TryLoadAsset(asset);

        public bool IsAllowedToAffectGameplay() => inner.IsAllowedToAffectGameplay();

        public List<IAsset> GetAssets(AssetCategory category, bool loadAsset = true)
        {
            return Filter(inner.GetAssets(category, loadAsset));
        }

        public List<IAsset> GetAssets(AssetLocation baseLocation, bool loadAsset = true)
        {
            return Filter(inner.GetAssets(baseLocation, loadAsset));
        }

        private List<IAsset> Filter(List<IAsset> assets)
        {
            if (hiddenGamePaths.Count == 0 || assets == null)
                return assets;

            return assets.Where(asset => asset?.Location == null
                || !string.Equals(asset.Location.Domain, "game", StringComparison.OrdinalIgnoreCase)
                || !hiddenGamePaths.Contains(asset.Location.Path)).ToList();
        }
    }
}
