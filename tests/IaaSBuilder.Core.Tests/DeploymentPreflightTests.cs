using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Core.Tests;

/// <summary>
/// The first two real deployments both failed part way through, leaving a resource group and in
/// one case a virtual network behind. Both causes were readable before anything was created: a
/// missing blob data role, and an ARM failure against a resource that could not be built.
///
/// The blob role is the one that keeps catching people, and Azure's own naming invites it -
/// subscription Owner sounds total and grants nothing on blob data. It caught this user twice,
/// the second time because the role had been granted on 'lab-rg' and the next run targeted
/// 'lab-rg2'.
/// </summary>
public class DeploymentPreflightTests
{
    private const string BlobWrite = DeploymentPreflight.BlobWriteDataAction;

    private static DeploymentPlan PlanWith(Action<DeploymentPlan>? configure = null)
    {
        var plan = PlanFactory.CreateDefault();

        // These tests are about the blob data role and the Microsoft.Storage provider, which only
        // apply when the package is staged in Azure Storage. That stopped being the default when
        // fetching it from a public URL was introduced, so the path under test is opted into
        // explicitly rather than assumed.
        plan.Artifacts.UsePublicPackageUrl = false;

        configure?.Invoke(plan);
        return plan;
    }

    private static PreflightFacts Facts(
        string[]? dataActions = null,
        string[]? notDataActions = null,
        Dictionary<string, string>? providers = null,
        bool? encryptionAtHost = null,
        string[]? actions = null,
        string[]? notActions = null) =>
        new(dataActions ?? [BlobWrite],
            notDataActions ?? [],
            providers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            encryptionAtHost,
            "the subscription",
            // Defaults to Contributor's shape - actions "*" minus the Microsoft.Authorization
            // writes - because that is what almost every real caller has.
            actions ?? ["*"],
            notActions ?? ["Microsoft.Authorization/*/Write"]);

    /// <summary>
    /// Missing the blob data role is survivable: the staging account is created by this tool and
    /// both Owner and Contributor can read its keys, so the package is staged with a key-signed
    /// SAS instead. This used to be Blocking, which stopped deployments that would have worked.
    /// </summary>
    [Fact]
    public void A_missing_blob_data_role_falls_back_to_the_account_key_instead_of_blocking()
    {
        var issues = DeploymentPreflight.Check(PlanWith(), Facts(dataActions: []));

        var issue = Assert.Single(issues);
        Assert.Equal(PreflightSeverity.Warning, issue.Severity);
        Assert.Contains("account key", issue.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no action is needed", issue.Detail);
    }

    /// <summary>
    /// Only when the key fallback is unavailable too is there nothing left to try.
    /// </summary>
    [Fact]
    public void Losing_both_the_blob_role_and_listkeys_blocks_the_deployment()
    {
        var issues = DeploymentPreflight.Check(
            PlanWith(),
            Facts(dataActions: [], actions: ["Microsoft.Resources/*"], notActions: []));

        var issue = Assert.Single(issues);
        Assert.Equal(PreflightSeverity.Blocking, issue.Severity);
        Assert.Contains("neither write blob data nor read storage account keys", issue.Detail);
    }

    /// <summary>
    /// listkeys sits under Microsoft.Storage, so a principal explicitly denied it - rather than
    /// simply not granted it - must also be blocked.
    /// </summary>
    [Fact]
    public void A_notaction_on_listkeys_is_not_mistaken_for_having_it()
    {
        var issues = DeploymentPreflight.Check(
            PlanWith(),
            Facts(dataActions: [],
                  actions: ["*"],
                  notActions: ["Microsoft.Storage/storageAccounts/listkeys/action"]));

        Assert.Equal(PreflightSeverity.Blocking, Assert.Single(issues).Severity);
    }

    /// <summary>
    /// An unreadable permissions API must not be read as "no listkeys" and turned into a block -
    /// the same unknown-is-not-absent rule the data actions already follow.
    /// </summary>
    [Fact]
    public void Unreadable_control_plane_actions_do_not_block()
    {
        var issues = DeploymentPreflight.Check(
            PlanWith(),
            Facts(dataActions: [], actions: null!, notActions: []) with { Actions = null });

        Assert.Equal(PreflightSeverity.Warning, Assert.Single(issues).Severity);
    }

    /// <summary>
    /// Pins the exact action the fallback depends on. The sibling test grants "*", which
    /// wildcard-matches any string and so would keep passing even if the constant were wrong -
    /// a negative control caught exactly that.
    /// </summary>
    [Fact]
    public void The_fallback_depends_on_the_real_listkeys_action_and_not_a_wildcard()
    {
        var issues = DeploymentPreflight.Check(
            PlanWith(),
            Facts(dataActions: [],
                  actions: ["Microsoft.Storage/storageAccounts/listkeys/action"],
                  notActions: []));

        Assert.Equal(PreflightSeverity.Warning, Assert.Single(issues).Severity);

        // A neighbouring Microsoft.Storage action is not a substitute for it.
        var without = DeploymentPreflight.Check(
            PlanWith(),
            Facts(dataActions: [],
                  actions: ["Microsoft.Storage/storageAccounts/read"],
                  notActions: []));

        Assert.Equal(PreflightSeverity.Blocking, Assert.Single(without).Severity);
    }

    [Fact]
    public void The_blob_role_message_explains_that_a_resource_group_grant_does_not_carry_over()
    {
        var issue = Assert.Single(DeploymentPreflight.Check(
            PlanWith(),
            Facts(dataActions: [], actions: ["Microsoft.Resources/*"], notActions: [])));

        // The user granted the role on 'lab-rg', then deployed to 'lab-rg2' and hit it again.
        Assert.Contains("subscription scope is simplest", issue.Detail);
        Assert.Contains("new name on every run", issue.Detail);
    }

    [Fact]
    public void Holding_the_blob_role_raises_nothing()
    {
        Assert.Empty(DeploymentPreflight.Check(PlanWith(), Facts()));
    }

    [Theory]
    [InlineData("Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write")]
    [InlineData("Microsoft.Storage/storageAccounts/blobServices/containers/blobs/*")]
    [InlineData("Microsoft.Storage/*")]
    [InlineData("*")]
    public void Wildcards_in_the_granted_action_count_as_the_blob_role(string granted)
    {
        Assert.Empty(DeploymentPreflight.Check(PlanWith(), Facts(dataActions: [granted])));
    }

    [Theory]
    [InlineData("Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read")]
    [InlineData("Microsoft.KeyVault/*")]
    public void An_unrelated_or_read_only_data_action_is_not_enough(string granted)
    {
        Assert.Single(DeploymentPreflight.Check(PlanWith(), Facts(dataActions: [granted])));
    }

    [Fact]
    public void A_notDataAction_subtracts_from_a_wildcard_grant()
    {
        // A deny-style assignment that strips blob write back out of a broad grant.
        var issues = DeploymentPreflight.Check(
            PlanWith(),
            Facts(dataActions: ["*"], notDataActions: [BlobWrite]));

        Assert.Single(issues);
    }

    [Fact]
    public void Skipping_the_upload_makes_the_blob_role_irrelevant()
    {
        var plan = PlanWith(p => p.Artifacts.SkipUpload = true);

        Assert.Empty(DeploymentPreflight.Check(plan, Facts(dataActions: [])));
    }

    /// <summary>
    /// A tenant can deny the permissions API to an account that is still perfectly able to deploy.
    /// "Could not read" must not be treated as "has no permission", or the preflight becomes the
    /// reason a working deployment is refused - the exact failure it exists to prevent.
    /// </summary>
    [Fact]
    public void An_unreadable_permissions_api_does_not_block()
    {
        var facts = new PreflightFacts(
            DataActions: null,
            NotDataActions: [],
            ProviderStates: new Dictionary<string, string>(),
            EncryptionAtHostRegistered: null,
            ScopeChecked: "the subscription");

        Assert.Empty(DeploymentPreflight.Check(PlanWith(), facts));
    }

    [Fact]
    public void An_empty_but_readable_permission_list_still_blocks()
    {
        // The distinction that matters: [] means "read it, the role is not there".
        Assert.Single(DeploymentPreflight.Check(PlanWith(), Facts(dataActions: [])));
    }

    [Fact]
    public void Skipping_the_upload_also_drops_the_storage_provider_requirement()
    {
        var plan = PlanWith(p => p.Artifacts.SkipUpload = true);

        Assert.DoesNotContain("Microsoft.Storage", DeploymentPreflight.RequiredProviders(plan));
    }

    [Fact]
    public void An_unregistered_provider_blocks_the_deployment()
    {
        var issues = DeploymentPreflight.Check(
            PlanWith(),
            Facts(providers: new(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.Compute"] = "NotRegistered"
            }));

        var issue = Assert.Single(issues);
        Assert.Equal(PreflightSeverity.Blocking, issue.Severity);
        Assert.Contains("Microsoft.Compute", issue.Title);
        Assert.Contains("az provider register", issue.Detail);
    }

    [Theory]
    [InlineData("Registered")]
    [InlineData("registered")]
    [InlineData("Registering")]
    public void A_registered_or_registering_provider_is_accepted(string state)
    {
        var issues = DeploymentPreflight.Check(
            PlanWith(),
            Facts(providers: new(StringComparer.OrdinalIgnoreCase) { ["Microsoft.Compute"] = state }));

        Assert.Empty(issues);
    }

    [Fact]
    public void Providers_that_could_not_be_read_are_not_reported_as_unregistered()
    {
        // An empty map means the read failed. Refusing to deploy on the strength of a failed
        // read would block deployments that would have worked.
        Assert.Empty(DeploymentPreflight.Check(PlanWith(), Facts(providers: [])));
    }

    [Fact]
    public void A_provider_missing_from_a_populated_map_is_not_reported()
    {
        var issues = DeploymentPreflight.Check(
            PlanWith(),
            Facts(providers: new(StringComparer.OrdinalIgnoreCase) { ["Microsoft.Network"] = "Registered" }));

        Assert.Empty(issues);
    }

    [Fact]
    public void Avd_adds_the_desktop_virtualization_provider()
    {
        var plan = PlanWith(p => p.Avd = new AvdSpec { Enabled = true });

        Assert.Contains("Microsoft.DesktopVirtualization", DeploymentPreflight.RequiredProviders(plan));
    }

    [Fact]
    public void Avd_that_is_present_but_disabled_does_not_add_the_provider()
    {
        var plan = PlanWith(p => p.Avd = new AvdSpec { Enabled = false });

        Assert.DoesNotContain("Microsoft.DesktopVirtualization", DeploymentPreflight.RequiredProviders(plan));
    }

    [Fact]
    public void A_key_vault_password_reference_adds_the_key_vault_provider()
    {
        var plan = PlanWith(p => p.Identity.AdminPasswordSecret =
            new KeyVaultSecretRef { VaultName = "kv", SecretName = "admin" });

        Assert.Contains("Microsoft.KeyVault", DeploymentPreflight.RequiredProviders(plan));
    }

    [Fact]
    public void The_provider_list_never_repeats_a_namespace()
    {
        var plan = PlanWith(p =>
        {
            p.Avd = new AvdSpec { Enabled = true };
            p.Mlz = new MlzSpec { Enabled = true, DeployDefender = true, DeploySentinel = true };
        });

        var providers = DeploymentPreflight.RequiredProviders(plan);

        Assert.Equal(providers.Count, providers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Mlz_blocks_when_encryption_at_host_is_not_registered()
    {
        var plan = PlanWith(p => p.Mlz = new MlzSpec { Enabled = true });

        var issues = DeploymentPreflight.Check(plan, Facts(encryptionAtHost: false));

        var issue = Assert.Single(issues);
        Assert.Contains("EncryptionAtHost", issue.Title);
        Assert.Contains("az feature register", issue.Detail);
    }

    [Fact]
    public void Mlz_is_happy_when_encryption_at_host_is_registered()
    {
        var plan = PlanWith(p => p.Mlz = new MlzSpec { Enabled = true });

        Assert.Empty(DeploymentPreflight.Check(plan, Facts(encryptionAtHost: true)));
    }

    [Fact]
    public void An_unknown_encryption_at_host_state_does_not_block()
    {
        var plan = PlanWith(p => p.Mlz = new MlzSpec { Enabled = true });

        Assert.Empty(DeploymentPreflight.Check(plan, Facts(encryptionAtHost: null)));
    }

    [Fact]
    public void Encryption_at_host_is_not_required_without_mlz()
    {
        Assert.Empty(DeploymentPreflight.Check(PlanWith(), Facts(encryptionAtHost: false)));
    }

    [Fact]
    public void Every_blocking_issue_is_reported_together_rather_than_one_at_a_time()
    {
        var plan = PlanWith(p => p.Mlz = new MlzSpec { Enabled = true });

        var issues = DeploymentPreflight.Check(plan, Facts(
            dataActions: [],
            providers: new(StringComparer.OrdinalIgnoreCase) { ["Microsoft.Compute"] = "NotRegistered" },
            encryptionAtHost: false,
            // No listkeys either, so the staging issue is Blocking rather than the key-fallback
            // warning - this test is about reporting every blocker at once.
            actions: ["Microsoft.Resources/*"],
            notActions: []));

        // Fixing one thing, redeploying, and discovering the next is the experience this avoids.
        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Equal(PreflightSeverity.Blocking, i.Severity));
    }

    [Fact]
    public void The_scope_that_was_checked_is_named_in_the_message()
    {
        var facts = new PreflightFacts(
            [], [], new Dictionary<string, string>(), null, "resource group 'lab-rg2'",
            Actions: ["Microsoft.Resources/*"], NotActions: []);

        var issue = Assert.Single(DeploymentPreflight.Check(PlanWith(), facts));

        Assert.Contains("lab-rg2", issue.Detail);
    }
}
