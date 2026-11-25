using System;
using System.Reflection;
using Vintagestory.API.Config;

namespace OptiTime
{
    /// <summary>
    /// Detects the Optimum client host. Optimum rewrites the game assemblies ahead of time, so OptiTime
    /// must not patch methods or override assets that Optimum already changed. Detection is decided once,
    /// in <see cref="OptiTimeMod.Start"/>, because shader assets are loaded before the client side starts.
    /// </summary>
    public static class OptimumCompatibility
    {
        private const string OptimumConfigTypeName = "Vintagestory.API.Config.OptimumConfig";
        private const string OptimumContractsAssemblyName = "Optimum.Api.Contracts";

        public static bool IsHost { get; private set; }

        /// <summary>
        /// Looks for the Optimum contracts assembly. The patched game API forwards its types to it, so
        /// resolving a forwarded type loads the assembly even when nothing used it yet. Loading the
        /// assembly by name covers an API that is not forwarding yet. On a vanilla client both lookups
        /// fail and the result is false.
        /// </summary>
        public static bool Detect()
        {
            IsHost = false;

            Assembly contracts = null;
            try
            {
                contracts = typeof(GamePaths).Assembly.GetType(OptimumConfigTypeName, false)?.Assembly;
            }
            catch
            {
                // A broken forwarder means this is not a usable Optimum host.
            }

            if (contracts == null)
            {
                try
                {
                    contracts = Assembly.Load(new AssemblyName(OptimumContractsAssemblyName));
                }
                catch
                {
                    // Not installed: vanilla client.
                }
            }

            if (contracts == null)
                return false;

            IsHost = true;
            return true;
        }
    }
}
