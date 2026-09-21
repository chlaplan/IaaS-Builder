using IaaSBuilder.Core.Validation;

namespace IaaSBuilder.Web.Services;

/// <summary>
/// The live validation result, computed once per plan change and shared by every field on screen.
/// </summary>
/// <remarks>
/// Two reasons this exists rather than each field validating itself.
///
/// First, correctness. The rules live in <see cref="DeploymentPlanValidator"/> - storage account
/// names are 3-24 lowercase alphanumerics, computer names are at most 15 characters, "admin" is
/// reserved, and so on. Re-expressing any of that in the markup would create a second copy that
/// drifts, and the copy the operator sees would not be the copy that blocks the deployment. Fields
/// therefore declare only *which* value they edit, as the dotted path the validator already
/// reports (<c>artifacts.storageAccountName</c>), and ask here what is wrong with it. Every rule
/// that exists today gets an inline error for free, and so does every rule added later.
///
/// Second, cost. There are around sixty field instances on screen. Validation walks the whole
/// plan and opens the DSC package to read its configuration names; doing that once per field per
/// keystroke would be sixty full passes. It runs once per change here instead.
/// </remarks>
public sealed class ValidationState : IDisposable
{
    private readonly PlanState _plan;
    private Dictionary<string, List<ValidationIssue>> _byPath = [];

    public ValidationState(PlanState plan)
    {
        _plan = plan;
        _plan.Changed += Recompute;
        Recompute();
    }

    /// <summary>The whole result, for the summary panel.</summary>
    public ValidationResult Result { get; private set; } = new([]);

    public event Action? Changed;

    private void Recompute()
    {
        Result = _plan.Validate();

        var byPath = new Dictionary<string, List<ValidationIssue>>(StringComparer.OrdinalIgnoreCase);

        foreach (var issue in Result.Issues)
        {
            if (!byPath.TryGetValue(issue.Path, out var list))
            {
                byPath[issue.Path] = list = [];
            }

            list.Add(issue);
        }

        _byPath = byPath;
        Changed?.Invoke();
    }

    /// <summary>
    /// Issues recorded against exactly this path. An empty path matches nothing, so a field that
    /// has not been told which value it edits simply never shows an error rather than throwing.
    /// </summary>
    public IReadOnlyList<ValidationIssue> For(string? path) =>
        string.IsNullOrEmpty(path) || !_byPath.TryGetValue(path, out var issues)
            ? []
            : issues;

    /// <summary>
    /// Whether this field should be shown as wrong.
    /// </summary>
    /// <remarks>
    /// Suppressed while the plan is pristine. The choice fields start deliberately blank, so a
    /// first visit would otherwise open with half the form outlined in red describing work the
    /// operator has not had a chance to do - which reads as breakage rather than an empty form.
    /// The moment anything is edited, every rule applies everywhere.
    /// </remarks>
    public bool HasError(string? path) =>
        !_plan.IsPristine && For(path).Any(i => i.Severity == ValidationSeverity.Error);

    public bool HasWarning(string? path) =>
        !_plan.IsPristine && For(path).Any(i => i.Severity == ValidationSeverity.Warning);

    /// <summary>The messages to show under the field, most severe first.</summary>
    public IReadOnlyList<string> MessagesFor(string? path) =>
        _plan.IsPristine
            ? []
            : [.. For(path)
                .OrderByDescending(i => i.Severity)
                .Select(i => i.Message)];

    public void Dispose() => _plan.Changed -= Recompute;
}
