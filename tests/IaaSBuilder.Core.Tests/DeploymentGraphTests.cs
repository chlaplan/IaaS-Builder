using IaaSBuilder.Core.Deployment;

namespace IaaSBuilder.Core.Tests;

public class DeploymentGraphTests
{
    private static DeploymentStep Step(
        string id,
        IReadOnlyList<string>? dependsOn = null,
        Func<CancellationToken, Task>? body = null) =>
        new(id, id, dependsOn ?? [], body ?? (_ => Task.CompletedTask));

    [Fact]
    public void Rejects_dependencies_on_steps_that_do_not_exist()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DeploymentGraph([Step("b", ["a"])]));

        Assert.Contains("depends on 'a'", ex.Message);
    }

    [Fact]
    public void Detects_circular_dependencies()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DeploymentGraph([Step("a", ["c"]), Step("b", ["a"]), Step("c", ["b"])]));

        Assert.Contains("Circular dependency", ex.Message);
    }

    [Fact]
    public void Rejects_duplicate_step_ids() =>
        Assert.Throws<ArgumentException>(() => new DeploymentGraph([Step("a"), Step("a")]));

    [Fact]
    public void Groups_independent_steps_into_the_same_wave()
    {
        var graph = new DeploymentGraph([
            Step("rg"),
            Step("network", ["rg"]),
            Step("artifacts", ["rg"]),
            Step("dc", ["network", "artifacts"]),
            Step("avd", ["dc"])
        ]);

        var waves = graph.GetExecutionWaves();

        Assert.Equal(4, waves.Count);
        Assert.Equal(["rg"], waves[0]);
        Assert.Equal(["artifacts", "network"], waves[1].OrderBy(x => x));
        Assert.Equal(["dc"], waves[2]);
        Assert.Equal(["avd"], waves[3]);
    }

    [Fact]
    public async Task Runs_steps_in_dependency_order()
    {
        var order = new List<string>();
        var gate = new Lock();

        void Record(string id)
        {
            lock (gate) order.Add(id);
        }

        var graph = new DeploymentGraph([
            Step("rg", [], async ct => { await Task.Delay(10, ct); Record("rg"); }),
            Step("network", ["rg"], async ct => { await Task.Delay(10, ct); Record("network"); }),
            Step("dc", ["network"], async ct => { await Task.Delay(10, ct); Record("dc"); }),
            Step("avd", ["dc"], async ct => { await Task.Delay(10, ct); Record("avd"); })
        ]);

        var result = await graph.RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(["rg", "network", "dc", "avd"], order);
    }

    [Fact]
    public async Task Runs_independent_steps_concurrently()
    {
        var running = 0;
        var peak = 0;
        var gate = new Lock();

        async Task Body(CancellationToken ct)
        {
            lock (gate)
            {
                running++;
                peak = Math.Max(peak, running);
            }

            await Task.Delay(100, ct);

            lock (gate) running--;
        }

        var graph = new DeploymentGraph([
            Step("a", [], Body),
            Step("b", [], Body),
            Step("c", [], Body)
        ]);

        await graph.RunAsync();

        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task Skips_dependents_when_a_prerequisite_fails()
    {
        var workstationRan = false;

        var graph = new DeploymentGraph([
            Step("dc", [], _ => throw new InvalidOperationException("DC blew up")),
            Step("workstation", ["dc"], _ =>
            {
                workstationRan = true;
                return Task.CompletedTask;
            })
        ]);

        var result = await graph.RunAsync();

        Assert.False(result.Succeeded);
        Assert.False(workstationRan);

        var workstation = result.Steps.Single(s => s.Id == "workstation");
        Assert.Equal(StepStatus.Skipped, workstation.Status);
        Assert.Contains("did not succeed", workstation.Error);
    }

    [Fact]
    public async Task Skipping_cascades_through_the_whole_chain()
    {
        var graph = new DeploymentGraph([
            Step("dc", [], _ => throw new InvalidOperationException("boom")),
            Step("sql", ["dc"]),
            Step("sharepoint", ["sql"])
        ]);

        var result = await graph.RunAsync();

        Assert.All(
            result.Steps.Where(s => s.Id != "dc"),
            s => Assert.Equal(StepStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task An_unrelated_failure_does_not_block_independent_work()
    {
        var independentRan = false;

        var graph = new DeploymentGraph([
            Step("failing", [], _ => throw new InvalidOperationException("boom")),
            Step("independent", [], _ =>
            {
                independentRan = true;
                return Task.CompletedTask;
            })
        ]);

        var result = await graph.RunAsync();

        Assert.True(independentRan);
        Assert.Equal(StepStatus.Succeeded, result.Steps.Single(s => s.Id == "independent").Status);
        Assert.Equal(StepStatus.Failed, result.Steps.Single(s => s.Id == "failing").Status);
    }

    [Fact]
    public async Task Reports_progress_for_every_state_transition()
    {
        var reports = new List<DeploymentProgress>();
        var progress = new Progress<DeploymentProgress>(p =>
        {
            lock (reports) reports.Add(p);
        });

        var graph = new DeploymentGraph([Step("a")]);
        await graph.RunAsync(progress);

        // Progress<T> marshals through the synchronization context, so allow it to drain.
        await Task.Delay(100);

        lock (reports)
        {
            Assert.Contains(reports, r => r.Status == StepStatus.Running);
            Assert.Contains(reports, r => r.Status == StepStatus.Succeeded);
        }
    }

    [Fact]
    public async Task Records_the_duration_of_each_step()
    {
        var graph = new DeploymentGraph([Step("a", [], ct => Task.Delay(50, ct))]);
        var result = await graph.RunAsync();

        var step = result.Steps.Single();
        Assert.NotNull(step.Duration);
        Assert.True(step.Duration >= TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public async Task Honours_the_parallelism_limit()
    {
        var running = 0;
        var peak = 0;
        var gate = new Lock();

        async Task Body(CancellationToken ct)
        {
            lock (gate)
            {
                running++;
                peak = Math.Max(peak, running);
            }

            await Task.Delay(50, ct);

            lock (gate) running--;
        }

        var graph = new DeploymentGraph(
            Enumerable.Range(0, 8).Select(i => Step($"s{i}", [], Body)))
        {
            MaxDegreeOfParallelism = 2
        };

        await graph.RunAsync();

        Assert.True(peak <= 2, $"Expected at most 2 concurrent steps but saw {peak}.");
    }
}
