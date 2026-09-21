using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Deployment;

/// <summary>
/// Expands the SACA model into the flat, conventionally-named parameters the SACA
/// templates declare (<c>Subnet_Management</c>, <c>BIGIP_VM1_ExternalPri_IP</c>, ...).
/// </summary>
/// <remarks>
/// The legacy form carried one text box per value - roughly 45 of them, named
/// <c>SACA_BIGIP1Ext1Pri_IP</c> through <c>SACA_IPS2InternalSec_IP</c> - and every one was
/// passed by hand on a 40-line command line. Because the template parameter names follow a
/// strict convention, the dictionaries on <see cref="SacaSpec"/> can be expanded
/// mechanically instead. Anything a given template does not declare is dropped by
/// <see cref="Templates.ArmTemplate.Bind"/>.
/// </remarks>
public static class SacaParameterBinder
{
    public static Dictionary<string, object?> BuildNetwork(DeploymentPlan plan, SacaSpec saca)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["VNetName"] = saca.VNetName,
            ["DNSLabel"] = saca.DnsLabel,
            ["Location"] = plan.Azure.Location
        };

        AddSubnets(saca, parameters);
        AddSharedAddresses(saca, parameters);
        return parameters;
    }

    public static Dictionary<string, object?> BuildAppliances(
        DeploymentPlan plan,
        SacaSpec saca,
        DeploymentSecrets secrets)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["VNetName"] = saca.VNetName,
            ["DNSLabel"] = saca.DnsLabel,
            ["Location"] = plan.Azure.Location,
            ["StorageAccountName"] = plan.Artifacts.StorageAccountName,
            ["adminUsername"] = plan.Identity.AdminUsername,
            ["adminPassword"] = secrets.AdminPassword,
            ["governmentCloudRegion"] = plan.Azure.Cloud == AzureCloud.UsGovernment,
            ["STIGDevice"] = plan.Hardening.ApplyStig,
            ["DHostID"] = plan.DedicatedHost is { Enabled: true } host ? host.HostId ?? "" : ""
        };

        AddSubnets(saca, parameters);
        AddSharedAddresses(saca, parameters);

        foreach (var (key, appliance) in saca.Appliances)
        {
            parameters[$"{key}_Name"] = appliance.Name;
            parameters[$"{key}_Size"] = appliance.VmSize;

            foreach (var (nic, address) in appliance.Addresses)
            {
                parameters[$"{key}_{nic}_IP"] = address;
            }
        }

        return parameters;
    }

    private static void AddSubnets(SacaSpec saca, Dictionary<string, object?> parameters)
    {
        foreach (var (key, subnet) in saca.Subnets)
        {
            parameters[$"Subnet_{key}_Name"] = subnet.Name;
            parameters[$"Subnet_{key}"] = subnet.AddressPrefix;
        }
    }

    private static void AddSharedAddresses(SacaSpec saca, Dictionary<string, object?> parameters)
    {
        foreach (var (key, address) in saca.SharedAddresses)
        {
            parameters[key] = address;
        }
    }
}
