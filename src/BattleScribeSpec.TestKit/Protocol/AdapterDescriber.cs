namespace BattleScribeSpec.Protocol;

/// <summary>Client-side describe handshake with legacy-adapter fallback.</summary>
public static class AdapterDescriber
{
    /// <summary>
    /// Send <c>describe</c> and return the adapter's self-description. Adapters predating
    /// protocol v1.1 answer with an error (or nothing useful) — those get a legacy default:
    /// protocol 1.0, roster-only, no optional capabilities.
    /// </summary>
    /// <remarks>
    /// An adapter that has not answered YET is not a legacy one. <c>describe</c> is the first command
    /// a fresh process gets, so its answer waits on the process starting up. It had a 10s ceiling of
    /// its own, a loaded machine went past it, and the timeout fell through to the legacy default: a
    /// nameless, roster-only adapter whose gamedata specs the run then skipped (#525). It gets the
    /// ceiling every other request gets, and running out of it is an error.
    /// </remarks>
    public static async Task<DescribeResult> DescribeAsync(IAdapterConnection connection, TimeSpan? timeout = null)
    {
        var ceiling = timeout ?? JsonProtocolEngine.DefaultRequestTimeout;
        using var cts = new CancellationTokenSource(ceiling);
        try
        {
            var response = await connection.SendCommandAsync(new DescribeCommand(), cts.Token);
            if (response is DescribeResult described)
            {
                return described;
            }
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"The adapter did not answer 'describe' within {ceiling.TotalSeconds:0}s.", ex);
        }
        catch (Exception)
        {
            // Legacy adapters may fail to parse the command entirely; fall through.
        }

        return new DescribeResult { Name = "", ProtocolVersion = "1.0", Domains = ["roster"] };
    }
}
