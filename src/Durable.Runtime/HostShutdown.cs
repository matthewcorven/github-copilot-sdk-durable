using System.Runtime.InteropServices;

namespace Durable.Runtime;

/// <summary>Platform shutdown notifications, isolated from the host's portable serving path.</summary>
internal sealed class HostShutdown : IDisposable
{
    private readonly ConsoleCancelEventHandler _onCancel;
    private readonly PosixSignalRegistration? _sigterm;
    private readonly PosixSignalRegistration? _sigint;

    public HostShutdown(CancellationTokenSource stopping)
    {
        _onCancel = (_, args) =>
        {
            args.Cancel = true;
            stopping.Cancel();
        };
        if (OperatingSystem.IsWindows())
        {
            Console.CancelKeyPress += _onCancel;
        }
        else
        {
            _sigterm = Register(PosixSignal.SIGTERM, stopping);
            _sigint = Register(PosixSignal.SIGINT, stopping);
        }
    }

    private static PosixSignalRegistration Register(PosixSignal signal, CancellationTokenSource stopping) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            Console.Error.WriteLine($"received {context.Signal}: finishing the turn in flight, then exiting.");
            stopping.Cancel();
        });

    public void Dispose()
    {
        Console.CancelKeyPress -= _onCancel;
        _sigterm?.Dispose();
        _sigint?.Dispose();
    }
}
