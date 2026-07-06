namespace BattleScribeSpec.Protocol;

/// <summary>
/// A request/response channel to an adapter speaking the NDJSON protocol.
/// Implemented by <see cref="AdapterProcess"/> (child process) and by in-memory
/// test doubles; protocol engines depend on this instead of a concrete process.
/// </summary>
public interface IAdapterConnection
{
    /// <summary>Send a protocol command and await the single response.</summary>
    Task<ProtocolResponse> SendCommandAsync(ProtocolCommand command, CancellationToken ct = default);
}
