using System.Text.Json.Nodes;

namespace BandC.Durable;

/// <summary>
/// The frozen snapshot that stands in for the Phase 1 <c>pull_request_read</c> calls.
/// </summary>
/// <remarks>
/// This file is application logic, not durability. It is the baseline's model, unchanged: the same
/// fields, the same default handling. It has no opinion about where anything is stored.
/// </remarks>
public sealed class Fixture
{
    public required string Repository { get; init; }
    public required string CapturedAt { get; init; }
    public required List<PullRequest> PullRequests { get; init; }

    /// <summary>Loads the fixture, or throws naming the path.</summary>
    public static Fixture Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"no fixture at {path}");
        }

        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"fixture {path} is not a JSON object");

        var list = new List<PullRequest>();
        foreach (var node in root["pullRequests"]!.AsArray())
        {
            var o = node!.AsObject();
            list.Add(new PullRequest
            {
                Number = o["number"]!.GetValue<int>(),
                Title = o["title"]!.GetValue<string>(),
                Author = o["author"]!.GetValue<string>(),
                HeadRef = o["headRef"]!.GetValue<string>(),
                IsFork = o["isFork"]!.GetValue<bool>(),
                State = o["state"]!.GetValue<string>(),
                OpenedMinutesAgo = o["openedMinutesAgo"]!.GetValue<int>(),
                UpdatedMinutesAgo = o["updatedMinutesAgo"]!.GetValue<int>(),
                Files = o["files"]!.AsArray().Select(f => f!.GetValue<string>()).ToArray(),
                Diff = new DiffShape(
                    o["diff"]!["files"]!.GetValue<int>(),
                    o["diff"]!["additions"]!.GetValue<int>(),
                    o["diff"]!["deletions"]!.GetValue<int>(),
                    o["diff"]!["whitespaceOnly"]!.GetValue<bool>()),
                Ci = o["ci"]!.GetValue<string>(),
                Reviews = o["reviews"]!.GetValue<string>(),
                AgentQuality = o["agentQuality"]!.GetValue<string>(),
                Labels = o["labels"]!.AsArray().Select(l => l!.GetValue<string>()).ToArray(),
                SupersededBy = o["supersededBy"] is null ? null : o["supersededBy"]!.GetValue<int>(),
            });
        }

        return new Fixture
        {
            Repository = root["repository"]!.GetValue<string>(),
            CapturedAt = root["capturedAt"]!.GetValue<string>(),
            PullRequests = list,
        };
    }
}

/// <summary>One pull request, as the frozen snapshot describes it.</summary>
public sealed class PullRequest
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string HeadRef { get; init; }
    public required bool IsFork { get; init; }
    public required string State { get; init; }
    public required int OpenedMinutesAgo { get; init; }
    public required int UpdatedMinutesAgo { get; init; }
    public required string[] Files { get; init; }
    public required DiffShape Diff { get; init; }
    public required string Ci { get; init; }
    public required string Reviews { get; init; }
    public required string AgentQuality { get; init; }
    public required string[] Labels { get; init; }
    public required int? SupersededBy { get; init; }
}

public sealed record DiffShape(int Files, int Additions, int Deletions, bool WhitespaceOnly);
