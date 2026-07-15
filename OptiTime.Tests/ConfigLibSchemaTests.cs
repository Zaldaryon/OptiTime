using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Vintagestory.API.Datastructures;
using Xunit;

namespace OptiTime.Tests;

public class ConfigLibSchemaTests
{
    [Fact]
    public void PublishedSettingsAreClientSideAndMapToConfigProperties()
    {
        string schemaPath = Path.Combine(FindRepositoryRoot(), "assets", "optitime", "config", "configlib-patches.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(schemaPath));

        JsonElement[] settings = document.RootElement
            .GetProperty("settings")
            .EnumerateArray()
            .Where(setting => setting.TryGetProperty("code", out _))
            .ToArray();

        Assert.Equal(26, settings.Length);

        HashSet<string> configProperties = typeof(OptiTimeConfig)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (JsonElement setting in settings)
        {
            string code = setting.GetProperty("code").GetString();
            Assert.True(setting.GetProperty("clientSide").GetBoolean(), $"{code} must be client-side");
            Assert.Contains(code, configProperties);

            if (code == nameof(OptiTimeConfig.BackgroundMaxFps))
            {
                Assert.Equal(
                    FrameRateOptimization.MinimumBackgroundFps,
                    setting.GetProperty("range").GetProperty("min").GetInt32());
            }

            TreeAttribute tree = CreateValueAttribute(setting);
            Assert.True(
                ConfigLibIntegration.TryApplySetting(new OptiTimeConfig(), code, tree, out _),
                $"{code} must be handled by ConfigLibIntegration");
        }
    }

    private static TreeAttribute CreateValueAttribute(JsonElement setting)
    {
        TreeAttribute tree = new();
        JsonElement defaultValue = setting.GetProperty("default");

        switch (setting.GetProperty("type").GetString())
        {
            case "boolean":
                tree.SetBool("value", defaultValue.GetBoolean());
                break;
            case "integer":
                tree.SetInt("value", defaultValue.GetInt32());
                break;
            case "float":
                tree.SetFloat("value", defaultValue.GetSingle());
                break;
            default:
                throw new InvalidDataException($"Unsupported ConfigLib setting type for {setting.GetProperty("code").GetString()}.");
        }

        return tree;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OptiTime.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the OptiTime repository root.");
    }
}
