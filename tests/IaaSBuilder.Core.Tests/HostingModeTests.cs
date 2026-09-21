using IaaSBuilder.Web.Services;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// <see cref="HostingMode"/> decides whether the interactive browser sign-in is offered, and that
/// flow opens a browser on the machine running the server. Deciding "local" for a site that is
/// actually published is not a cosmetic bug: it strands a logon prompt on the web server that
/// nobody can see, while the visitor waits for a redirect that never arrives. So the bias must be
/// towards "remote", and these tests pin that.
/// </summary>
public class HostingModeTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5099")]
    [InlineData("http://localhost:5099")]
    [InlineData("https://localhost:7099")]
    [InlineData("http://[::1]:5099")]
    public void Loopback_bindings_are_local(string url) =>
        Assert.True(HostingMode.FromUrls([url]).IsLocalOperator);

    [Theory]
    [InlineData("http://0.0.0.0:80")]
    [InlineData("http://10.1.2.3:5099")]
    [InlineData("https://iaas.example.com")]
    public void Routable_bindings_are_not_local(string url) =>
        Assert.False(HostingMode.FromUrls([url]).IsLocalOperator);

    [Theory]
    [InlineData("http://*:5099")]
    [InlineData("http://+:80")]
    public void Wildcard_bindings_are_not_local(string url)
    {
        // These are not valid absolute Uris, so they cannot be inspected - and they are exactly
        // how a real site is published. Anything unparseable has to count as reachable.
        Assert.False(HostingMode.FromUrls([url]).IsLocalOperator);
    }

    [Fact]
    public void One_routable_binding_among_loopback_ones_is_enough_to_be_remote()
    {
        var mode = HostingMode.FromUrls(["http://127.0.0.1:5099;https://iaas.example.com"]);

        Assert.False(mode.IsLocalOperator);
        Assert.Contains("iaas.example.com", mode.Reason);
    }

    [Fact]
    public void Semicolon_separated_loopback_bindings_stay_local() =>
        Assert.True(HostingMode.FromUrls(["http://127.0.0.1:5099;https://localhost:7099"]).IsLocalOperator);

    [Fact]
    public void No_binding_means_the_host_defaults_which_are_loopback() =>
        Assert.True(HostingMode.FromUrls([]).IsLocalOperator);

    [Fact]
    public void Empty_and_whitespace_entries_are_ignored() =>
        Assert.True(HostingMode.FromUrls(["  ;http://127.0.0.1:5099;  "]).IsLocalOperator);
}

/// <summary>
/// Half-configured redirect sign-in must not count as configured: it would add authentication
/// middleware that fails on every request, which on a hosted site means the tool is simply down.
/// </summary>
public class EntraOptionsTests
{
    private static EntraOptions Full() => new()
    {
        ClientId = "11111111-1111-1111-1111-111111111111",
        TenantId = "22222222-2222-2222-2222-222222222222",
        ClientSecret = "secret"
    };

    [Fact]
    public void Fully_populated_is_configured() => Assert.True(Full().IsConfigured);

    [Fact]
    public void Default_is_not_configured() => Assert.False(new EntraOptions().IsConfigured);

    [Fact]
    public void Missing_client_id_is_not_configured()
    {
        var options = Full();
        options.ClientId = "";
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void Missing_tenant_is_not_configured()
    {
        var options = Full();
        options.TenantId = "   ";
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void Missing_secret_is_not_configured()
    {
        var options = Full();
        options.ClientSecret = "";
        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData("UsGovernment", Models.AzureCloud.UsGovernment)]
    [InlineData("usgovernment", Models.AzureCloud.UsGovernment)]
    [InlineData("Public", Models.AzureCloud.Public)]
    [InlineData("", Models.AzureCloud.Public)]
    [InlineData("nonsense", Models.AzureCloud.Public)]
    public void Cloud_parses_case_insensitively_and_falls_back(string value, Models.AzureCloud expected) =>
        Assert.Equal(expected, EntraSignIn.ParseCloud(value));
}
