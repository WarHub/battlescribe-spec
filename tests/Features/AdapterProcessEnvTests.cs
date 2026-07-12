using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests.Features;

[Trait("Category", "Unit")]
public sealed class AdapterProcessEnvTests
{
    [Fact]
    public void BuildStartInfo_WithEnvironment_AppliesEachEntryToProcessStartInfo()
    {
        var psi = AdapterProcess.BuildStartInfo("dotnet", "some.dll", new Dictionary<string, string>
        {
            ["BSSPEC_TEST_ENV"] = "hello",
            ["BSSPEC_WORKER_INDEX"] = "3",
        });

        Assert.Equal("hello", psi.Environment["BSSPEC_TEST_ENV"]);
        Assert.Equal("3", psi.Environment["BSSPEC_WORKER_INDEX"]);
    }

    [Fact]
    public void BuildStartInfo_WithNullEnvironment_InheritsParentEnvironment()
    {
        const string sentinelKey = "BSSPEC_INHERITED_SENTINEL";
        const string sentinelValue = "inherited";

        Environment.SetEnvironmentVariable(sentinelKey, sentinelValue);
        try
        {
            var psi = AdapterProcess.BuildStartInfo("dotnet", "some.dll", environment: null);

            Assert.Equal(sentinelValue, psi.Environment[sentinelKey]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelKey, null);
        }
    }

    [Fact]
    public void BuildStartInfo_LayersExtraEnvironmentOnTopOfInherited()
    {
        const string sentinelKey = "BSSPEC_INHERITED_SENTINEL";
        const string sentinelValue = "inherited";

        Environment.SetEnvironmentVariable(sentinelKey, sentinelValue);
        try
        {
            var psi = AdapterProcess.BuildStartInfo("dotnet", "some.dll", new Dictionary<string, string>
            {
                ["BSSPEC_EXTRA_VAR"] = "extra",
            });

            // Inherited environment variable is still present
            Assert.Equal(sentinelValue, psi.Environment[sentinelKey]);

            // Extra environment variables are also present
            Assert.Equal("extra", psi.Environment["BSSPEC_EXTRA_VAR"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelKey, null);
        }
    }

    [Fact]
    public void BuildStartInfo_SetsExecutableAndArguments()
    {
        var psi = AdapterProcess.BuildStartInfo("dotnet", "some.dll", environment: null);

        Assert.Equal("dotnet", psi.FileName);
        Assert.Equal("some.dll", psi.Arguments);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardInput);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
    }
}
