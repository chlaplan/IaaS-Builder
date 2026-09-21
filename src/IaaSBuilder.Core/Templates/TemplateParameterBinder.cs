using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;

namespace IaaSBuilder.Core.Templates;

/// <summary>
/// Projects a <see cref="DeploymentPlan"/> onto the ARM template parameter contract.
/// </summary>
/// <remarks>
/// This is the one place that knows how the model maps to template parameter names.
/// In the legacy script that mapping was duplicated across 25 deployment calls, so a
/// rename meant 25 edits and any one of them could be missed.
/// </remarks>
public static class TemplateParameterBinder
{
    /// <summary>Parameters shared by every VM and networking template.</summary>
    public static Dictionary<string, object?> BuildCommon(
        DeploymentPlan plan,
        DeploymentSecrets secrets,
        string artifactsLocation,
        string artifactsSasToken = "",
        string packagePath = Deployment.PublicPackageSource.StagedPackagePath)
    {
        var dc = plan.EnabledServers.FirstOrDefault(s => s.Role == ServerRole.DomainController);
        var primarySite = plan.EnabledServers.FirstOrDefault(s => s.Role == ServerRole.SccmPrimarySite);
        var distributionPoint = plan.EnabledServers.FirstOrDefault(s => s.Role == ServerRole.SccmDistributionPoint);
        var sql = plan.EnabledServers.FirstOrDefault(s => s.Role == ServerRole.Sql);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["prefix"] = plan.Network.Prefix,
            ["DomainName"] = plan.Identity.DomainName,
            ["adminUsername"] = plan.Identity.AdminUsername,
            ["adminPassword"] = secrets.AdminPassword,
            ["AdfsServiceAccountName"] = plan.Identity.AdfsServiceAccountName,
            ["_artifactsLocation"] = artifactsLocation,
            ["_artifactsLocationSasToken"] = artifactsSasToken,
            ["dscPackagePath"] = packagePath,
            ["location"] = plan.Azure.Location,
            ["addressprefix"] = plan.Network.AddressPrefix,
            ["addresssubnet"] = plan.Network.SubnetPrefix,
            ["VirtualNetworkName"] = plan.Network.EffectiveVirtualNetworkName,
            ["NSG"] = plan.Network.EffectiveNetworkSecurityGroupName,
            ["subnetname"] = plan.Network.SubnetName,
            ["assignPublicIp"] = plan.Network.AssignPublicIps,
            ["bastionsubnet"] = plan.Bastion.SubnetPrefix,
            ["DCName"] = dc?.Name ?? "",
            ["DCip"] = dc?.PrivateIpAddress ?? "",
            ["PSName"] = primarySite?.Name ?? "",
            ["DPMPName"] = distributionPoint?.Name ?? "",
            ["SQLName"] = sql?.Name ?? "",
            // The templates declare these as strings, not bools, so keep ARM's casing.
            ["STIG"] = plan.Hardening.ApplyStig ? "true" : "false",
            ["MSFTBaseline"] = plan.Hardening.ApplyMicrosoftBaseline ? "true" : "false",
            // Only the DC configuration reads this. A dedicated CA server gets its CA from
            // CAConfiguration.ps1 instead, so this stays false in that case or the forest would
            // be given two enterprise root CAs.
            ["installCertificateAuthority"] =
                plan.Identity.CertificateAuthority == CertificateAuthorityPlacement.DomainController
                    ? "true"
                    : "false",
            // Read by every configuration, which writes it to C:\InstallMedia.txt for the install
            // scripts to find. Passed to all roles rather than only the ones that download today,
            // so that a role which gains a download later needs no template change.
            ["installMediaLocation"] = plan.Artifacts.InstallMediaLocation.Trim(),
            ["installMediaFallback"] = plan.Artifacts.InstallMediaFallbackToInternet ? "true" : "false"
        };
    }

    /// <summary>Common parameters plus the ones specific to a single server.</summary>
    public static Dictionary<string, object?> BuildForServer(
        DeploymentPlan plan,
        ServerSpec server,
        IReadOnlyDictionary<string, object?> common)
    {
        var definition = RoleCatalog.Get(server.Role);
        var parameters = new Dictionary<string, object?>(common, StringComparer.OrdinalIgnoreCase)
        {
            ["servername"] = server.Name,
            ["ip"] = server.PrivateIpAddress,
            ["vmsize"] = server.VmSize,
            ["vmdisk"] = server.DiskType,
            ["publisher"] = server.Image.Publisher,
            ["offer"] = server.Image.Offer,
            ["sku"] = server.Image.Sku,
            ["role"] = definition.DscToken,
            ["DHostID"] = plan.DedicatedHost is { Enabled: true } host ? host.HostId ?? "" : ""
        };

        if (!string.IsNullOrWhiteSpace(server.SharePointVersion))
        {
            parameters["sharePointVersion"] = server.SharePointVersion;
        }

        // Caller-supplied escape hatch always wins.
        foreach (var (key, value) in server.ExtraParameters)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    /// <summary>
    /// Parameters for the networking and Bastion templates.
    /// </summary>
    /// <remarks>
    /// Networking.json and Bastion.json were copied from the VM template, so they declare most
    /// of its contract as required with no default even though they only build a virtual
    /// network, an NSG and a Bastion host. A plan with no enabled servers is legitimate - the
    /// validator permits it and warns "only networking will be deployed" - so a placeholder
    /// server shape stands in, otherwise ARM would reject the deployment for missing parameters
    /// it never reads.
    /// </remarks>
    public static Dictionary<string, object?> BuildForNetwork(
        DeploymentPlan plan,
        DeploymentSecrets secrets,
        string artifactsLocation,
        string artifactsSasToken = "",
        string packagePath = Deployment.PublicPackageSource.StagedPackagePath)
    {
        var common = BuildCommon(plan, secrets, artifactsLocation, artifactsSasToken, packagePath);
        var server = plan.EnabledServers.FirstOrDefault() ?? PlaceholderServer(plan);

        return BuildForServer(plan, server, common);
    }

    private static ServerSpec PlaceholderServer(DeploymentPlan plan) => new()
    {
        Name = "placeholder",
        Role = ServerRole.MemberServer,
        PrivateIpAddress = Validation.Cidr.TryParse(plan.Network.SubnetPrefix, out var subnet)
            ? subnet.FirstUsableAddress() ?? "10.0.0.4"
            : "10.0.0.4"
    };

    public static Dictionary<string, object?> BuildForAvd(
        DeploymentPlan plan,
        AvdSpec avd,
        DeploymentSecrets secrets)
    {
        var tokenExpiration = DateTimeOffset.UtcNow
            .AddHours(avd.TokenLifetimeHours)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["hostpoolName"] = avd.HostPoolName,
            ["domain"] = plan.Identity.DomainName,
            ["vmNamePrefix"] = plan.Network.Prefix,
            ["hostpoolType"] = avd.HostPoolType,
            ["vmSize"] = avd.VmSize,
            ["vmLocation"] = plan.Azure.Location,
            ["administratorAccountUsername"] = plan.Identity.UserPrincipalName,
            ["administratorAccountPassword"] = secrets.AdminPassword,
            ["vmResourceGroup"] = plan.Azure.ResourceGroup,
            ["vmNumberOfInstances"] = avd.SessionHostCount,
            ["vmGalleryImagePublisher"] = avd.Image.Publisher,
            ["vmGalleryImageOffer"] = avd.Image.Offer,
            ["vmGalleryImageSKU"] = avd.Image.Sku,
            ["vmDiskType"] = avd.DiskType,
            ["vmImageType"] = "Gallery",
            ["loadBalancerType"] = avd.LoadBalancerType,
            ["existingSubnetName"] = plan.Network.SubnetName,
            ["existingVnetName"] = plan.Network.EffectiveVirtualNetworkName,
            ["virtualNetworkResourceGroupName"] = plan.Azure.ResourceGroup,
            ["location"] = avd.MetadataLocation,
            ["addToWorkspace"] = false,
            // Computed at deployment time, not at app start, so it cannot go stale
            // during a long session the way the legacy script-scope value did.
            ["tokenExpirationTime"] = tokenExpiration,
            ["createAvailabilitySet"] = true
        };
    }
}
