using IaaSBuilder.Core.Azure;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The storage upload has failed on four separate real deployments, and every time the message
/// named two possible causes and two different fixes and left the operator to work out which
/// applied. The two causes need genuinely different actions - one is a missing role assignment,
/// the other is a tenant policy - so guessing wrong costs another failed deployment.
/// </summary>
public class StorageFailureAdviceTests
{
    private const string Account = "labdsc7341";
    private const string ResourceGroup = "rg-lab44";
    private const string Subscription = "00000000-1111-2222-3333-444444444444";

    private static string Explain(AzureDeploymentService.SharedKeyStatus status) =>
        AzureDeploymentService.ExplainUploadFailure(status, Account, ResourceGroup, Subscription);

    [Fact]
    public void The_ordinary_case_asks_for_the_blob_data_role_and_nothing_else()
    {
        var message = Explain(AzureDeploymentService.SharedKeyStatus.CannotListKeys);

        Assert.Contains("Storage Blob Data Contributor", message);

        // Storage Blob Delegator is only needed when shared key is off. Naming it here would send
        // the operator to grant a role that has nothing to do with their failure.
        Assert.DoesNotContain("Storage Blob Delegator", message);
    }

    /// <summary>
    /// The case a real deployment hit. Shared key disabled also kills service and account SAS, so
    /// the read SAS must become a user delegation SAS - which needs a second, separate role.
    /// Advising only the blob data role here would produce another failed run.
    /// </summary>
    [Fact]
    public void The_policy_case_asks_for_both_roles_and_names_the_policy()
    {
        var message = Explain(AzureDeploymentService.SharedKeyStatus.DisabledOnAccount);

        Assert.Contains("Storage Blob Data Contributor", message);
        Assert.Contains("Storage Blob Delegator", message);
        Assert.Contains("prevent shared key access", message);
    }

    [Theory]
    [InlineData(AzureDeploymentService.SharedKeyStatus.CannotListKeys)]
    [InlineData(AzureDeploymentService.SharedKeyStatus.DisabledOnAccount)]
    internal void Every_explanation_is_actionable(AzureDeploymentService.SharedKeyStatus status)
    {
        var message = Explain(status);

        // A runnable command, scoped to the resource group that actually failed - not a
        // placeholder the operator has to reconstruct from the portal.
        Assert.Contains("az role assignment create", message);
        Assert.Contains($"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}", message);
        Assert.Contains(Account, message);

        // The air-gapped escape hatch stays reachable from both.
        Assert.Contains("skipUpload", message);
    }

    /// <summary>
    /// Guards the guard: if the two messages ever become identical, every assertion above would
    /// still pass while the whole point of distinguishing them had been lost.
    /// </summary>
    [Fact]
    public void The_two_explanations_are_actually_different()
    {
        Assert.NotEqual(
            Explain(AzureDeploymentService.SharedKeyStatus.CannotListKeys),
            Explain(AzureDeploymentService.SharedKeyStatus.DisabledOnAccount));
    }

    /// <summary>
    /// Owner and Contributor both carry <c>listKeys</c>, so a tenant where it fails is off the
    /// documented path and the operator's log is the only evidence there will ever be. The
    /// exception used to be caught and discarded, which made that log undiagnosable.
    /// </summary>
    [Fact]
    public void The_reason_list_keys_failed_reaches_the_operator()
    {
        const string Reason = "listKeys failed with HTTP 403 (AuthorizationFailed): denied by policy.";

        var message = AzureDeploymentService.ExplainUploadFailure(
            AzureDeploymentService.SharedKeyStatus.CannotListKeys,
            Account,
            "rg-lab55",
            Subscription,
            Reason);

        Assert.Contains(Reason, message, StringComparison.Ordinal);

        // Appended, not substituted - the operator still needs to be told what to do about it.
        Assert.Contains("Storage Blob Data Contributor", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The status code and error code are what distinguish "denied" from "the api-version this
    /// sovereign cloud serves is different", so both have to survive into the message.
    /// </summary>
    [Fact]
    public void The_key_failure_description_names_the_status_and_the_error_code()
    {
        var described = AzureDeploymentService.DescribeKeyFailure(
            new global::Azure.RequestFailedException(403, "Denied.\nMore detail here.", "AuthorizationFailed", null));

        Assert.Contains("403", described, StringComparison.Ordinal);
        Assert.Contains("AuthorizationFailed", described, StringComparison.Ordinal);
        Assert.Contains("Denied.", described, StringComparison.Ordinal);
    }
}
