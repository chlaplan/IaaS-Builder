namespace IaaSBuilder.Core.Validation;

public enum ValidationSeverity
{
    Warning,
    Error
}

/// <param name="Severity">Errors block deployment; warnings do not.</param>
/// <param name="Path">Dotted path into the plan, e.g. "servers[2].privateIpAddress".</param>
/// <param name="Message">Human-readable explanation.</param>
public readonly record struct ValidationIssue(ValidationSeverity Severity, string Path, string Message)
{
    public static ValidationIssue Error(string path, string message) =>
        new(ValidationSeverity.Error, path, message);

    public static ValidationIssue Warning(string path, string message) =>
        new(ValidationSeverity.Warning, path, message);

    public override string ToString() => $"[{Severity}] {Path}: {Message}";
}

public sealed class ValidationResult
{
    public ValidationResult(IReadOnlyList<ValidationIssue> issues) => Issues = issues;

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public IEnumerable<ValidationIssue> Errors =>
        Issues.Where(i => i.Severity == ValidationSeverity.Error);

    public IEnumerable<ValidationIssue> Warnings =>
        Issues.Where(i => i.Severity == ValidationSeverity.Warning);

    public bool IsValid => !Errors.Any();
}
