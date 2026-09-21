using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Validation;

/// <summary>
/// Re-derives the lab's subnets from its virtual network address space.
/// </summary>
/// <remarks>
/// Exists because of a real deployment failure. The address space was moved to a different range
/// and the workload subnet moved with it, but the Bastion prefix was left behind on the old one.
/// The virtual network deployed perfectly - it is the Bastion subnet *inside* it that ARM rejects,
/// with NetcfgSubnetRangeOutsideVnet, several minutes and one resource group later.
///
/// Validation already catches this and now blocks the deployment, but telling someone their
/// subnet is outside the address space still leaves them doing CIDR arithmetic to get out of it.
/// This does the arithmetic.
///
/// The layout deliberately reproduces the shipped defaults when applied to a /16 - workload on the
/// first /24, Bastion on the /26 immediately after it - so repairing a plan lands somewhere
/// familiar rather than somewhere merely valid.
/// </remarks>
public static class SubnetLayout
{
    /// <summary>Azure will not create an AzureBastionSubnet smaller than this.</summary>
    private const int BastionPrefixLength = 26;

    /// <summary>
    /// Whether any subnet currently sits outside the address space, or the address space itself is
    /// unparseable. Bastion is only considered when it is actually being deployed.
    /// </summary>
    public static bool NeedsRefit(DeploymentPlan plan)
    {
        if (!Cidr.TryParse(plan.Network.AddressPrefix, out var vnet)) return false;

        if (!Cidr.TryParse(plan.Network.SubnetPrefix, out var workload) || !vnet.Contains(workload))
        {
            return true;
        }

        if (!plan.Bastion.Enabled) return false;

        return !Cidr.TryParse(plan.Bastion.SubnetPrefix, out var bastion)
            || !vnet.Contains(bastion)
            || bastion.Overlaps(workload);
    }

    /// <summary>
    /// Computes a workload subnet and a Bastion subnet that both fit inside <paramref name="vnet"/>.
    /// Returns false when the address space is too small to hold both, which is a real answer
    /// rather than a failure - a /27 lab cannot have Bastion, and saying so beats emitting
    /// something that ARM will reject later.
    /// </summary>
    public static bool TryCompute(Cidr vnet, out string workloadPrefix, out string bastionPrefix)
    {
        workloadPrefix = "";
        bastionPrefix = "";

        // A /24 address space gives the workload half of it and Bastion a /26 of the rest; a /16
        // gives the familiar /24. Capped so the workload subnet can never be smaller than the /26
        // that has to follow it.
        var workloadLength = Math.Max(vnet.PrefixLength + 1, 24);
        if (workloadLength > BastionPrefixLength) return false;

        if (!Cidr.TryCreate(vnet.Network, workloadLength, out var workload)) return false;

        // The address after a block of /26-or-larger is always /26-aligned, so this needs no
        // rounding - but TryCreate still refuses it if that ever stops being true.
        var bastionStart = workload.Broadcast + 1;
        if (!Cidr.TryCreate(bastionStart, BastionPrefixLength, out var bastion)) return false;

        if (!vnet.Contains(workload) || !vnet.Contains(bastion)) return false;

        workloadPrefix = workload.ToString();
        bastionPrefix = bastion.ToString();
        return true;
    }

    /// <summary>
    /// Applies <see cref="TryCompute"/> to the plan. The Bastion prefix is only touched when
    /// Bastion is enabled, so turning it off does not quietly rewrite a value the operator chose.
    /// </summary>
    public static bool TryRefit(DeploymentPlan plan)
    {
        if (!Cidr.TryParse(plan.Network.AddressPrefix, out var vnet)) return false;
        if (!TryCompute(vnet, out var workload, out var bastion)) return false;

        plan.Network.SubnetPrefix = workload;

        if (plan.Bastion.Enabled)
        {
            plan.Bastion.SubnetPrefix = bastion;
        }

        return true;
    }
}
