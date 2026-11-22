using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Durable.EfCore.Tests;

/// <summary>
/// The skip mechanism itself: a test that needs a server must skip with a reason that
/// names the endpoint, and must never pass by doing nothing.
/// </summary>
/// <remarks>
/// These tests do not need a server, so they run everywhere. They are what makes the skip
/// provable: the reason a server test did not run is asserted here, rather than being
/// inferred from a green suite.
/// </remarks>
public sealed class ServerProbeTests
{
    [Fact]
    public void Probe_Reports_An_Unreachable_Endpoint_With_A_Reason()
    {
        // Port 1 on the loopback interface is closed on every supported platform, so this
        // exercises the unreachable path without depending on what is installed.
        var result = ServerProbe.Probe(
            "the closed test endpoint at 127.0.0.1:1",
            () => new SqlConnection(
                "Server=127.0.0.1,1;Database=none;User Id=none;Password=none;" +
                "Connect Timeout=1;TrustServerCertificate=True;Encrypt=False"));

        Assert.False(result.IsReachable);
        Assert.NotNull(result.Reason);
        Assert.Contains("not reachable", result.SkipMessage, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:1", result.SkipMessage, StringComparison.Ordinal);
        Assert.Contains(result.Reason!, result.SkipMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_Reports_A_Reachable_Endpoint_Without_A_Reason()
    {
        // A connection that needs no server: the probe's contract is "did Open() succeed",
        // and this proves the reachable branch reports no reason.
        var result = ServerProbe.Probe("an in-process connection", () => new NoServerConnection());

        Assert.True(result.IsReachable);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(ServerKind.SqlServer)]
    [InlineData(ServerKind.PostgreSql)]
    [InlineData(ServerKind.MySql)]
    public void Server_Fact_Skips_Exactly_When_The_Probe_Says_Unreachable(ServerKind kind)
    {
        var probe = ServerProbe.For(kind);
        var attribute = new ServerFactAttribute(kind);

        if (probe.IsReachable)
        {
            Assert.Null(attribute.Skip);
        }
        else
        {
            Assert.Equal(probe.SkipMessage, attribute.Skip);
            Assert.Contains(probe.Endpoint, attribute.Skip, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(ServerKind.SqlServer)]
    [InlineData(ServerKind.PostgreSql)]
    [InlineData(ServerKind.MySql)]
    public void Probe_Names_The_Endpoint_It_Tried(ServerKind kind)
    {
        var probe = ServerProbe.For(kind);

        Assert.False(string.IsNullOrWhiteSpace(probe.Endpoint));
        Assert.Contains("localhost", probe.Endpoint, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoServerConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "none";

        public override string DataSource => "none";

        public override string ServerVersion => "0";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) =>
            throw new NotSupportedException();

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
