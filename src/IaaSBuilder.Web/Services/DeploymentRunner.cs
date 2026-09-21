using IaaSBuilder.Core.Azure;
using IaaSBuilder.Core.Deployment;
using IaaSBuilder.Core.Models;
using IaaSBuilder.Core.Templates;

namespace IaaSBuilder.Web.Services;

public enum RunState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record RunLogEntry(DateTimeOffset Timestamp, string Message, bool IsError = false);

/// <summary>
/// Runs a deployment off the request thread and publishes progress to the UI.
/// </summary>
/// <remarks>
/// The legacy click handler ran every deployment inline on the WPF UI thread, with
/// <c>Start-Sleep -Seconds 660</c> in the middle of it, so the window sat at "Not Responding"
/// for the entire build. Here the run is a background task, progress arrives over SignalR as
/// each step changes state, and the operator can cancel.
/// </remarks>
public sealed class DeploymentRunner
{
    private readonly TemplateResolver _templates;
    private readonly AzureSession _session;
    private CancellationTokenSource? _cts;

    public DeploymentRunner(TemplateResolver templates, AzureSession session)
    {
        _templates = templates;
        _session = session;
    }

    public RunState State { get; private set; } = RunState.Idle;
    public bool WasWhatIf { get; private set; }
    public IReadOnlyList<DeploymentStep> Steps { get; private set; } = [];
    public List<RunLogEntry> Log { get; } = [];
    public string? FatalError { get; private set; }
    public DateTimeOffset? StartedUtc { get; private set; }
    public DateTimeOffset? FinishedUtc { get; private set; }

    public bool IsRunning => State == RunState.Running;

    public event Action? Changed;

    public int CompletedCount =>
        Steps.Count(s => s.Status is StepStatus.Succeeded or StepStatus.Failed
            or StepStatus.Skipped or StepStatus.Cancelled);

    public int PercentComplete =>
        Steps.Count == 0 ? 0 : (int)Math.Round(100.0 * CompletedCount / Steps.Count);

    public void Cancel() => _cts?.Cancel();

    public async Task StartAsync(DeploymentPlan plan, string adminPassword, bool whatIf)
    {
        if (IsRunning)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        State = RunState.Running;
        WasWhatIf = whatIf;
        FatalError = null;
        StartedUtc = DateTimeOffset.UtcNow;
        FinishedUtc = null;
        Steps = [];
        Log.Clear();
        Append(whatIf ? "Starting validation pass (what-if)." : "Starting deployment.");
        Changed?.Invoke();

        // The password is copied into a disposable holder that is cleared when the run ends;
        // it is never part of the plan and so can never reach the saved plan file.
        var secrets = new DeploymentSecrets(adminPassword);

        try
        {
            if (!_session.IsSignedIn)
            {
                throw new InvalidOperationException(
                    "Sign in to Azure before deploying. Plans can be authored and validated offline, " +
                    "but a deployment needs a connection.");
            }

            var azure = new AzureDeploymentService(_session.Credential, plan.Azure.Cloud, _templates);
            var orchestrator = new DeploymentOrchestrator(azure, _templates);

            var graph = orchestrator.BuildGraph(plan, secrets, new OrchestrationOptions { WhatIf = whatIf });
            Steps = graph.Steps.ToList();
            Append($"{Steps.Count} steps planned.");
            Changed?.Invoke();

            var progress = new Progress<DeploymentProgress>(OnProgress);

            var result = await Task.Run(
                () => graph.RunAsync(progress, _cts.Token),
                _cts.Token);

            State = result.Succeeded ? RunState.Succeeded : RunState.Failed;

            if (result.Succeeded)
            {
                Append(whatIf ? "Validation passed." : "Deployment finished successfully.");
            }
            else
            {
                foreach (var failed in result.Failed)
                {
                    Append($"{failed.DisplayName}: {failed.Error}", isError: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            State = RunState.Cancelled;
            Append("Cancelled by the operator.", isError: true);
        }
        catch (Exception ex)
        {
            State = RunState.Failed;
            FatalError = ex.Message;
            Append(ex.Message, isError: true);
        }
        finally
        {
            secrets.Dispose();
            FinishedUtc = DateTimeOffset.UtcNow;
            Changed?.Invoke();
        }
    }

    private void OnProgress(DeploymentProgress progress)
    {
        var message = progress.Status switch
        {
            StepStatus.Running => $"{progress.DisplayName}: started.",
            StepStatus.Succeeded => $"{progress.DisplayName}: succeeded in {Format(progress.Duration)}.",
            StepStatus.Failed => $"{progress.DisplayName}: FAILED - {progress.Message}",
            StepStatus.Skipped => $"{progress.DisplayName}: skipped - {progress.Message}",
            StepStatus.Cancelled => $"{progress.DisplayName}: cancelled.",
            _ => null
        };

        if (message is not null)
        {
            Append(message, progress.Status is StepStatus.Failed);
        }

        Changed?.Invoke();
    }

    private void Append(string message, bool isError = false)
    {
        lock (Log)
        {
            Log.Add(new RunLogEntry(DateTimeOffset.UtcNow, message, isError));
        }
    }

    private static string Format(TimeSpan? duration) =>
        duration is null
            ? "-"
            : duration.Value.TotalMinutes >= 1
                ? $"{duration.Value.TotalMinutes:F1} min"
                : $"{duration.Value.TotalSeconds:F0}s";
}
