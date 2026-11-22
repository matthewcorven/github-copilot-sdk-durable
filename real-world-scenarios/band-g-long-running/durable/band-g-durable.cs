// Band G Durable variant (issue #177): the long-running workflow with a durable approval gate.
//
//   band-g-durable --run <dir> start    [--gate-seconds N] [--fault <step>]
//   band-g-durable --run <dir> resume
//   band-g-durable --run <dir> wait     [--timeout N]
//   band-g-durable --run <dir> approve  [--comment <text>] [--as <label>]
//   band-g-durable --run <dir> reject   [--comment <text>] [--as <label>]
//   band-g-durable --run <dir> inspect
//   band-g-durable --run <dir> abort
//
// Run dir: the directory the run records into. It holds the store (`store.jsonl`), the scene the
// run writes its own files into (`scene/`), and a `run.json` the driver reads back so a later
// process does not need a task id on its command line. It must already exist; the driver never
// creates one and never deletes one. run.sh and resume.sh own that.
//
// Every verb opens the store, does its own work, and exits. A run therefore spans processes the way
// a real one does, and the only thing shared between them is the store.

#:project ../../../lib/Copilot.Durable.csproj
#:include Workflow.cs
#:include Model.cs
#:include Gate.cs
#:include BriefWorkflow.cs
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001;NU1903;CS1591;IL2026;IL3050;CA2266

using System.Text.Json;
using BandGDurable;
using Durable;


try
{
    return await MainAsync(args).ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"band-g-durable: {ex}");
    return 70;
}

static async Task<int> MainAsync(string[] args)
{
var options = Cli.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(Cli.Usage);
    return 2;
}

var runDirectory = options.Run;
if (!Directory.Exists(runDirectory))
{
    Console.Error.WriteLine($"band-g-durable: no run directory at '{runDirectory}'. Create one, or run run.sh first.");
    return 2;
}

var storePath = Path.Combine(runDirectory, "store.jsonl");
if (options.Verb != "start" && !File.Exists(storePath))
{
    Console.Error.WriteLine($"band-g-durable: no store at '{storePath}'. Run run.sh first and keep its directory.");
    return 2;
}

var scene = Path.Combine(runDirectory, "scene");
Directory.CreateDirectory(scene);

using var store = DurableStore.Create(new DurableStoreOptions
{
    Path = storePath,
    Format = DurableStoreFormat.Jsonl,
});

// Cancelling this leaves the parent Waiting at its checkpoint, which is the state a process that
// stopped at the gate would leave: nothing in flight, the wait on record.
using var stopAtGate = new CancellationTokenSource();
var runFile = Path.Combine(runDirectory, "run.json");
var prior = File.Exists(runFile)
    ? JsonSerializer.Deserialize<RunFile>(File.ReadAllText(runFile), DurableJson.Options)
    : null;

if (options.Verb != "start" && prior is null)
{
    Console.Error.WriteLine($"band-g-durable: '{runDirectory}' holds no run file; nothing to {options.Verb}.");
    return 2;
}

if (options.Verb == "start" && prior is not null)
{
    Console.Error.WriteLine($"band-g-durable: '{runDirectory}' already holds run {prior.TaskId}; use resume, not start.");
    return 2;
}

// The gate reads its decision document through the store, so the definition takes the store.
var gate = ApprovalGate.Define(store);

await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

// One gateway and one definition per process, built once, closing over this process's control. The
// runner resolves a task by name through the registry, so the definition installed below is the one
// that runs — including after a resume, where the definition a previous process installed is gone.
var gateway = new ModelGateway(harness, scene);
if (options.Deterministic)
{
    gateway.DeterministicModel = DeterministicAnswer;
}

if (options.NoMemo)
{
    gateway.BypassMemo = true;
}

var control = options.Fault is null ? NoControl.Instance : (Control)new ExitAtStep(options.Fault);
var workflow = BriefWorkflow.Define(store, harness, gate, gateway, scene, control);
harness.Registry.Install(new Extension("band-g", tasks: [workflow, gate]));


async Task<int> StartAsync()
{
    // The brief id is the task's own id, so the input does not name it: an input written before the
    // record exists cannot refer to it, and a field that has to be corrected afterwards is a field
    // that can be read stale. A phase reads `runtime.Task.Id` instead.
    var input = new BriefInput
    {
        RunDirectory = runDirectory,
        Task = Brief.Task,
        GateSeconds = options.GateSeconds,
    };

    // Created through the store rather than CreateTaskAsync: creating and scheduling are the same
    // call on the harness, and a phase must not run before the gateway knows which run it is
    // counting for. The task is Pending until RunTaskAsync below, which is the state a process that
    // died between the create and the run would also leave.
    var record = await store.CommitAsync(tx => Task.FromResult(
        tx.CreateTask(workflow, DurableJson.ToElement(input)))).ConfigureAwait(false);

    gateway.Owner = record.Id;
    File.WriteAllText(runFile, JsonSerializer.Serialize(new RunFile
    {
        TaskId = record.Id,
        RunDirectory = runDirectory,
        GateSeconds = options.GateSeconds,
        StartedAt = DateTimeOffset.UtcNow,
    }, DurableJson.Options));

    if (options.Deterministic)
    {
        Console.WriteLine($"model:      deterministic (the scenario supplies each answer after its turn)");
    }

    if (options.NoMemo)
    {
        Console.WriteLine($"memo:       bypassed (the control run: every step attempt calls the model)");
    }

    Console.WriteLine($"task:       {record.Id} (created, gate window {options.GateSeconds}s)");
    if (options.Fault is { } fault)
    {
        Console.WriteLine($"fault:      armed at step '{fault}'");
    }

    var run = harness.RunTaskAsync(record.Id, stopAtGate.Token);
    await WatchAsync(run, record.Id).ConfigureAwait(false);
    return await FinishAsync(run, record.Id).ConfigureAwait(false);
}

async Task<int> ResumeAsync()
{
    var briefTask = store.Task(prior!.TaskId)!;
    gateway.Owner = briefTask.Id;

    Console.WriteLine($"task:       {briefTask.Id} (resuming)");
    Console.WriteLine($"status:     {briefTask.Status}, restarts before this process: {briefTask.Restarts}");
    Console.WriteLine($"gate:       {GateStatus(briefTask.Id)}");

    // ResumeAsync re-enters every task still in flight: the workflow if it was running, and the
    // gate child whether it was pending, running, or waiting. The gate is re-entered from its own
    // checkpoint, so a deadline recorded before the restart is the one it still honours.
    //
    // The resumed run is driven only to the gate. A resume that arrives before a decision exists
    // must stop there again, because the decision belongs to a later process — that is the whole
    // shape of the demonstration.
    return await ResumeToGateAsync(briefTask.Id, "resumed").ConfigureAwait(false);
}

// Re-enters everything in flight, says how many there were, and drives the run to the gate.
//
// ResumeAsync is awaited on its own task so the gate stop can cancel it: the operation re-enters the
// parent and the gate child together, and the parent is the one that stops at the gate.
async Task<int> ResumeToGateAsync(string taskId, string resumedLabel)
{
    var resuming = harness.ResumeAsync(stopAtGate.Token);

    // The re-entry count is only known once the call returns, so it is printed by the caller on the
    // path that gets past the gate. Stopping at the gate cancels this task, and a cancelled wait
    // says nothing about how many tasks were re-entered.
    var finished = await WatchTaskAsync(resuming, taskId).ConfigureAwait(false);
    if (finished)
    {
        var outcomes = await resuming.ConfigureAwait(false);
        Console.WriteLine($"{resumedLabel}:    {outcomes.Count} task(s) re-entered ({string.Join(", ", outcomes.Select(outcome => outcome.Status))})");
    }

    return await FinishAsync(
        resuming.ContinueWith(completed => new TaskOutcome(completed.IsCompletedSuccessfully ? "completed" : "failed"), TaskScheduler.Default),
        taskId,
        resumedLabel).ConfigureAwait(false);
}

// Watches a resume until the run reaches the gate or the resume returns, whichever is first.
async Task<bool> WatchTaskAsync(Task<IReadOnlyList<TaskOutcome>> resuming, string taskId)
{
    while (!resuming.IsCompleted)
    {
        var status = store.Task(taskId)?.Status;
        if (status is Durable.TaskStatus.Waiting or Durable.TaskStatus.Terminal or null)
        {
            break;
        }

        if (store.Tasks.Any(task => task.OwnerTaskId == taskId && task.Name == Brief.GateTaskName))
        {
            break;
        }

        await Task.Delay(25).ConfigureAwait(false);
    }

    await Task.Delay(100).ConfigureAwait(false);
    return resuming.IsCompleted;
}

async Task<int> WaitAsync()
{
    var briefTask = store.Task(prior!.TaskId)!;
    gateway.Owner = briefTask.Id;

    if (briefTask.Status == Durable.TaskStatus.Terminal)
    {
        return Inspect();
    }

    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
    try
    {
        var outcome = await harness.WaitForTaskAsync(briefTask.Id, budget.Token).ConfigureAwait(false);
        return Report(outcome);
    }
    catch (OperationCanceledException)
    {
        var now = store.Task(briefTask.Id)!;
        Console.WriteLine($"wait:       no verdict within {options.TimeoutSeconds}s; the run is still {now.Status}");
        Console.WriteLine($"gate:       {GateStatus(briefTask.Id)}");
        return 3;
    }
}

async Task<int> DecideAsync(bool approved)
{
    var briefTask = store.Task(prior!.TaskId)!;
    var label = options.As ?? "resume";
    var comment = options.Comment ?? (approved
        ? "approved from the resume process"
        : "rejected from the resume process");

    var result = approved
        ? await GateCommands.ApproveAsync(store, prior.TaskId, prior.TaskId, label, comment).ConfigureAwait(false)
        : await GateCommands.RejectAsync(store, prior.TaskId, prior.TaskId, label, comment).ConfigureAwait(false);

    Console.WriteLine($"decision:   {result.Note}");
    Console.WriteLine($"on file:    {(result.Decision.Approved ? "approved" : "rejected")} by {result.Decision.Recorder} at {result.Decision.At:O}");
    Console.WriteLine($"run:        {briefTask.Status}");

    // Recording and driving are separate verbs on purpose. A decision is a value the store holds;
    // whether the gate has read it yet is a different fact, and a caller that wants both asks for
    // both. Fusing them would make the second approval of a live gate impossible to observe — the
    // first one would already have driven the run to completion, so the second would be refused for
    // being late rather than for naming a decision that is already on file.
    return result.Recorded ? 0 : 4;
}

int Inspect()
{
    var briefTask = store.Task(prior!.TaskId)!;
    Console.WriteLine($"task:       {briefTask.Id} {briefTask.Status} (restarts {briefTask.Restarts})");
    Console.WriteLine($"gate:       {GateStatus(briefTask.Id)}");
    var decision = Brief.ReadDecision(store, briefTask.Id);
    Console.WriteLine($"decision:   {(decision is null ? "(none)" : $"{(decision.Approved ? "approved" : "rejected")} by {decision.Recorder}: {decision.Comment}")}");
    Console.WriteLine($"model:      {ModelLine(briefTask.Id)}");
    if (briefTask.Outcome is { } outcome)
    {
        var value = outcome.Deserialize<TaskOutcome>(DurableJson.Options)!;
        Console.WriteLine($"outcome:    {value.Status}{(value.Error is null ? string.Empty : $" — {value.Error}")}");
        if (value.Result is { ValueKind: JsonValueKind.Object } result
            && result.TryGetProperty("verdict", out var verdict))
        {
            Console.WriteLine($"verdict:    {verdict.GetString()}");
        }

        // The cascade's ordering, as the parent's abort handler saw it. Printed only when it was
        // recorded, so a normal run's output does not grow a line that means nothing.
        if (value.Result is { ValueKind: JsonValueKind.Object } detail
            && detail.TryGetProperty("observed", out var observed)
            && observed.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in observed.EnumerateArray())
            {
                var id = entry.TryGetProperty("taskId", out var taskId) ? taskId.GetString() : "?";
                var status = entry.TryGetProperty("status", out var state) ? state.GetString() : "?";
                var sawVerdict = entry.TryGetProperty("verdict", out var childVerdict) ? childVerdict.GetString() : null;
                Console.WriteLine($"observed:   {id} was {status}{(sawVerdict is null ? " with no verdict yet" : $" ({sawVerdict})")} when the abort handler ran");
            }
        }
    }

    return 0;
}

async Task<int> AbortAsync()
{
    var briefTask = store.Task(prior!.TaskId)!;
    Console.WriteLine($"abort:      {briefTask.Id} and its children");
    await harness.AbortTaskAsync(briefTask.Id).ConfigureAwait(false);
    return Inspect();
}

string GateStatus(string owner)
{
    var gates = store.Tasks.Where(task => task.OwnerTaskId == owner && task.Name == Brief.GateTaskName).ToList();
    if (gates.Count == 0)
    {
        return "(not created)";
    }

    return string.Join(", ", gates.Select(candidate =>
    {
        var verdict = candidate.Outcome?.Deserialize<TaskOutcome>(DurableJson.Options)?.Result?.Deserialize<GateOutcome>(DurableJson.Options)?.Verdict;
        return $"{candidate.Id} {candidate.Status}{(verdict is null ? string.Empty : $" ({verdict})")}";
    }));
}

string ModelLine(string owner)
{
    var state = store.Document(Brief.LedgerKind, owner);
    if (state?.Latest is not { } latest)
    {
        return "0 calls, 0 memo hits";
    }

    var ledger = latest.Deserialize<ModelLedger>(DurableJson.Options) ?? new ModelLedger();
    return $"{ledger.Calls.Count} calls [{string.Join(", ", ledger.Calls.Select(call => call.Step))}], {ledger.MemoHits} memo hits";
}

// Waits until the run has either reached the gate, finished, or failed, so the caller can decide
// what to print without holding the run open for the gate's whole window.
async Task WatchAsync(Task<TaskOutcome> run, string taskId)
{
    while (!run.IsCompleted)
    {
        var status = store.Task(taskId)?.Status;
        if (status is Durable.TaskStatus.Waiting or Durable.TaskStatus.Terminal or null)
        {
            break;
        }

        // The child exists before the parent's waiting commit does, so either signal is enough.
        if (store.Tasks.Any(task => task.OwnerTaskId == taskId && task.Name == Brief.GateTaskName))
        {
            break;
        }

        await Task.Delay(25).ConfigureAwait(false);
    }

    // Let the runner finish a commit it has already started before the caller tears anything down.
    await Task.Delay(100).ConfigureAwait(false);
}

// Stops at the gate, or reports the run's own end when it got past one.
async Task<int> FinishAsync(Task<TaskOutcome> run, string taskId, string? resumedLabel = null)
{
    if (store.Task(taskId)?.Status == Durable.TaskStatus.Waiting)
    {
        stopAtGate.Cancel();
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The wait was cancelled on purpose. The checkpoint stays where the last commit left it.
        }

        // A cancel that raced a completed gate must not be reported as "waiting".
        if (store.Task(taskId)?.Status == Durable.TaskStatus.Waiting)
        {
            Console.WriteLine($"outcome:    waiting at the gate (task {taskId} is Waiting on {GateStatus(taskId)})");
            Console.WriteLine($"gate:       {GateStatus(taskId)}");
            Console.WriteLine($"model:      {ModelLine(taskId)}");
            return 0;
        }

        return Inspect();
    }

    return Report(await run.ConfigureAwait(false));
}

int Report(TaskOutcome outcome)
{
    Console.WriteLine($"outcome:    {outcome.Status}");
    if (outcome.Error is { } error)
    {
        Console.WriteLine($"error:      {error}");
    }

    if (prior is not null)
    {
        Console.WriteLine($"model:      {ModelLine(prior.TaskId)}");
    }

    if (outcome.Result is { ValueKind: JsonValueKind.Object } result && result.TryGetProperty("verdict", out _))
    {
        var brief = result.Deserialize<BriefOutcome>(DurableJson.Options);
        if (brief is not null)
        {
            Console.WriteLine($"verdict:    {brief.Verdict}");
            Console.WriteLine($"draft:      {brief.Draft}");
            if (brief.Brief is not null)
            {
                Console.WriteLine($"brief:      {brief.Brief}");
            }
        }
    }

    return outcome.Status == "completed" ? 0 : 1;
}

/// <summary>
/// The answer the scenario gives a turn when no model is configured.
/// </summary>
/// <remarks>
/// This is the scenario's own function, not a branch inside the durable layer: the turn still went
/// to a runtime, the admission still committed, and the answer is recorded in the memo exactly as a
/// model's would be. What it buys is a run that behaves the same on a laptop with no endpoint and in
/// CI. <c>--live</c> drops it and the answer is whatever the configured model said.
/// </remarks>
static string DeterministicAnswer(string prompt)
{
    // Stable for a prompt, so the same step in two processes produces the same text and a
    // diff between the two runs is a real difference rather than sampling noise.
    var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(prompt));
    var tag = Convert.ToHexString(digest)[..8].ToLowerInvariant();
    var subject = prompt.Contains("Plan the work", StringComparison.Ordinal) ? "plan"
        : prompt.Contains("name the evidence", StringComparison.Ordinal) ? "research"
        : prompt.Contains("state the recommendation", StringComparison.Ordinal) ? "draft"
        : "brief";
    return $"[deterministic {subject} {tag}] The {subject} for the email-summarization beta, "
        + "stated in one sentence so the run has a stable answer to record.";
}

return options.Verb switch
{
    "start" => await StartAsync().ConfigureAwait(false),
    "resume" => await ResumeAsync().ConfigureAwait(false),
    "wait" => await WaitAsync().ConfigureAwait(false),
    "approve" => await DecideAsync(approved: true).ConfigureAwait(false),
    "reject" => await DecideAsync(approved: false).ConfigureAwait(false),
    "inspect" => Inspect(),
    "abort" => await AbortAsync().ConfigureAwait(false),
    _ => 2,
};
}

/// <summary>The run's own record, so a later process needs only the directory.</summary>
internal sealed class RunFile
{
    /// <summary>The workflow task's id.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>The directory the run records into.</summary>
    public string RunDirectory { get; set; } = string.Empty;

    /// <summary>The gate window the run was created with.</summary>
    public int GateSeconds { get; set; } = Brief.DefaultGateSeconds;

    /// <summary>When the run was created.</summary>
    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>The command line.</summary>
internal sealed class Cli
{
    /// <summary>The usage block.</summary>
    public const string Usage = """
        usage: band-g-durable --run <dir> <verb> [options]

          start    create the run and drive it to the gate (or to completion)
          resume   re-enter every task still in flight, with no fault armed
          wait     wait for the run to reach a terminal state
          approve  record an approval, unless one is already on file
          reject   record a rejection, unless one is already on file
          inspect  print the store's view of the run
          abort    abort the run, cascading to its children

        options:
          --run <dir>         the run directory (required)
          --gate-seconds N    the gate window, on start (default: 30)
          --fault <step>      leave the process before <step> commits, on start
          --timeout N         the wait budget in seconds (default: 60)
          --comment <text>    the reviewer's comment, on approve/reject
          --as <label>        who is asking, on approve/reject (default: resume)
          --deterministic     the scenario supplies each answer after its turn, so a run needs
                              no model endpoint (the turn itself is unchanged)
          --live              take each answer from the configured model. This is the default when
                              neither --live nor --deterministic is given, and naming it makes
                              that explicit. It needs a runtime that can answer; the recorded
                              runtime serves a fixed transcript, not a model
          --no-memo           the control run: ignore the memo, so every step attempt calls the
                              model. Used by evidence.sh to measure what the memo saves
        """;

    /// <summary>The verb.</summary>
    public string Verb { get; private init; } = string.Empty;

    /// <summary>The run directory.</summary>
    public string Run { get; private init; } = string.Empty;

    /// <summary>The gate window.</summary>
    public int GateSeconds { get; private init; } = Brief.DefaultGateSeconds;

    /// <summary>The step to leave the process at, when a fault is armed.</summary>
    public string? Fault { get; private init; }

    /// <summary>The wait budget.</summary>
    public int TimeoutSeconds { get; private init; } = 60;

    /// <summary>The reviewer's comment.</summary>
    public string? Comment { get; private init; }

    /// <summary>Who is asking.</summary>
    public string? As { get; private init; }

    /// <summary>Whether the scenario supplies each answer instead of taking the model's.</summary>
    public bool Deterministic { get; private init; }

    /// <summary>
    /// Whether the answer is whatever the configured model said.
    /// </summary>
    /// <remarks>
    /// The explicit counterpart of <see cref="Deterministic"/>, so a caller states which it means
    /// rather than relying on an absence. The two are mutually exclusive; naming both is a usage
    /// error rather than a precedence rule nobody remembers.
    /// </remarks>
    public bool Live { get; private init; }

    /// <summary>Whether the control run ignores the memo, so every step attempt calls the model.</summary>
    public bool NoMemo { get; private init; }

    /// <summary>Parses the command line, or returns <see langword="null"/> when it does not parse.</summary>
    public static Cli? Parse(string[] args)
    {
        var verbs = new[] { "start", "resume", "wait", "approve", "reject", "inspect", "abort" };
        string? verb = null;
        string? run = null;
        var gateSeconds = Brief.DefaultGateSeconds;
        string? fault = null;
        var timeout = 60;
        string? comment = null;
        string? asLabel = null;
        var deterministic = false;
        var live = false;
        var noMemo = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--run" when i + 1 < args.Length:
                    run = args[++i];
                    break;
                case "--gate-seconds" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out gateSeconds) || gateSeconds <= 0)
                    {
                        return null;
                    }

                    break;
                case "--fault" when i + 1 < args.Length:
                    fault = args[++i];
                    break;
                case "--timeout" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out timeout) || timeout <= 0)
                    {
                        return null;
                    }

                    break;
                case "--comment" when i + 1 < args.Length:
                    comment = args[++i];
                    break;
                case "--as" when i + 1 < args.Length:
                    asLabel = args[++i];
                    break;
                case "--deterministic":
                    deterministic = true;
                    break;
                case "--live":
                    live = true;
                    break;
                case "--no-memo":
                    noMemo = true;
                    break;
                default:
                    if (verb is null && verbs.Contains(args[i]))
                    {
                        verb = args[i];
                        break;
                    }

                    return null;
            }
        }

        if (deterministic && live)
        {
            Console.Error.WriteLine("band-g-durable: --deterministic and --live are alternatives; name one.");
            return null;
        }

        return verb is null || run is null
            ? null
            : new Cli
            {
                Verb = verb,
                Run = run,
                GateSeconds = gateSeconds,
                Fault = fault,
                TimeoutSeconds = timeout,
                Comment = comment,
                As = asLabel,
                Deterministic = deterministic,
                NoMemo = noMemo,
                Live = live,
            };
    }
}
