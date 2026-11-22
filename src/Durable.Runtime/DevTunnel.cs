using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Durable.Runtime;

/// <summary>An optional child process; its lifetime never gates HTTP readiness.</summary>
internal sealed class DevTunnel : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TextWriter _diagnostics;
    private readonly Task _monitor;
    private bool _disposing;

    private DevTunnel(Process process, TextWriter diagnostics)
    {
        _process = process;
        _diagnostics = TextWriter.Synchronized(diagnostics);
        _monitor = MonitorAsync();
    }

    internal static DevTunnel? Start(
        int port,
        TextWriter diagnostics,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        var start = new ProcessStartInfo("devtunnel")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("host");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        try
        {
            var process = (startProcess ?? Process.Start)(start);
            if (process is null)
            {
                diagnostics.WriteLine("warning: devtunnel did not start; serving locally without a tunnel.");
                return null;
            }

            return new DevTunnel(process, diagnostics);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            diagnostics.WriteLine($"warning: devtunnel is unavailable: {ex.Message}; serving locally without a tunnel.");
            return null;
        }
    }

    private async Task MonitorAsync()
    {
        try
        {
            await Task.WhenAll(PumpAsync(_process.StandardOutput), PumpAsync(_process.StandardError));
            await _process.WaitForExitAsync();
            if (!_disposing)
            {
                _diagnostics.WriteLine(
                    $"warning: devtunnel exited with code {_process.ExitCode}; serving locally without a tunnel.");
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or ObjectDisposedException)
        {
            _diagnostics.WriteLine($"warning: could not monitor devtunnel: {ex.Message}; serving locally without tunnel diagnostics.");
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            foreach (Match match in Regex.Matches(line, @"https?://[^\s<>""']+", RegexOptions.CultureInvariant))
            {
                var candidate = match.Value.TrimEnd(',', ';', ')', '.');
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var url)
                    && url.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase))
                {
                    _diagnostics.WriteLine($"dev tunnel: {url.AbsoluteUri}");
                }
            }

            _diagnostics.WriteLine($"devtunnel: {line}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The optional child may have exited between the check and the kill.
        }
        catch (Win32Exception ex)
        {
            _diagnostics.WriteLine($"warning: could not stop devtunnel: {ex.Message}");
        }

        try
        {
            await _monitor.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _diagnostics.WriteLine("warning: devtunnel did not stop within 5s.");
        }
        finally
        {
            _process.Dispose();
        }
    }
}
