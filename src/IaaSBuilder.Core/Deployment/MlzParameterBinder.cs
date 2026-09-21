using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Deployment;

/// <summary>
/// Binds <see cref="MlzSpec"/> onto the parameters of Microsoft's Mission Landing Zone template.
/// </summary>
/// <remarks>
/// MLZ declares over a hundred parameters and requires exactly one of them - <c>identifier</c>.
/// Everything else has a sensible default, so this deliberately binds only the settings the UI
/// exposes and lets the template decide the rest. That is not laziness: three of MLZ's defaults
/// (<c>windowsVmAdminPassword</c>, <c>linuxVmAdminPasswordOrKey</c>, <c>deploymentNameSuffix</c>)
/// are <c>newGuid()</c> and <c>utcNow()</c> expressions, which are only legal as defaults of a
/// top-level parameter. Supplying our own value for them is not an improvement, and supplying an
/// empty string where a 12-character minimum is declared would fail validation outright.
/// </remarks>
public static class MlzParameterBinder
{
    public static Dictionary<string, object?> Build(DeploymentPlan plan, MlzSpec mlz)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["identifier"] = mlz.Identifier,
            ["location"] = plan.Azure.Location,
            ["environmentAbbreviation"] = mlz.EnvironmentAbbreviation,

            // All four default to the deployment subscription upstream, which is exactly the
            // single-subscription layout. They are bound explicitly so that a plan naming separate
            // tier subscriptions round-trips, and so the deployment does not silently depend on
            // which subscription happened to be selected.
            ["hubSubscriptionId"] = Tier(mlz.HubSubscriptionId, plan),
            ["identitySubscriptionId"] = Tier(mlz.IdentitySubscriptionId, plan),
            ["operationsSubscriptionId"] = Tier(mlz.OperationsSubscriptionId, plan),
            ["sharedServicesSubscriptionId"] = Tier(mlz.SharedServicesSubscriptionId, plan),

            ["deployIdentity"] = mlz.DeployIdentity,
            ["deployBastion"] = mlz.DeployBastion,

            // Azure Firewall Premium carries the IDPS feature that satisfies the SCCA VDSS
            // requirement; Standard does not. Defaulting to Premium matches upstream.
            ["firewallSkuTier"] = mlz.FirewallSkuTier,
            ["firewallIntrusionDetectionMode"] = mlz.FirewallIntrusionDetectionMode,
            ["firewallThreatIntelMode"] = mlz.FirewallThreatIntelMode,

            ["deployDefender"] = mlz.DeployDefender,
            ["defenderSkuTier"] = mlz.DefenderSkuTier,
            ["deploySentinel"] = mlz.DeploySentinel,

            ["deployPolicy"] = mlz.DeployPolicy,
            ["policy"] = mlz.Policy
        };

        // Upstream declares this as a plain string with a '' default and only uses it when Defender
        // is on. Sending an empty string is harmless, but sending nothing is tidier.
        if (!string.IsNullOrWhiteSpace(mlz.EmailSecurityContact))
        {
            parameters["emailSecurityContact"] = mlz.EmailSecurityContact.Trim();
        }

        // MLZ takes one tags object rather than individual parameters.
        if (plan.Azure.Tags.Count > 0)
        {
            parameters["tags"] = new Dictionary<string, string>(plan.Azure.Tags);
        }

        return parameters;
    }

    private static string Tier(string? configured, DeploymentPlan plan) =>
        string.IsNullOrWhiteSpace(configured) ? plan.Azure.SubscriptionId : configured.Trim();
}
