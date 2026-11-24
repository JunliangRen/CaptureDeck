namespace CaptureRack.DeviceControl;

public sealed record ControlDeviceAddress(string ProviderId, string DeviceId);

public enum TransportCommand { Play, Pause, Stop, FastForward, Rewind, RecordToTape }

public enum TransportState { Unknown, Playing, Paused, Stopped, FastForwarding, Rewinding, RecordingToTape }

public enum TransportCommandOutcome { Unsupported, Rejected, Accepted, Confirmed, TimedOut }

/// <summary>Accepted describes the protocol response. Confirmed requires observing the requested state.</summary>
public sealed record TransportCommandResult(TransportCommandOutcome Outcome, TransportState ObservedState = TransportState.Unknown);

/// <summary>Controls a single device independently of host capture and file writing.</summary>
public interface ITransportController : IAsyncDisposable
{
    ControlDeviceAddress Device { get; }
    IReadOnlySet<TransportCommand> SupportedCommands { get; }

    // A controller that cannot report its transport state returns Unknown.
    ValueTask<TransportState> ReadStateAsync(CancellationToken cancellation);
    ValueTask<TransportCommandResult> ExecuteAsync(TransportCommand command, CancellationToken cancellation);
}
