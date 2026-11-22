using System.Text.Json;
using Durable;

namespace BandGDurable;

/// <summary>
/// The long-running workflow: plan, research, write, wait at a human gate, then brief.
/// </summary>
/// <remarks>
/// The phases are the same shape the baseline's Magentic workflow has — the pre-gate work runs, the
/// gate stops the run, and the post-gate work runs once a human answers — with two differences that
/// the Durable layer makes possible:
/// <list type="number">
/// <item>
/// The gate is a child task that commits <see cref="TaskStatus.Waiting"/>, not a call that stays
/// open. The workflow's own checkpoint names the child, so the process can exit at the gate and a
/// later process re-enters both from the store.
/// </item>
/// <item>
/// Every model step is memoized by its position in the run. A process that ends after step three of
/// six and a process that ends after step five both resume without re-calling the model for a step
/// whose answer the store already holds.
/// </item>
/// </list>
/// </remarks>
public static class BriefWorkflow
{
    /// <summary>The workflow's definition.</summary>
    /// <param name="harness">The harness whose conversations the model turns run in.</param>
    /// <param name="gate">The gate definition the workflow creates as a child task.</param>
    /// <param name="model">
    /// The gateway the steps call through. Its memo is keyed by the step's position, so the same
    /// gateway serves a first process and a resumed one.
    /// </param>
    /// <param name="scene">A directory the run records its own artefacts into, outside the store.</param>
    /// <param name="control">
    /// The seam a fault-injection run uses. <see langword="null"/> in a normal run.
    /// </param>
    public static DurableTaskDefinition Define(
        DurableStore store,
        DurableHarness harness,
        DurableTaskDefinition gate,
        ModelGateway model,
        string scene,
        Control control)
    {
        return DurableTask.DefinePhases<BriefInput, BriefCheckpoint>(
            Brief.TaskName,
            () => new BriefCheckpoint { Phase = "plan" },
            new Dictionary<string, Func<BriefInput, BriefCheckpoint, TaskRuntime, Task<TaskCommit>>>
            {
                // Step 1: the plan. This is the v1's pre-gate model call, and the one a resume has
                // to be able to skip.
                ["plan"] = async (input, checkpoint, runtime) =>
                {
                    var (answer, fromMemo) = await model.CallAsync(runtime, "plan", PlanPrompt(input.Task));
                    control.BeforeStep("plan", fromMemo);
                    return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(new BriefCheckpoint
                    {
                        Phase = "research",
                        Plan = answer,
                    })));
                },

                // Step 2: the researcher's pass. Memoized the same way, and reachable only after the
                // plan's checkpoint, so a restart never re-plans.
                ["research"] = async (input, checkpoint, runtime) =>
                {
                    var (answer, fromMemo) = await model.CallAsync(runtime, "research", ResearchPrompt(checkpoint.Plan ?? string.Empty));
                    control.BeforeStep("research", fromMemo);
                    File.WriteAllText(Path.Combine(scene, "research.md"), answer);
                    return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(new BriefCheckpoint
                    {
                        Phase = "write",
                        Plan = checkpoint.Plan,
                        Research = answer,
                    })));
                },

                // Step 3: the writer's draft, the last model call before the gate.
                ["write"] = async (input, checkpoint, runtime) =>
                {
                    var (answer, fromMemo) = await model.CallAsync(runtime, "write", WritePrompt(checkpoint.Research ?? string.Empty));
                    control.BeforeStep("write", fromMemo);
                    File.WriteAllText(Path.Combine(scene, "draft-v1.md"), answer);
                    return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(new BriefCheckpoint
                    {
                        Phase = "seek-approval",
                        Plan = checkpoint.Plan,
                        Research = checkpoint.Research,
                        Draft = answer,
                    })));
                },

                // The gate. The child is created in the same transaction as the checkpoint that
                // records it, so a crash leaves either both or neither, and the wait is a committed
                // fact rather than an open call.
                //
                // Re-entry takes the existing child id from the checkpoint instead of creating a
                // second gate: a resume after the child exists must not fork the approval into two.
                ["seek-approval"] = (input, checkpoint, runtime) =>
                {
                    if (checkpoint.GateTask is { } existing)
                    {
                        control.AtGate(existing, resumed: true);
                        return runtime.CommitAsync(() => TaskCommit.Waiting(
                            DurableJson.ToElement(new BriefCheckpoint
                            {
                                Phase = "post-approval",
                                Plan = checkpoint.Plan,
                                Research = checkpoint.Research,
                                Draft = checkpoint.Draft,
                                GateTask = existing,
                            }),
                            [existing]));
                    }

                    return runtime.CommitAsync(async tx =>
                    {
                        var child = runtime.CreateChildTask(tx, gate, DurableJson.ToElement(new GateInput
                        {
                            Owner = runtime.Task.Id,
                            WindowSeconds = input.GateSeconds,
                        }));

                        control.AtGate(child.Id, resumed: false);

                        return await Task.FromResult(TaskCommit.Waiting(
                            DurableJson.ToElement(new BriefCheckpoint
                            {
                                Phase = "post-approval",
                                Plan = checkpoint.Plan,
                                Research = checkpoint.Research,
                                Draft = checkpoint.Draft,
                                GateTask = child.Id,
                            }),
                            [child.Id]));
                    });
                },

                // After the gate. The decision is read from the child's terminal outcome, and the
                // decision document becomes the record of what was approved. A rejection ends the
                // run here; it does not fall through to the brief.
                ["post-approval"] = async (input, checkpoint, runtime) =>
                {
                    var outcomes = checkpoint.GateTask is { } gateId
                        ? await runtime.OutcomesAsync([gateId])
                        : [];
                    var verdict = outcomes.FirstOrDefault();
                    var gateOutcome = verdict?.Result is { } result
                        ? result.Deserialize<GateOutcome>(DurableJson.Options)
                        : null;

                    if (gateOutcome is null || gateOutcome.Verdict != "approved")
                    {
                        var reason = gateOutcome?.Verdict ?? "the gate produced no verdict";
                        return await runtime.CommitAsync(() => TaskCommit.Failed(
                            $"the plan was not approved ({reason}): {(gateOutcome?.Decision?.Comment ?? "no comment")}"));
                    }

                    // The post-gate step is memoized too. Re-entering here after the brief was
                    // written does not call the model again.
                    var (answer, fromMemo) = await model.CallAsync(
                        runtime,
                        "brief",
                        BriefPrompt(checkpoint.Draft ?? string.Empty, gateOutcome.Decision?.Comment ?? string.Empty));
                    control.BeforeStep("brief", fromMemo);
                    File.WriteAllText(Path.Combine(scene, "brief.md"), answer);

                    return await runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(new BriefOutcome
                    {
                        Plan = checkpoint.Plan,
                        Research = checkpoint.Research,
                        Draft = checkpoint.Draft,
                        Brief = answer,
                        Verdict = gateOutcome.Verdict,
                        Comment = gateOutcome.Decision?.Comment,
                    })));
                },
            },

            // Aborting a waiting parent aborts its children first: that cascade is in the layer, and
            // this handler is what the parent records once its children are settled. It reads each
            // child's record here, inside the handler, so the capture shows the ordering rather than
            // a pair of terminal states that any ordering would produce.
            async (input, checkpoint, runtime) =>
            {
                var observed = new List<AbortObservation>();
                // The store, not the runtime: TaskRuntime exposes no read path, which the README
                // records as a limitation. The children are the tasks this one owns.
                foreach (var child in store.Tasks.Where(task => task.OwnerTaskId == runtime.Task.Id))
                {
                    observed.Add(new AbortObservation
                    {
                        TaskId = child.Id,
                        Status = child.Status.ToString(),
                        Verdict = child.Outcome?.Deserialize<TaskOutcome>(DurableJson.Options)?.Result
                            ?.Deserialize<GateOutcome>(DurableJson.Options)?.Verdict,
                    });
                }

                return await runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(new BriefOutcome
                {
                    Plan = checkpoint.Plan,
                    Research = checkpoint.Research,
                    Draft = checkpoint.Draft,
                    Verdict = "aborted",
                    Comment = "the run was aborted",
                    Observed = observed,
                })));
            });
    }

    /// <summary>The prompt the plan step sends.</summary>
    public static string PlanPrompt(string task) =>
        $"Plan the work for this brief in one sentence. Task: {task}";

    /// <summary>The prompt the research step sends.</summary>
    public static string ResearchPrompt(string plan) =>
        $"In one sentence, name the evidence the research team should gather. Plan: {plan}";

    /// <summary>The prompt the write step sends.</summary>
    public static string WritePrompt(string research) =>
        $"In one sentence, state the recommendation the brief should make. Evidence: {research}";

    /// <summary>The prompt the post-gate brief step sends.</summary>
    public static string BriefPrompt(string draft, string comment) =>
        $"In one sentence, note what changed after review. Draft: {draft} Reviewer said: {comment}";
}

/// <summary>What a finished workflow produced, and how it ended.</summary>
public sealed class BriefOutcome
{
    /// <summary>The plan the run committed to.</summary>
    public string? Plan { get; set; }

    /// <summary>The evidence the researcher named.</summary>
    public string? Research { get; set; }

    /// <summary>The draft the writer produced before the gate.</summary>
    public string? Draft { get; set; }

    /// <summary>The brief written after approval.</summary>
    public string? Brief { get; set; }

    /// <summary>How the run ended: <c>approved</c>, <c>rejected</c>, <c>timed-out</c>, or <c>aborted</c>.</summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>The reviewer's comment, when there was one.</summary>
    public string? Comment { get; set; }

    /// <summary>
    /// What the abort handler saw of its own children, at the moment it ran.
    /// </summary>
    /// <remarks>
    /// This is the ordering evidence for the cascade, and it is only recorded on the abort path.
    /// "Both tasks are terminal" is equally consistent with the gate ending for any reason; "the
    /// child was already terminal when the parent's handler ran" is not. <see langword="null"/> on
    /// every path that is not an abort.
    /// </remarks>
    public List<AbortObservation>? Observed { get; set; }
}

/// <summary>What one child looked like from inside a parent's abort handler.</summary>
public sealed class AbortObservation
{
    /// <summary>The child task's id.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>The child's status at the moment the parent's abort handler read it.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The child's verdict when it had one, or <see langword="null"/> when it had none yet.</summary>
    public string? Verdict { get; set; }
}

/// <summary>
/// The fault-injection seam. A normal run gets the inert control; a fault run gets the one the
/// fault asks for.
/// </summary>
/// <remarks>
/// The seam is a scenario concern, so it lives in the scenario: the layer has no notion of a run
/// that stops itself. Each hook runs inside the phase, between the effect and the commit that
/// records it, which is the only place a fault can stall a run without corrupting it.
/// </remarks>
public class Control
{
    /// <summary>Runs after a model step's answer is known, before the step's checkpoint commits.</summary>
    public virtual void BeforeStep(string step, bool fromMemo)
    {
    }

    /// <summary>Runs once the gate child exists, before the parent's waiting commit.</summary>
    public virtual void AtGate(string gateTaskId, bool resumed)
    {
    }
}

/// <summary>An inert control: a normal run does nothing at the seam.</summary>
public sealed class NoControl : Control
{
    /// <summary>The shared instance.</summary>
    public static readonly NoControl Instance = new();
}

/// <summary>Leaves the process at a named point, the way a killed process would.</summary>
/// <remarks>
/// <see cref="Environment.Exit"/> is deliberate rather than <c>throw</c>: an exception is caught by
/// the runner and committed as a failure, which would make the store say the run failed. A process
/// that is gone commits nothing, so the checkpoint stays where the last commit left it and the next
/// process is the only one that can move it. SIGKILL would be the same thing with a lower signal
/// quality — nothing to catch, but nothing to print either.
/// </remarks>
public sealed class ExitAtStep : Control
{
    private readonly string _step;

    /// <summary>Leaves before <paramref name="step"/>'s checkpoint commits, unless the step was a memo hit.</summary>
    /// <param name="step">The step to leave at.</param>
    public ExitAtStep(string step) => _step = step;

    /// <inheritdoc/>
    public override void BeforeStep(string step, bool fromMemo)
    {
        if (step != _step)
        {
            return;
        }

        if (fromMemo)
        {
            // A memo hit means the step committed in an earlier process. There is nothing in flight
            // to interrupt, so leaving here would prove nothing about the fault.
            return;
        }

        Console.WriteLine($"fault:      leaving the process before {step} commits its checkpoint");
        Console.Out.Flush();
        Environment.Exit(42);
    }
}
