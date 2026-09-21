namespace IaaSBuilder.Core.Deployment;

public enum StepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

/// <summary>A single unit of work in a deployment.</summary>
public sealed class DeploymentStep
{
    public DeploymentStep(
        string id,
        string displayName,
        IReadOnlyList<string> dependsOn,
        Func<CancellationToken, Task> executeAsync)
    {
        Id = id;
        DisplayName = displayName;
        DependsOn = dependsOn;
        ExecuteAsync = executeAsync;
    }

    public string Id { get; }
    public string DisplayName { get; }

    /// <summary>Ids of steps that must reach <see cref="StepStatus.Succeeded"/> first.</summary>
    public IReadOnlyList<string> DependsOn { get; }

    public Func<CancellationToken, Task> ExecuteAsync { get; }

    public StepStatus Status { get; internal set; } = StepStatus.Pending;
    public string? Error { get; internal set; }

    /// <summary>
    /// Something worth saying about a step that still succeeded. Reported in place of
    /// <see cref="Error"/>, which stays meaning "this went wrong".
    /// </summary>
    public string? Note { get; internal set; }

    public TimeSpan? Duration { get; internal set; }
}

public sealed record DeploymentProgress(
    string StepId,
    string DisplayName,
    StepStatus Status,
    string? Message = null,
    TimeSpan? Duration = null);

public sealed record DeploymentRunResult(IReadOnlyList<DeploymentStep> Steps)
{
    public bool Succeeded => Steps.All(s => s.Status is StepStatus.Succeeded or StepStatus.Skipped);
    public IEnumerable<DeploymentStep> Failed => Steps.Where(s => s.Status == StepStatus.Failed);
}
