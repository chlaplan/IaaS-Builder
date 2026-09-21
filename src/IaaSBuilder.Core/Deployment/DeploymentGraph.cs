using System.Diagnostics;

namespace IaaSBuilder.Core.Deployment;

/// <summary>
/// Executes deployment steps in dependency order, running independent steps concurrently.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the legacy script's ordering strategy, which was: fire every
/// <c>New-AzResourceGroupDeployment</c> with <c>-AsJob</c>, never look at the jobs again,
/// and sleep for a guessed number of seconds when something genuinely needed to wait
/// (<c>Start-Sleep -Seconds 660</c> before building AVD, so the DC "should" be up).
/// That was simultaneously too slow for a fast subscription and a race on a slow one.
/// </para>
/// <para>
/// Dependencies are declared as data on each step, so the engine waits for the exact
/// thing it needs, runs everything else in parallel, and skips work whose prerequisites
/// failed instead of deploying on top of a broken foundation.
/// </para>
/// </remarks>
public sealed class DeploymentGraph
{
    private readonly Dictionary<string, DeploymentStep> _steps;

    public DeploymentGraph(IEnumerable<DeploymentStep> steps)
    {
        _steps = new Dictionary<string, DeploymentStep>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            if (!_steps.TryAdd(step.Id, step))
            {
                throw new ArgumentException($"Duplicate deployment step id '{step.Id}'.", nameof(steps));
            }
        }

        ValidateDependencies();
    }

    public IReadOnlyCollection<DeploymentStep> Steps => _steps.Values;

    /// <summary>Maximum number of steps to run at once.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 8;

    private void ValidateDependencies()
    {
        foreach (var step in _steps.Values)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!_steps.ContainsKey(dependency))
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Id}' depends on '{dependency}', which is not part of this deployment.");
                }
            }
        }

        DetectCycles();
    }

    private void DetectCycles()
    {
        var state = _steps.Keys.ToDictionary(k => k, _ => 0, StringComparer.OrdinalIgnoreCase);
        var path = new Stack<string>();

        foreach (var id in _steps.Keys)
        {
            Visit(id);
        }

        void Visit(string id)
        {
            switch (state[id])
            {
                case 2: return;
                case 1:
                    throw new InvalidOperationException(
                        $"Circular dependency detected: {string.Join(" -> ", path.Reverse())} -> {id}");
            }

            state[id] = 1;
            path.Push(id);

            foreach (var dependency in _steps[id].DependsOn)
            {
                Visit(dependency);
            }

            path.Pop();
            state[id] = 2;
        }
    }

    /// <summary>
    /// Returns the step ids grouped into waves; every step in a wave can run concurrently.
    /// Exposed mainly so the UI and the CLI can show a plan before anything is deployed.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> GetExecutionWaves()
    {
        var remaining = new HashSet<string>(_steps.Keys, StringComparer.OrdinalIgnoreCase);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var waves = new List<IReadOnlyList<string>>();

        while (remaining.Count > 0)
        {
            var wave = remaining
                .Where(id => _steps[id].DependsOn.All(completed.Contains))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ValidateDependencies already rules this out, but fail loudly rather than loop.
            if (wave.Count == 0)
            {
                throw new InvalidOperationException("Deployment graph cannot make progress.");
            }

            foreach (var id in wave)
            {
                remaining.Remove(id);
                completed.Add(id);
            }

            waves.Add(wave);
        }

        return waves;
    }

    public async Task<DeploymentRunResult> RunAsync(
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new HashSet<string>(_steps.Keys, StringComparer.OrdinalIgnoreCase);

        // A list, not a dictionary keyed by Task: a step that completes synchronously can
        // hand back a cached, reference-equal Task, which would collide as a key.
        var running = new List<Task<DeploymentStep>>();
        using var throttle = new SemaphoreSlim(MaxDegreeOfParallelism);

        while (pending.Count > 0 || running.Count > 0)
        {
            foreach (var id in pending.ToList())
            {
                var step = _steps[id];

                // A prerequisite failed, was skipped or was cancelled: this can never run.
                if (step.DependsOn.Any(d => _steps[d].Status is StepStatus.Failed or StepStatus.Skipped or StepStatus.Cancelled))
                {
                    pending.Remove(id);
                    step.Status = StepStatus.Skipped;
                    var blocker = step.DependsOn.First(d =>
                        _steps[d].Status is StepStatus.Failed or StepStatus.Skipped or StepStatus.Cancelled);
                    step.Error = $"Skipped because '{_steps[blocker].DisplayName}' did not succeed.";
                    Report(progress, step);
                    continue;
                }

                if (!step.DependsOn.All(completed.Contains))
                {
                    continue;
                }

                pending.Remove(id);
                running.Add(ExecuteAsync(step, throttle, progress, cancellationToken));
            }

            if (running.Count == 0)
            {
                continue;
            }

            var finished = await Task.WhenAny(running);
            running.Remove(finished);
            var finishedStep = await finished;

            if (finishedStep.Status == StepStatus.Succeeded)
            {
                completed.Add(finishedStep.Id);
            }
        }

        return new DeploymentRunResult(_steps.Values.ToList());
    }

    private async Task<DeploymentStep> ExecuteAsync(
        DeploymentStep step,
        SemaphoreSlim throttle,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                step.Status = StepStatus.Cancelled;
                step.Error = "Cancelled before the step started.";
                return step;
            }

            step.Status = StepStatus.Running;
            Report(progress, step);

            await step.ExecuteAsync(cancellationToken);
            step.Status = StepStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            step.Status = StepStatus.Cancelled;
            step.Error = "Cancelled.";
        }
        catch (Exception ex)
        {
            step.Status = StepStatus.Failed;
            step.Error = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            step.Duration = stopwatch.Elapsed;
            throttle.Release();
            Report(progress, step);
        }

        return step;
    }

    private static void Report(IProgress<DeploymentProgress>? progress, DeploymentStep step) =>
        progress?.Report(new DeploymentProgress(step.Id, step.DisplayName, step.Status, step.Error ?? step.Note, step.Duration));
}
