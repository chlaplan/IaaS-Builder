using IaaSBuilder.Core;
using IaaSBuilder.Core.Dsc;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Serialization;
using IaaSBuilder.Core.Templates;
using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Web.Services;

/// <summary>
/// The single in-memory deployment plan the UI edits.
/// </summary>
/// <remarks>
/// This replaces the ~200 live WPF controls that the legacy script read directly at the point
/// of deployment, and the 150 lines of hand-written CSV field mapping that tried (and failed)
/// to persist them. The plan is now one object: editable, validatable, and round-trippable.
/// Registered as a singleton because this is a single-operator desktop tool that happens to
/// present a browser UI.
/// </remarks>
public sealed class PlanState
{
    private readonly TemplateResolver _templates;
    private readonly CatalogState _catalog;
    private readonly Lock _gate = new();

    public PlanState(TemplateResolver templates, CatalogState catalog)
    {
        _templates = templates;
        _catalog = catalog;
        Plan = PlanFactory.CreateDefault();
        OperatorSettings.ClearChoices(Plan);
        _catalog.TargetCloud = Plan.Azure.Cloud;
    }

    /// <summary>
    /// Every notification tells the catalog which cloud the plan now targets. Done here rather
    /// than in each editor because forgetting it in one place is silent: the region list simply
    /// keeps showing the previous cloud's regions and looks perfectly normal.
    /// </summary>
    private void RaiseChanged()
    {
        _catalog.TargetCloud = Plan.Azure.Cloud;
        Changed?.Invoke();
    }

    public DeploymentPlan Plan { get; private set; }

    /// <summary>Where the plan was last saved to or loaded from.</summary>
    public string? PlanPath { get; private set; }

    /// <summary>
    /// Held only in memory and never written to the plan file. Cleared on demand.
    /// </summary>
    public string AdminPassword { get; set; } = "";

    public bool IsDirty { get; private set; }

    /// <summary>
    /// True until the operator edits or loads anything. Used to keep a first-time visitor from
    /// being met by a list of validation errors about fields deliberately left blank - the
    /// getting-started checklist is already asking for exactly those, in order, and saying the
    /// same thing twice in red reads as though something is broken.
    /// </summary>
    public bool IsPristine { get; private set; } = true;

    public event Action? Changed;

    /// <summary>
    /// Raised by editors after mutating <see cref="Plan"/> so every open component refreshes.
    /// </summary>
    public void NotifyChanged()
    {
        IsDirty = true;
        IsPristine = false;
        RaiseChanged();
    }

    /// <summary>
    /// Applies previously saved operator choices without counting as an edit, so a returning
    /// operator is neither warned about unsaved changes nor shown validation for fields they
    /// have not touched this visit.
    /// </summary>
    public void ApplySavedSettings(OperatorSettings settings)
    {
        settings.ApplyTo(Plan);
        RaiseChanged();
    }

    /// <summary>
    /// Plan-internal validation plus the catalog checks, so an unavailable region or a
    /// non-existent image SKU shows up while editing rather than part way through a deployment.
    /// </summary>
    public ValidationResult Validate()
    {
        using var secrets = new DeploymentSecrets(AdminPassword);
        var result = new DeploymentPlanValidator(AvailableDscTokens()).Validate(Plan, secrets);
        var preflight = CatalogPreflight.Check(Plan, _catalog.Catalog);

        return preflight.Count == 0
            ? result
            : new ValidationResult([.. result.Issues, .. preflight]);
    }

    /// <summary>
    /// The role tokens actually present in the shipped DSC package.
    /// </summary>
    /// <remarks>
    /// The legacy form offered a "Domain Join" option that mapped to a configuration name the
    /// package does not contain, so the VM built and then silently never joined the domain.
    /// Reading the package means the UI can only offer roles that can really be configured.
    /// </remarks>
    public ISet<string>? AvailableDscTokens()
    {
        lock (_gate)
        {
            try
            {
                var path = _templates.Resolve(Plan.Artifacts.DscPackagePath);
                var tokens = DscPackageInspector.GetAvailableRoleTokens(path);
                return tokens.Count > 0 ? tokens : null;
            }
            catch
            {
                // No package on disk: fall back to accepting every token rather than
                // blocking the operator from editing a plan.
                return null;
            }
        }
    }

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        await DeploymentPlanSerializer.SaveAsync(Plan, path, ct);
        PlanPath = Path.GetFullPath(path);
        IsDirty = false;
        Changed?.Invoke();
    }

    public string Serialize() => DeploymentPlanSerializer.Serialize(Plan);

    public void Load(string json, string? path = null)
    {
        Plan = DeploymentPlanSerializer.Deserialize(json);
        PlanPath = path is null ? null : Path.GetFullPath(path);
        IsDirty = false;
        IsPristine = false;
        RaiseChanged();
    }

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        Plan = await DeploymentPlanSerializer.LoadAsync(path, ct);
        PlanPath = Path.GetFullPath(path);
        IsDirty = false;
        IsPristine = false;
        RaiseChanged();
    }

    public void Reset(string prefix = "lab", string domainName = "contoso.local")
    {
        Plan = PlanFactory.CreateDefault(prefix, domainName);
        OperatorSettings.ClearChoices(Plan);
        PlanPath = null;
        IsDirty = false;
        IsPristine = true;
        RaiseChanged();
    }
}
