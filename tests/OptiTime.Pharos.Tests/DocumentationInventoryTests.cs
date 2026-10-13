namespace OptiTime.Pharos.Tests;

public class DocumentationInventoryTests
{
    [Fact]
    public void ReadmeListsEveryCommandOption()
    {
        string readme = File.ReadAllText(Path.Combine(ModSourceAnalysis.RepositoryRoot, "README.md"));
        int start = readme.IndexOf("**Available", StringComparison.Ordinal);
        Assert.True(start >= 0, "README option inventory is missing.");
        start = readme.IndexOf('\n', start) + 1;
        int end = readme.IndexOf('\n', start);
        Assert.True(end > start, "README option inventory line is missing.");
        string[] documented = System.Text.RegularExpressions.Regex.Matches(
                readme[start..end], "`([a-z]+)`")
            .Select(match => match.Groups[1].Value).OrderBy(value => value).ToArray();
        Assert.Equal(OptiTimeConfig.OptionNames.OrderBy(value => value).ToArray(), documented);
    }
}
