using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using IaaSBuilder.Core.Models;

namespace IaaSBuilder.Core.Azure;

/// <summary>A subscription the signed-in account can see.</summary>
public sealed record AzureSubscriptionInfo(
    string SubscriptionId,
    string DisplayName,
    string? TenantId,
    string? State)
{
    /// <summary>Falls back to the id so a subscription with no name is still selectable.</summary>
    public string Label =>
        string.IsNullOrWhiteSpace(DisplayName) ? SubscriptionId : DisplayName;

    /// <summary>
    /// Disabled and warned subscriptions still enumerate but cannot take a deployment, so the
    /// picker shows the state rather than letting it fail at deploy time.
    /// </summary>
    public bool IsUsable =>
        string.IsNullOrWhiteSpace(State) ||
        State.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An existing resource group, with the region it was created in.</summary>
public sealed record AzureResourceGroupInfo(string Name, string Location);

/// <summary>
/// Reads the account's subscriptions and resource groups so the operator can pick them from a
/// list instead of pasting GUIDs.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="AzureDeploymentService"/>: this is read-only discovery
/// that runs while the plan is still being authored, whereas that class needs a finished plan.
/// Everything here is best-effort - callers must keep working when it returns nothing, because
/// in an air-gapped enclave it always will.
/// </remarks>
public sealed class AzureAccountDirectory
{
    private readonly ArmClient _client;

    public AzureAccountDirectory(TokenCredential credential, AzureCloud cloud)
    {
        _client = new ArmClient(credential, default, new ArmClientOptions
        {
            Environment = AzureCloudEndpoints.GetArmEnvironment(cloud)
        });
    }

    public async Task<IReadOnlyList<AzureSubscriptionInfo>> GetSubscriptionsAsync(
        CancellationToken ct = default)
    {
        var found = new List<AzureSubscriptionInfo>();

        await foreach (var subscription in _client.GetSubscriptions().GetAllAsync(ct))
        {
            var data = subscription.Data;
            if (string.IsNullOrWhiteSpace(data.SubscriptionId))
            {
                continue;
            }

            found.Add(new AzureSubscriptionInfo(
                data.SubscriptionId,
                data.DisplayName ?? "",
                data.TenantId?.ToString(),
                data.State?.ToString()));
        }

        return found
            .OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<AzureResourceGroupInfo>> GetResourceGroupsAsync(
        string subscriptionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return [];
        }

        var subscription = _client.GetSubscriptionResource(
            new ResourceIdentifier($"/subscriptions/{subscriptionId}"));

        var found = new List<AzureResourceGroupInfo>();

        await foreach (var group in subscription.GetResourceGroups().GetAllAsync(cancellationToken: ct))
        {
            found.Add(new AzureResourceGroupInfo(
                group.Data.Name,
                group.Data.Location.Name));
        }

        return found
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
