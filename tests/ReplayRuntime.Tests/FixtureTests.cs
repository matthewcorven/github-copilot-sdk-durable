namespace ReplayRuntime.Tests;

public sealed class FixtureTests
{
    private const string ValidFixture = """
        {
          "protocolVersion": 3,
          "runtimeVersion": "0.0.0-replay",
          "models": [ { "id": "m1", "name": "Model One", "vendor": "replay" } ],
          "sessions": [
            {
              "id": "s1",
              "model": "m1",
              "turns": [ { "messageId": "msg-1", "content": "hello" } ]
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsModelsSessionsAndTurns()
    {
        var fixture = Fixture.Parse(ValidFixture);

        Assert.Equal(3, fixture.ProtocolVersion);
        Assert.Equal("0.0.0-replay", fixture.RuntimeVersion);
        Assert.Single(fixture.Models);
        Assert.Equal("m1", fixture.Models[0].Id);
        Assert.Single(fixture.Sessions);
        Assert.Equal("s1", fixture.Sessions[0].Id);
        Assert.Single(fixture.Sessions[0].Turns);
        Assert.Equal("msg-1", fixture.Sessions[0].Turns[0].MessageId);
        Assert.Equal("hello", fixture.Sessions[0].Turns[0].Content);
    }

    [Fact]
    public void Parse_AppliesDefaultsWhenOptionalFieldsAreAbsent()
    {
        var fixture = Fixture.Parse("""{ "sessions": [ { "id": "s1" } ] }""");

        Assert.Equal(3, fixture.ProtocolVersion);
        Assert.Equal("0.0.0-replay", fixture.RuntimeVersion);
        Assert.Empty(fixture.Models);
        Assert.Empty(fixture.Sessions[0].Turns);
    }

    [Fact]
    public void Parse_RejectsDuplicateSessionIds()
    {
        const string json = """
            {
              "sessions": [
                { "id": "dup" },
                { "id": "dup" }
              ]
            }
            """;

        var ex = Assert.Throws<FixtureException>(() => Fixture.Parse(json));
        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsTurnWithoutMessageId()
    {
        const string json = """
            {
              "sessions": [
                { "id": "s1", "turns": [ { "content": "no id" } ] }
              ]
            }
            """;

        var ex = Assert.Throws<FixtureException>(() => Fixture.Parse(json));
        Assert.Contains("messageId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsNonPositiveProtocolVersion()
    {
        var ex = Assert.Throws<FixtureException>(() => Fixture.Parse("""{ "protocolVersion": 0 }"""));
        Assert.Contains("protocolVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindSession_ReturnsNullForUnknownId()
    {
        var fixture = Fixture.Parse(ValidFixture);

        Assert.NotNull(fixture.FindSession("s1"));
        Assert.Null(fixture.FindSession("missing"));
    }

    [Fact]
    public void Load_ReadsCheckedInFixture()
    {
        var path = Path.Combine(RepositoryRoot(), "tools", "ReplayRuntime", "fixtures", "basic.json");

        var fixture = Fixture.Load(path);

        Assert.True(fixture.ProtocolVersion >= 3);
        Assert.NotEmpty(fixture.Models);
        Assert.NotEmpty(fixture.Sessions);
        Assert.NotEmpty(fixture.Sessions[0].Turns);
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "validate.sh")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("could not locate the repository root from the test output directory");
    }
}
