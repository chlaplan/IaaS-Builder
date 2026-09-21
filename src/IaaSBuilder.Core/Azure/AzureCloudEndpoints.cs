using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Azure;

/// <summary>
/// Builds credentials and cloud endpoints for the selected Azure cloud.
/// </summary>
/// <remarks>
/// Replaces the two hard-coded <c>Connect-AzAccount</c> menu branches and, more usefully,
/// adds the non-interactive paths (managed identity, environment variables, Azure CLI)
/// the legacy script had no way to use - which is what made it impossible to run in CI.
/// </remarks>
public static class AzureCloudEndpoints
{
    public static Uri GetAuthorityHost(AzureCloud cloud) => cloud switch
    {
        AzureCloud.Public => AzureAuthorityHosts.AzurePublicCloud,
        AzureCloud.UsGovernment => AzureAuthorityHosts.AzureGovernment,
        AzureCloud.China => AzureAuthorityHosts.AzureChina,
        AzureCloud.Custom => CustomCloud.Require().AuthorityHost!,
        _ => AzureAuthorityHosts.AzurePublicCloud
    };

    public static Uri GetResourceManagerEndpoint(AzureCloud cloud) => cloud switch
    {
        AzureCloud.Public => new Uri("https://management.azure.com/"),
        AzureCloud.UsGovernment => new Uri("https://management.usgovcloudapi.net/"),
        AzureCloud.China => new Uri("https://management.chinacloudapi.cn/"),
        AzureCloud.Custom => CustomCloud.Require().ResourceManagerEndpoint!,
        _ => new Uri("https://management.azure.com/")
    };

    /// <summary>Blob storage DNS suffix, used when building artifact URIs and private DNS zones.</summary>
    public static string GetBlobSuffix(AzureCloud cloud) => cloud switch
    {
        AzureCloud.Public => "blob.core.windows.net",
        AzureCloud.UsGovernment => "blob.core.usgovcloudapi.net",
        AzureCloud.China => "blob.core.chinacloudapi.cn",
        AzureCloud.Custom => CustomCloud.Require().BlobSuffix!,
        _ => "blob.core.windows.net"
    };

    public static ArmEnvironment GetArmEnvironment(AzureCloud cloud) => cloud switch
    {
        AzureCloud.Public => ArmEnvironment.AzurePublicCloud,
        AzureCloud.UsGovernment => ArmEnvironment.AzureGovernment,
        AzureCloud.China => ArmEnvironment.AzureChina,
        AzureCloud.Custom => CustomEnvironment(),
        _ => ArmEnvironment.AzurePublicCloud
    };

    private static ArmEnvironment CustomEnvironment()
    {
        var definition = CustomCloud.Require();
        return new ArmEnvironment(definition.ResourceManagerEndpoint!, definition.ResourceManagerAudience!);
    }

    /// <summary>Name to show for the cloud, including the operator-supplied label for custom clouds.</summary>
    public static string GetDisplayName(AzureCloud cloud) => cloud switch
    {
        AzureCloud.Public => "Azure commercial",
        AzureCloud.UsGovernment => "Azure US Government",
        AzureCloud.China => "Azure China",
        AzureCloud.Custom => CustomCloud.Definition?.Name ?? "Custom cloud (cloud.json)",
        _ => cloud.ToString()
    };

    /// <summary>
    /// Delegated scope for Azure Resource Manager in this cloud, for an app registration that
    /// calls ARM on behalf of the signed-in user.
    /// </summary>
    public static string GetResourceManagerUserScope(AzureCloud cloud) =>
        GetResourceManagerEndpoint(cloud).ToString().TrimEnd('/') + "/user_impersonation";

    /// <summary>The <c>.default</c> scope, used when acquiring a token for a credential directly.</summary>
    public static string GetResourceManagerDefaultScope(AzureCloud cloud) =>
        GetResourceManagerEndpoint(cloud).ToString().TrimEnd('/') + "/.default";

    public static TokenCredential CreateCredential(AzureCloud cloud, string? tenantId = null) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            AuthorityHost = GetAuthorityHost(cloud),
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId
        });

    /// <summary>
    /// Authorization code + PKCE in the system browser: the user is redirected to the real Entra
    /// sign-in page, so Conditional Access, MFA and device compliance all apply and there is no
    /// code to read out or be talked into entering somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This opens a browser <b>on the machine running this code</b> and listens on a loopback
    /// redirect. That is exactly right for the offline single-operator executable, where the
    /// server and the user are the same machine, and exactly wrong for a hosted website, where it
    /// would try to open a browser on the web server and then hang until it timed out. Callers
    /// must gate it - see <c>HostingMode</c> in the web app.
    /// </para>
    /// <para>
    /// No client id is supplied, so MSAL uses the well-known Azure development application, the
    /// same public client the Azure CLI and Azure PowerShell rely on. That keeps the offline
    /// distribution free of any app registration the operator would have to create inside an
    /// enclave.
    /// </para>
    /// </remarks>
    public static TokenCredential CreateInteractiveBrowserCredential(
        AzureCloud cloud,
        string? tenantId = null,
        string? clientId = null) =>
        new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            AuthorityHost = GetAuthorityHost(cloud),
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
            // MSAL otherwise leaves the browser on a bare "authentication complete" page, which
            // reads as a dead end. Say which application finished so the user knows to go back.
            BrowserCustomization = new BrowserCustomizationOptions
            {
                SuccessMessage = "Signed in to IaaS Builder. You can close this tab and return to the app."
            }
        });

    /// <summary>
    /// Device code flow, for machines with no browser - the usual situation on a
    /// hardened jump box inside an enclave.
    /// </summary>
    /// <remarks>
    /// Least secure of the interactive options and deliberately not the default. The code is a
    /// bearer of nothing by itself, but it is trivially phishable: an attacker generates a code
    /// against their own session and persuades someone to enter it. Microsoft's own guidance is
    /// to block this flow with Conditional Access wherever it is not needed. Keep it for the case
    /// it was designed for - no browser on the machine - and prefer
    /// <see cref="CreateInteractiveBrowserCredential"/> everywhere else.
    /// </remarks>
    public static TokenCredential CreateDeviceCodeCredential(
        AzureCloud cloud,
        Func<DeviceCodeInfo, CancellationToken, Task> onDeviceCode,
        string? tenantId = null) =>
        new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            AuthorityHost = GetAuthorityHost(cloud),
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            DeviceCodeCallback = onDeviceCode
        });
}
