using YamlDotNet.RepresentationModel;

namespace Bydlo.Tests;

public sealed class AzureLoginWorkflowTests
{
    private static (YamlMappingNode Root, string Text) ReadWorkflow()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, ".github", "workflows", "ci.yml"));
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        return ((YamlMappingNode)Assert.Single(yaml.Documents).RootNode, text);
    }

    [Fact]
    public void Azure_login_pins_v3_release_commit_and_keeps_oidc_inputs()
    {
        var (root, text) = ReadWorkflow();
        var jobs = (YamlMappingNode)root["jobs"];
        var job = (YamlMappingNode)jobs["native-packages"];
        var steps = (YamlSequenceNode)job["steps"];
        var login = Assert.Single(steps.Children.Cast<YamlMappingNode>(), step =>
            step.Children.TryGetValue("uses", out var action) && action.ToString().StartsWith("Azure/login@", StringComparison.Ordinal));
        Assert.Equal("Azure/login@a641126d1b8aa4d1fa005f4f92df94a3a4c4c906", login["uses"].ToString());
        var inputs = (YamlMappingNode)login["with"];
        Assert.Equal(3, inputs.Children.Count);
        Assert.Equal("${{ secrets.AZURE_CLIENT_ID }}", inputs["client-id"].ToString());
        Assert.Equal("${{ secrets.AZURE_TENANT_ID }}", inputs["tenant-id"].ToString());
        Assert.Equal("${{ secrets.AZURE_SUBSCRIPTION_ID }}", inputs["subscription-id"].ToString());
        Assert.DoesNotContain("AZURE_LOGIN_POST_CLEANUP", text, StringComparison.Ordinal);
        Assert.DoesNotContain("mask-client-id:", text, StringComparison.Ordinal);
        Assert.Equal("startsWith(matrix.rid, 'win-') && needs.signing-policy.outputs.unsigned != 'true' && env.AZURE_CLIENT_ID != ''", login["if"].ToString());
    }

    [Fact]
    public void Signing_job_keeps_release_only_gate_and_minimal_oidc_permissions()
    {
        var (root, _) = ReadWorkflow();
        var job = (YamlMappingNode)((YamlMappingNode)root["jobs"])["native-packages"];
        var condition = job["if"].ToString();
        Assert.Contains("github.server_url == 'https://github.com'", condition, StringComparison.Ordinal);
        Assert.Contains("(startsWith(github.ref, 'refs/tags/v') || github.event_name == 'workflow_dispatch')", condition, StringComparison.Ordinal);
        Assert.Contains("needs.desktop.result == 'success'", condition, StringComparison.Ordinal);
        Assert.Contains("needs.sign-windows.result == 'success'", condition, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request", condition, StringComparison.Ordinal);
        var permissions = (YamlMappingNode)job["permissions"];
        Assert.Equal(2, permissions.Children.Count);
        Assert.Equal("read", permissions["contents"].ToString());
        Assert.Equal("write", permissions["id-token"].ToString());
        Assert.False(((YamlMappingNode)root["permissions"]).Children.ContainsKey("id-token"));
        Assert.Equal("${{ matrix.os }}", job["runs-on"].ToString());
        var matrix = (YamlMappingNode)((YamlMappingNode)job["strategy"])["matrix"];
        var windows = ((YamlSequenceNode)matrix["include"]).Children.Cast<YamlMappingNode>()
            .Where(entry => entry["rid"].ToString().StartsWith("win-", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(windows);
        Assert.All(windows, entry => Assert.Equal("windows-latest", entry["os"].ToString()));
    }
}
