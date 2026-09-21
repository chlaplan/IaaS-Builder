using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Roles;

namespace IaaSBuilder.Core;

/// <summary>
/// Produces a sensible starting plan.
/// </summary>
/// <remarks>
/// The legacy form encoded its defaults as literal strings scattered across
/// <c>form.xml</c> and the top of the script (<c>$DefaultVMSize = "Standard_F2s"</c>,
/// <c>$DefaultOSImage = "2019-Datacenter"</c>, <c>$DefaultWVDImage = "20h1-evd-o365pp"</c>),
/// which is why they were never updated as those SKUs aged out.
/// </remarks>
public static class PlanFactory
{
    public static DeploymentPlan CreateDefault(string prefix = "lab", string domainName = "contoso.local")
    {
        var plan = new DeploymentPlan
        {
            Name = prefix,
            Azure = new AzureTarget
            {
                Location = "eastus",
                ResourceGroup = $"{prefix}-rg",
                Tags = { ["builtBy"] = "IaaSBuilder" }
            },
            Identity = new IdentitySpec
            {
                DomainName = domainName,
                AdminUsername = "labadmin"
            },
            Network = new NetworkSpec
            {
                Prefix = prefix,
                AddressPrefix = "10.10.0.0/16",
                SubnetName = "workload",
                SubnetPrefix = "10.10.0.0/24"
            },
            Bastion = new BastionSpec
            {
                Enabled = true,
                SubnetPrefix = "10.10.1.0/26"
            },
            Artifacts = new ArtifactSpec
            {
                StorageAccountName = $"{Sanitize(prefix)}dsc{Random.Shared.Next(1000, 9999)}"
            }
        };

        var domainController = RoleCatalog.CreateDefault(ServerRole.DomainController, prefix);
        domainController.PrivateIpAddress = "10.10.0.10";
        plan.Servers.Add(domainController);

        // No workstation is seeded. It used to be added disabled, which still put a client card on
        // the Servers page of every new lab and invited the reading that a client is part of the
        // baseline build. Add one from the Tier 2 buttons when the lab actually calls for it.

        return plan;
    }

    /// <summary>
    /// Whether another server of this role may be added, and if not, why.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the page so the CLI and the web UI cannot disagree. The validator
    /// already rejects a second primary domain controller, but only once the plan is deployed or
    /// re-validated; refusing the click says so at the moment the operator asks for it, and names
    /// the role they actually want.
    /// </remarks>
    public static bool CanAddServer(DeploymentPlan plan, ServerRole role, out string? reason)
    {
        if (RoleCatalog.TryGet(role, out var definition) &&
            definition.SingleInstance &&
            plan.Servers.Any(s => s.Role == role))
        {
            reason = role == ServerRole.DomainController
                ? "A lab has one first domain controller, which creates the forest. "
                    + "Add an Additional Domain Controller to replicate it."
                : $"Only one {definition.DisplayName} is supported in a plan.";

            return false;
        }

        // A forest gets one enterprise root CA. Adding a CA server while the domain controller
        // is set to install one would build two, so the placement has to be chosen first - on
        // the Identity page, which is where the choice lives.
        if (role == ServerRole.CertificateAuthority &&
            plan.Identity.CertificateAuthority != CertificateAuthorityPlacement.DedicatedServer)
        {
            reason = "Set the certificate authority to run on a dedicated server, on the "
                + "Identity page, before adding one. Otherwise the domain controller installs "
                + "the enterprise root CA and this server would be a second one.";

            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Adds a server of the given role, picking the next free address in the subnet.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The role may only appear once and the plan already has one.
    /// </exception>
    public static ServerSpec AddServer(DeploymentPlan plan, ServerRole role)
    {
        if (!CanAddServer(plan, role, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var server = RoleCatalog.CreateDefault(role, plan.Network.Prefix);
        server.Name = MakeUniqueName(plan, server.Name);
        server.PrivateIpAddress = NextFreeAddress(plan);
        plan.Servers.Add(server);
        return server;
    }

    private static string MakeUniqueName(DeploymentPlan plan, string preferred)
    {
        if (plan.Servers.All(s => !string.Equals(s.Name, preferred, StringComparison.OrdinalIgnoreCase)))
        {
            return preferred;
        }

        for (var i = 2; i < 100; i++)
        {
            var trimmed = preferred.Length > 13 ? preferred[..13] : preferred;
            var candidate = $"{trimmed}{i:00}";
            if (plan.Servers.All(s => !string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return preferred;
    }

    private static string NextFreeAddress(DeploymentPlan plan)
    {
        if (!Validation.Cidr.TryParse(plan.Network.SubnetPrefix, out var subnet))
        {
            return "";
        }

        var used = plan.Servers
            .Select(s => s.PrivateIpAddress)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Azure reserves the first four addresses of every subnet.
        for (var offset = 4u; offset < subnet.AddressCount - 1; offset++)
        {
            var candidate = FormatAddress(subnet.Network + offset);
            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    private static string FormatAddress(uint value) =>
        $"{(byte)(value >> 24)}.{(byte)(value >> 16)}.{(byte)(value >> 8)}.{(byte)value}";

    private static string Sanitize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
