using System.Diagnostics;
using System.Text;
using BandEMobile.Chat;
using BandEMobile.Durability;

namespace BandEMobile;

/// <summary>
/// The demonstration screen: the document, the conversation, and the tokens as they arrive.
/// </summary>
/// <remarks>
/// <para>
/// Two things happen here and nothing else: a turn is streamed and committed, and what the store
/// holds is rendered. The page keeps no conversation of its own — the list it shows is read back
/// from <see cref="BandEDurable.ReadSession"/> after every write — so if the transcript is on screen
/// after a kill it came from the file.
/// </para>
/// <para>
/// The page is deliberately not a chat client. The four scripted turns exist so the demonstration is
/// the same four turns every time; the free-text box exists so a reader can probe the model.
/// </para>
/// </remarks>
public partial class MainPage : ContentPage
{
    private readonly AppHost _host;
    private bool _busy;

    public MainPage()
    {
        InitializeComponent();
        _host = AppHost.Create();
        Loaded += async (_, _) => await FirstRenderAsync();
    }

    private async Task FirstRenderAsync()
    {
        try
        {
            _host.Trace("app: page loaded");
            if (!_host.Durable.HasState)
            {
                await _host.Durable.SeedAsync(Demonstration.SeedDocument());
                _host.Trace("app: seeded a pristine store");
            }

            Render("restored from the durable store: " + _host.StoreFingerprint());
        }
        catch (Exception ex)
        {
            _host.Trace($"app: first render failed {ex.GetType().Name}: {ex.Message}");
            StatusLabel.Text = $"startup failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ─── Rendering ──────────────────────────────────────────────────────────────────────────────

    private void Render(string note)
    {
        var session = _host.Durable.ReadSession();
        var document = session.Document;

        DocumentRevisionLabel.Text =
            $"revision {document.Revision} · updated by turn {document.UpdatedByTurn?.ToString() ?? "—"} · " +
            $"{_host.Durable.ArtifactVersions.Count} committed versions";
        DocumentTitleLabel.Text = document.Title.Length == 0 ? "(untitled)" : document.Title;
        DocumentBodyLabel.Text = document.Body.Length == 0 ? "(empty)" : document.Body;

        TranscriptList.Clear();
        if (session.Turns.Count == 0)
        {
            TranscriptList.Add(new Label
            {
                Text = "(no turns in the durable transcript)",
                FontSize = 11,
                FontAttributes = FontAttributes.Italic,
            });
        }

        foreach (var turn in session.Turns)
        {
            TranscriptList.Add(new Label
            {
                FontSize = 11,
                LineBreakMode = LineBreakMode.WordWrap,
                Text = $"▶ {turn.Index}. {turn.Request}{Environment.NewLine}" +
                       $"{turn.Reply ?? turn.Note ?? "(no reply recorded)"}",
            });
        }

        foreach (var interrupted in session.Interrupted)
        {
            TranscriptList.Add(new Label
            {
                FontSize = 11,
                TextColor = Colors.DarkOrange,
                LineBreakMode = LineBreakMode.WordWrap,
                Text = $"⚠ turn {interrupted.TurnIndex} {interrupted.Phase}: no reply was committed " +
                       $"(started {interrupted.StartedAt})",
            });
        }

        StatusLabel.Text =
            $"turns {session.Turns.Count} · revisions {_host.Durable.ArtifactVersions.Count} · " +
            $"interrupted {session.Interrupted.Count} · {_host.ChatClient.Name}{Environment.NewLine}" +
            note;

        RenderResources();
        SetBusy(_busy);
    }

    private void RenderResources()
    {
        var resources = _host.LastResources;
        ResultLabel.Text = resources is null
            ? "resources: not measured yet — tap Measure"
            : $"PSS {resources.ProcessPssMb:F0} MB of {resources.SystemTotalMb:F0} MB " +
              $"({resources.SystemAvailableMb:F0} MB free) · model cache " +
              $"{resources.ModelCacheBytes / 1048576.0:F1} MB in {resources.ModelFileCount} files";
    }

    private void SetBusy(bool busy)
    {
        // Diagnostics wants to know whether a button was disabled when a tap arrived, because a
        // disabled button swallows the tap and the screen then looks frozen.
        _host.Trace($"busy {busy}");
        _busy = busy;
        SendButton.IsEnabled = !busy;
        ScriptButton.IsEnabled = !busy;
        PrepareButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        CaptureButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        CatalogButton.IsEnabled = !busy;
    }

    // ─── Actions ────────────────────────────────────────────────────────────────────────────────

    private async void OnPrepareClicked(object? sender, EventArgs e) => await GuardAsync("prepare", PrepareAsync);

    private async void OnCatalogClicked(object? sender, EventArgs e) => await GuardAsync("catalog", CatalogAsync);

    private async void OnCaptureClicked(object? sender, EventArgs e) => await GuardAsync("measure", MeasureAsync);

    private async void OnExportClicked(object? sender, EventArgs e) => await GuardAsync("export", ExportAsync);

    private async void OnResetClicked(object? sender, EventArgs e) => await GuardAsync("reset", ResetAsync);

    private async void OnSendClicked(object? sender, EventArgs e) => await GuardAsync("send", SendAsync);

    private void OnScriptClicked(object? sender, EventArgs e)
    {
        var next = _host.Durable.ReadSession().Turns.Count + 1;
        InputEntry.Text = Demonstration.Script[Math.Clamp(next - 1, 0, Demonstration.Script.Count - 1)];
    }

    /// <summary>
    /// Runs an action, catching everything. An <c>async void</c> handler's exception is otherwise
    /// swallowed by the framework and the screen simply stops — which is how this page was found to
    /// be losing turns, so every handler goes through here instead.
    /// </summary>
    private async Task GuardAsync(string name, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _host.Trace($"error[{name}] {ex.GetType().Name}: {ex.Message}");
            ResultLabel.Text = $"{name} failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task PrepareAsync()
    {
        SetBusy(true);
        var before = _host.MeasureModelCache();
        var clock = Stopwatch.StartNew();
        _host.Trace($"prepare: cache before {before.Bytes} B / {before.Files} files");

        var prepared = await _host.ChatClient.PrepareAsync(progress =>
            MainThread.BeginInvokeOnMainThread(() => ResultLabel.Text = progress));

        clock.Stop();
        _host.Trace($"prepare: {clock.Elapsed.TotalMilliseconds:F0} ms, ok={prepared.Succeeded}");

        if (!prepared.Succeeded)
        {
            ResultLabel.Text = $"prepare failed [{prepared.Failure!.Kind}]: {prepared.Failure.Message}";
            _host.ExportEvidence("prepare-failed");
            return;
        }

        var after = _host.CaptureResources($"prepare: {clock.Elapsed.TotalMilliseconds:F0} ms");
        ResultLabel.Text =
            $"model ready in {clock.Elapsed.TotalSeconds:F1} s · cache {after.ModelCacheBytes / 1048576.0:F1} MB " +
            $"in {after.ModelFileCount} files (+{(after.ModelCacheBytes - before.Bytes) / 1048576.0:F1} MB this run)";
        _host.Trace($"prepare: cache after {after.ModelCacheBytes} B / {after.ModelFileCount} files");
        _host.ExportEvidence("model-ready");
    }

    private async Task CatalogAsync()
    {
        SetBusy(true);
        var aliases = await _host.ChatClient.ListAliasesAsync();
        ResultLabel.Text = aliases.Count == 0
            ? "catalog unavailable — see the trace"
            : "catalog: " + string.Join(", ", aliases.Take(15));
        _host.Trace($"catalog: {string.Join(",", aliases)}");
    }

    private async Task MeasureAsync()
    {
        SetBusy(true);
        var resources = _host.CaptureResources("manual measurement");
        foreach (var entry in _host.ModelCacheListing())
        {
            _host.Trace($"cache-file {Path.GetFileName(entry.Path)} {entry.Bytes} B");
        }

        RenderResources();
        _host.ExportEvidence("measure");
        _host.Trace($"measure: pss {resources.ProcessPssMb:F1} MB, " +
                    $"system {resources.SystemAvailableMb:F0}/{resources.SystemTotalMb:F0} MB");
    }

    private Task ExportAsync()
    {
        SetBusy(true);
        var directory = _host.ExportEvidence("manual");
        ResultLabel.Text = directory is null ? "no export directory on this device" : $"exported {directory}";
        _host.Trace($"export: {directory}");
        return Task.CompletedTask;
    }

    private async Task ResetAsync()
    {
        SetBusy(true);
        await _host.Durable.ResetAsync(Demonstration.SeedDocument());
        _host.Trace("reset: a fresh transcript and the seed document were committed as new revisions");
        Render("reset: no turns, revision 0");
    }

    private async Task SendAsync()
    {
        var text = InputEntry.Text?.Trim();
        _host.Trace($"send: busy={_busy} text={text ?? "(empty)"}");
        if (string.IsNullOrEmpty(text))
        {
            ResultLabel.Text = "tap 'Next scripted turn' to fill the box, then Send";
            return;
        }

        SetBusy(true);
        InputEntry.Text = string.Empty;
        await RunTurnAsync(text);
    }

    private async Task RunTurnAsync(string request)
    {
        var before = _host.Durable.ReadSession();
        var turnIndex = before.Turns.Count + 1;

        if (!_host.ChatClient.IsReady && !await PrepareReadyAsync())
        {
            Render("turn not started: the model is not ready");
            return;
        }

        // The checkpoint is committed before the model is asked anything, so a kill from here on
        // leaves a task the next start reports as interrupted rather than a turn that never happened.
        var taskId = await _host.Durable.BeginTurnAsync(turnIndex, request);
        _host.Trace($"turn {turnIndex}: checkpoint committed as {taskId}");

        var prompt = Demonstration.UserMessageFor(turnIndex, before.Document);
        var conversation = BuildConversation(before, prompt);
        _host.Trace($"turn {turnIndex}: {conversation.Count} messages, prompt {prompt.Length} chars");

        var streamed = new StringBuilder();
        var chunks = 0;
        var clock = Stopwatch.StartNew();

        try
        {
            // A cap, not a target: the emulator generates slowly enough that an uncapped revision can
            // outrun any turn budget. 160 tokens is what the 0.5B model used to emit the ~2 000-character
            // documents this demonstration edits, and the trace records how many deltas it produced.
            await foreach (var delta in _host.ChatClient.CompleteStreamingAsync(conversation, maxTokens: 220))
            {
                chunks++;
                streamed.Append(delta);
                StreamingLabel.Text = "▶ " + (streamed.Length <= 220 ? streamed.ToString() : streamed.ToString()[^220..]);
            }

            clock.Stop();
            _host.Trace($"turn {turnIndex}: stream closed, {chunks} deltas, {streamed.Length} chars, " +
                        $"{clock.Elapsed.TotalMilliseconds:F0} ms");

            await CommitAsync(taskId, turnIndex, request, before.Document, streamed.ToString(), clock.Elapsed, chunks);
        }
        catch (BridgeException failure)
        {
            clock.Stop();
            _host.Trace($"turn {turnIndex}: bridge failure [{failure.Kind}] {failure.Message}");
            await AbandonAsync(taskId, $"turn {turnIndex} [{failure.Kind}] {failure.Message}");
            ResultLabel.Text = $"turn {turnIndex} failed [{failure.Kind}]: {failure.Message}";
        }
        catch (Exception ex)
        {
            clock.Stop();
            _host.Trace($"turn {turnIndex}: {ex.GetType().Name}: {ex.Message}");
            await AbandonAsync(taskId, $"turn {turnIndex} {ex.GetType().Name}: {ex.Message}");
            ResultLabel.Text = $"turn {turnIndex} failed: {ex.GetType().Name}: {ex.Message}";
            _host.ExportEvidence("turn-error");
        }

        Render($"turn {turnIndex} finished · {_host.StoreFingerprint()}");
    }

    private async Task<bool> PrepareReadyAsync()
    {
        var prepared = await _host.ChatClient.PrepareAsync();
        if (!prepared.Succeeded)
        {
            ResultLabel.Text = $"prepare failed [{prepared.Failure!.Kind}]: {prepared.Failure.Message}";
            return false;
        }

        return true;
    }

    private async Task CommitAsync(
        string taskId,
        int turnIndex,
        string request,
        ArtifactState documentBeforeTurn,
        string reply,
        TimeSpan elapsed,
        int chunks)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            // A stream that ended with nothing is not a completed turn. Committing it as one would
            // durably record an answer that does not exist.
            await AbandonAsync(taskId, $"turn {turnIndex} produced no text");
            ResultLabel.Text = $"turn {turnIndex}: the stream produced no text; the turn was not committed";
            return;
        }

        var (answer, body, note) = Demonstration.Parse(reply, documentBeforeTurn.Body.Length);
        var document = Demonstration.WithRevision(documentBeforeTurn, body, turnIndex, reply);

        var stored = new StoredTurn
        {
            Request = request,
            Reply = answer,
            Note = note,
            ElapsedMs = (long)elapsed.TotalMilliseconds,
            ChunkCount = chunks,
        };

        await _host.Durable.CompleteTurnAsync(taskId, turnIndex, stored, body is null ? null : document);
        _host.Trace($"turn {turnIndex}: committed reply {answer.Length} chars, document rev {document.Revision} ({note})");

        var tokens = CountWords(answer) + CountWords(body ?? string.Empty);
        var perSecond = elapsed.TotalSeconds > 0 ? tokens / elapsed.TotalSeconds : 0;

        // Every committed turn writes its own export, so the store's bytes for each turn are on disk
        // next to the reply. This is what makes "the store after turn 3" quotable in evidence.
        _host.CaptureResources(
            $"turn {turnIndex}: {chunks} deltas, {elapsed.TotalMilliseconds:F0} ms",
            $"turn {turnIndex}: ~{perSecond:F1} words/s over reply+document");
        _host.ExportEvidence($"turn-{turnIndex}");
        ResultLabel.Text =
            $"turn {turnIndex}: {chunks} deltas · {elapsed.TotalMilliseconds:F0} ms · " +
            $"~{perSecond:F1} words/s · document rev {document.Revision} ({note})";
    }

    private Task AbandonAsync(string taskId, string reason) => _host.Durable.AbandonTurnAsync(taskId, reason);

    /// <summary>
    /// The history the transport is given: the durable transcript plus this turn.
    /// </summary>
    /// <remarks>
    /// The system prompt is not stored: it belongs to the transport, not to what the user said. The
    /// prompt for this turn is built and handed over as a string before the call, so the history
    /// cannot change underneath a streaming turn.
    /// </remarks>
    private static IReadOnlyList<ChatMessage> BuildConversation(SessionSnapshot session, string prompt)
    {
        var messages = new List<ChatMessage> { ChatMessage.System(Demonstration.SystemPrompt) };
        foreach (var turn in session.Turns)
        {
            messages.Add(ChatMessage.User(turn.Request));
            if (!string.IsNullOrEmpty(turn.Reply))
            {
                messages.Add(ChatMessage.Assistant(turn.Reply));
            }
        }

        messages.Add(ChatMessage.User(prompt));
        return messages;
    }

    /// <summary>
    /// Word count over the reply, the only token measure available without the model's tokenizer.
    /// </summary>
    /// <remarks>
    /// It is called words/s on screen, not tok/s, because it is not tokens: the honest figure the
    /// evidence needs is the SDK's own <c>Generator.TokenCount</c>, which is in logcat, and the
    /// README's resource table says which of the two each number is.
    /// </remarks>
    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
