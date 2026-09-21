using System.Text.Json;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Web.Services;

/// <summary>
/// The handful of values that are genuine <em>choices</em> rather than engineering defaults:
/// which subscription, which region, which resource group, what the domain is called.
/// </summary>
/// <remarks>
/// These start blank. A prepopulated choice is a guess presented as an answer - "eastus" and
/// "contoso.local" are not decisions anyone made, and worse, they make the getting-started
/// checklist tick "Pick a region" before the operator has picked anything. The rest of the plan
/// (address spaces, VM sizes, disk types, image versions) stays prefilled, because a blank CIDR
/// helps nobody and those are defaults in the real sense: values that are right until you have a
/// reason to change them.
///
/// Once an operator saves their settings the choices come back on the next visit, which is the
/// case prepopulation was trying to serve in the first place.
/// </remarks>
public sealed record OperatorSettings(
    AzureCloud Cloud,
    string? SubscriptionId,
    string? TenantId,
    string? Location,
    string? ResourceGroup,
    string? DomainName,
    string? AdminUsername,
    bool ShowVmPrices = true)
{
    /// <summary>Deliberately never includes the administrator password.</summary>
    public static OperatorSettings Capture(DeploymentPlan plan, bool showVmPrices = true) => new(
        plan.Azure.Cloud,
        plan.Azure.SubscriptionId,
        plan.Azure.TenantId,
        plan.Azure.Location,
        plan.Azure.ResourceGroup,
        plan.Identity.DomainName,
        plan.Identity.AdminUsername,
        showVmPrices);

    /// <summary>
    /// Blanks the choice fields on a freshly created plan. Applied in the web layer only - the
    /// CLI's <c>init</c> and a large number of tests rely on <c>PlanFactory</c>'s defaults, and
    /// a headless <c>init</c> that produced an unvalidatable plan would be a regression.
    /// </summary>
    public static void ClearChoices(DeploymentPlan plan)
    {
        plan.Azure.SubscriptionId = "";
        plan.Azure.TenantId = null;
        plan.Azure.Location = "";
        plan.Azure.ResourceGroup = "";
        plan.Identity.DomainName = "";
        plan.Identity.AdminUsername = "";
    }

    /// <summary>
    /// Restores saved choices. Each field is applied only when it holds something, so a settings
    /// blob written by an older build that did not know about a field leaves it blank rather
    /// than wiping it.
    /// </summary>
    public void ApplyTo(DeploymentPlan plan)
    {
        plan.Azure.Cloud = Cloud;

        if (!string.IsNullOrWhiteSpace(SubscriptionId)) plan.Azure.SubscriptionId = SubscriptionId;
        if (!string.IsNullOrWhiteSpace(TenantId)) plan.Azure.TenantId = TenantId;
        if (!string.IsNullOrWhiteSpace(Location)) plan.Azure.Location = Location;
        if (!string.IsNullOrWhiteSpace(ResourceGroup)) plan.Azure.ResourceGroup = ResourceGroup;
        if (!string.IsNullOrWhiteSpace(DomainName)) plan.Identity.DomainName = DomainName;
        if (!string.IsNullOrWhiteSpace(AdminUsername)) plan.Identity.AdminUsername = AdminUsername;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static OperatorSettings? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OperatorSettings>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Corrupt or hand-edited storage must not take the page down on first render.
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
