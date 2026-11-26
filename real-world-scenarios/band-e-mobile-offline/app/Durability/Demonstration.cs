using System.Globalization;
using System.Text;

namespace BandEMobile.Durability;

/// <summary>
/// The demonstration's script: three edits, then a fourth after a kill.
/// </summary>
/// <remarks>
/// <para>
/// This is not a general chat client. It is a scripted demonstration of one property — that the
/// conversation and the document survive a process kill, with no server anywhere — and every choice
/// here serves that: fixed turns, a small document, and a reply format the model can honour.
/// </para>
/// <para>
/// The document is edited by the model, not by this code. Each turn sends the current body and asks
/// for a complete revised one; <see cref="Parse"/> splits the reply into the short answer shown in
/// the transcript and the new body that is committed. When the model does not honour the separator
/// the turn still completes and the reply is kept as the answer, with the body unchanged — a
/// documented degradation rather than an invented edit.
/// </para>
/// </remarks>
public static class Demonstration
{
    /// <summary>
    /// How the document is exchanged, and why it is the whole reply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document <b>is</b> the model's reply: no separator, no envelope, nothing to parse. Two
    /// earlier designs were tried on the emulator and both failed for a measured reason, recorded in
    /// <c>evidence/e2-model-protocol.txt</c>:
    /// </para>
    /// <list type="bullet">
    /// <item>A symbolic marker (<c>===DOCUMENT===</c>) was reproduced as prose. The model wrote
    /// <c>**Title:**</c> headings instead and the marker never appeared.</item>
    /// <item>A plain-word marker (<c>New version:</c>) fared no better: the reply was the document,
    /// with no marker line anywhere in it.</item>
    /// </list>
    /// <para>
    /// So the protocol is the least fragile one available: ask for the document and take the whole
    /// reply as the document. The chat line shown in the transcript is written by this app and says
    /// what happened, which is honest about which half of the screen came from the model.
    /// </para>
    /// </remarks>
    public const string DocumentMarker = "(none — the whole reply is the document)";

    /// <summary>
    /// The system prompt, and the reason it is blunt.
    /// </summary>
    /// <remarks>
    /// A 0.5B instruct model with a generic writing prompt refused the first attempt on the emulator:
    /// "I apologize, but I cannot generate content without authorship…". That is a fact about the
    /// model, not about the app, and the honest fix is to say what the turn is — a revision of a
    /// document the user already owns — and to ask for the output in a format this code can read.
    /// The refusal is kept in the E2 evidence rather than hidden, because "a smaller model costs you
    /// instruction-following" is part of the resource story requirement 5 asks for.
    /// </remarks>
    public const string SystemPrompt =
        "You are a text editor. The user gives you a CURRENT DOCUMENT and one CHANGE to make. Copy " +
        "the current document exactly, word for word, and apply only that change. Keep the title " +
        "line unchanged unless the change is about the title. Do not summarise, do not shorten, do " +
        "not add commentary, do not use markdown bold. Your whole reply is the revised document.";

    /// <summary>The fixed turns of the demonstration, in order.</summary>
    public static readonly IReadOnlyList<string> Script =
    [
        "Turn 1: write the document. Start with the title line 'Offline Release Checklist', then one " +
        "paragraph summarising a mobile app release that must work with no network.",
        "Turn 2: add a section 'Risks' with exactly two short bullet points about offline operation. " +
        "Keep everything already written.",
        "Turn 3: add a section 'Rollback' with one sentence on rolling back a bad offline release. " +
        "Keep everything already written.",
        "Turn 4: add a final line 'On-call engineer: Dana'. Change nothing else.",
    ];

    /// <summary>The document the store is seeded with, before the agent has touched it.</summary>
    public static ArtifactState SeedDocument() => new()
    {
        Title = "Offline Release Checklist",
        Body = "(empty — the agent has not written this document yet)",
        Revision = 0,
        UpdatedByTurn = null,
    };

    /// <summary>
    /// The user message for a turn: the request plus the current body, so the model revises what is
    /// actually stored rather than what it remembers. Nothing in the model remembers anything.
    /// </summary>
    public static string UserMessageFor(int turnIndex, ArtifactState document)
    {
        var request = Script[Math.Clamp(turnIndex - 1, 0, Script.Count - 1)];
        var builder = new StringBuilder();
        builder.AppendLine("TASK: " + request);
        builder.AppendLine();
        builder.AppendLine("CURRENT DOCUMENT (revision " + document.Revision + "):");
        builder.AppendLine(document.Body);
        builder.AppendLine();
        builder.AppendLine("Copy the CURRENT DOCUMENT above exactly and apply only the TASK change. " +
                           "Reply with the document and nothing else.");
        return builder.ToString();
    }

    /// <summary>
    /// Takes a model reply as the revised document, and writes the one-line chat note for it.
    /// </summary>
    /// <param name="reply">The model's whole reply.</param>
    /// <param name="currentBodyLength">
    /// The length of the document the turn was asked to revise. Used to refuse a reply that lost most
    /// of it: a document that shrinks to a sentence is a model failure, not an edit.
    /// </param>
    /// <returns>The note, the new body, and how the body was obtained.</returns>
    public static (string Answer, string? Body, string Note) Parse(string reply, int currentBodyLength = 0)
    {
        var text = (reply ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return ("(the model returned no text)", null, "empty reply");
        }

        // A model that ignored the instruction and answered conversationally would otherwise have its
        // sentence committed as the document. The two cheap signals for that are length and shape: a
        // document has more than one line, and a refusal reads like an apology.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var looksConversational =
            lines.Length == 1 && text.Length < 160 ||
            text.StartsWith("I apologize", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("I'm sorry", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Sorry,", StringComparison.OrdinalIgnoreCase);

        if (looksConversational)
        {
            return (text, null, "the reply was conversational, not a document");
        }

        // A revision may be shorter than what it replaces, but not drastically: the model sometimes
        // answers a "keep everything already written" turn with a one-line summary of the whole thing,
        // and committing that would silently delete the document the demonstration is about. Half the
        // previous length is the floor, and a turn that falls below it keeps the old document and
        // records why.
        if (currentBodyLength > 0 && text.Length * 2 < currentBodyLength)
        {
            return (text.Length > 240 ? text[..240] + "…" : text, null,
                $"reply was {text.Length} chars against a {currentBodyLength}-char document: kept the document");
        }

        var note = lines.Length == 1 ? "single-line document" : "whole reply taken as the document";
        return ($"Revised the document ({text.Length} chars, {lines.Length} lines).", text, note);
    }

    /// <summary>
    /// The document after a turn: the parsed body committed as a new revision.
    /// </summary>
    /// <remarks>
    /// The revision count is taken from the stored document so it is monotonic across process
    /// restarts: after a kill, a resumed turn continues at revision <c>n+1</c> rather than restarting
    /// at 1.
    /// </remarks>
    public static ArtifactState WithRevision(
        ArtifactState current,
        string? newBody,
        int turnIndex,
        string reply)
    {
        if (newBody is null)
        {
            return current;
        }

        var title = current.Title;
        if (current.Revision == 0 || title.Length == 0)
        {
            var firstLine = newBody.Split('\n', 2, StringSplitOptions.None)[0].TrimStart('#', '*', ' ').Trim();
            if (firstLine.Length is > 0 and < 80)
            {
                title = firstLine;
            }
        }

        return new ArtifactState
        {
            Title = title,
            Body = newBody,
            Revision = current.Revision + 1,
            UpdatedByTurn = turnIndex,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
    }
}
