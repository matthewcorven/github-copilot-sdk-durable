using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Durable.Runtime;

namespace Durable.Tests;

public sealed class RuntimeHostPortabilityTests
{
    [Fact]
    public async Task OptionalTool_DisposalStopsItsChild()
    {
        var record = Path.Combine(Path.GetTempPath(), $"tunnel-lifetime-{Guid.NewGuid():N}.json");
        Process? child = null;
        using var diagnostics = new StringWriter();
        try
        {
            var tunnel = DevTunnel.Start(4321, diagnostics, start =>
            {
                start.FileName = Path.Combine(AppContext.BaseDirectory, "tunnel-fixture",
                    OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel");
                start.Environment["DURABLE_TUNNEL_FIXTURE_RECORD"] = record;
                child = Process.Start(start);
                return child;
            });
            Assert.NotNull(tunnel);
            Assert.NotNull(child);
            var pid = child.Id;
            await tunnel.DisposeAsync();
            using var remaining = TryGetProcess(pid);
            Assert.True(remaining is null || remaining.HasExited, $"Optional child {pid} is still running.");
            Assert.DoesNotContain("warning:", diagnostics.ToString());
        }
        finally
        {
            File.Delete(record);
        }
    }

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }

    [Fact]
    public async Task OptionalTool_StartFailureLeavesHttpServing()
    {
        using var diagnostics = new StringWriter();
        await using var transport = HttpTransport.Start(0);
        await using var tunnel = DevTunnel.Start(transport.Port, diagnostics,
            _ => throw new Win32Exception(5, "controlled execution refusal"));
        using var stopping = new CancellationTokenSource();
        var serving = transport.RunAsync(
            (_, response) => response.WriteErrorAsync(400, "Bad Request", "portable server is serving"),
            stopping.Token, TimeSpan.FromSeconds(5));
        try
        {
            using var client = new HttpClient();
            using var response = await client.PostAsync($"http://127.0.0.1:{transport.Port}/", new StringContent("{}"));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("portable server is serving", await response.Content.ReadAsStringAsync());
            Assert.Null(tunnel);
            Assert.Contains("warning: devtunnel is unavailable:", diagnostics.ToString());
            Assert.DoesNotContain("error:", diagnostics.ToString());
        }
        finally
        {
            stopping.Cancel();
            await serving;
        }
    }

    [Fact]
    public void CorePath_HasNoPlatformSpecificApis()
    {
        var sources = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "runtime-source"), "*.cs");
        Assert.Contains(sources, path => Path.GetFileName(path) == "Program.cs");
        Assert.Contains(sources, path => Path.GetFileName(path) == "HostShutdown.cs");
        const string forbidden = @"\b(OperatingSystem|OSPlatform|RuntimeInformation|PosixSignal|PosixSignalRegistration|DllImport|LibraryImport|Registry|WindowsIdentity)\b|Environment\s*\.\s*(OSVersion|SpecialFolder)|[A-Za-z]:\\";
        foreach (var path in sources.Where(path => Path.GetFileName(path) != "HostShutdown.cs"))
        {
            Assert.False(Regex.IsMatch(File.ReadAllText(path), forbidden, RegexOptions.CultureInvariant),
                $"Platform-specific API in portable runtime source: {Path.GetFileName(path)}");
        }
    }
}
