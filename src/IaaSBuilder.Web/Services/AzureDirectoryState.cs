using IaaSBuilder.Core.Azure;

namespace IaaSBuilder.Web.Services;

/// <summary>
/// Holds the subscriptions and resource groups discovered after sign-in, so the Azure page can
/// offer lists instead of asking for a pasted GUID.
/// </summary>
/// <remarks>
/// Every lookup here is best-effort and additive. A failure leaves the previous data in place and
/// records the message - it never blocks authoring, because the whole point of this tool is that a
/// plan can be built with no connection at all.
/// </remarks>
public sealed class AzureDirectoryState
{
    private readonly AzureSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AzureDirectoryState(AzureSession session)
    {
        _session = session;
    }

    public IReadOnlyList<AzureSubscriptionInfo> Subscriptions { get; private set; } = [];
    public IReadOnlyList<AzureResourceGroupInfo> ResourceGroups { get; private set; } = [];

    /// <summary>The subscription <see cref="ResourceGroups"/> was loaded for.</summary>
    public string? ResourceGroupsSubscriptionId { get; private set; }

    public bool IsLoadingSubscriptions { get; private set; }
    public bool IsLoadingResourceGroups { get; private set; }
    public string? LastError { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<string> ResourceGroupNames =>
        ResourceGroups.Select(g => g.Name).ToList();

    public AzureResourceGroupInfo? FindResourceGroup(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : ResourceGroups.FirstOrDefault(
                g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public AzureSubscriptionInfo? FindSubscription(string? subscriptionId) =>
        string.IsNullOrWhiteSpace(subscriptionId)
            ? null
            : Subscriptions.FirstOrDefault(
                s => s.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase));

    public async Task LoadSubscriptionsAsync(CancellationToken ct = default)
    {
        if (!_session.IsSignedIn)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        IsLoadingSubscriptions = true;
        LastError = null;
        Changed?.Invoke();

        try
        {
            var directory = new AzureAccountDirectory(_session.Credential, _session.Cloud);
            Subscriptions = await directory.GetSubscriptionsAsync(ct);
        }
        catch (Exception ex)
        {
            LastError = $"Could not list subscriptions: {ex.Message}";
        }
        finally
        {
            IsLoadingSubscriptions = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task LoadResourceGroupsAsync(string subscriptionId, CancellationToken ct = default)
    {
        if (!_session.IsSignedIn || string.IsNullOrWhiteSpace(subscriptionId))
        {
            return;
        }

        await _gate.WaitAsync(ct);
        IsLoadingResourceGroups = true;
        LastError = null;
        Changed?.Invoke();

        try
        {
            var directory = new AzureAccountDirectory(_session.Credential, _session.Cloud);
            ResourceGroups = await directory.GetResourceGroupsAsync(subscriptionId, ct);
            ResourceGroupsSubscriptionId = subscriptionId;
        }
        catch (Exception ex)
        {
            // Keep whatever was listed before: a stale list is more useful than an empty one.
            LastError = $"Could not list resource groups: {ex.Message}";
        }
        finally
        {
            IsLoadingResourceGroups = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Called on sign-out so one account's directory is never shown to the next.</summary>
    public void Clear()
    {
        Subscriptions = [];
        ResourceGroups = [];
        ResourceGroupsSubscriptionId = null;
        LastError = null;
        Changed?.Invoke();
    }
}
