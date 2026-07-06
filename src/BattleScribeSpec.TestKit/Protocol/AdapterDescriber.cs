namespace BattleScribeSpec.Protocol;

/// <summary>Client-side describe handshake with legacy-adapter fallback.</summary>
public static class AdapterDescriber
{
    /// <summary>
    /// Send <c>describe</c> and return the adapter's self-description. Adapters predating
    /// protocol v1.1 answer with an error (or nothing useful) — those get a legacy default:
    /// protocol 1.0, roster-only, no optional capabilities.
    /// </summary>
    public static async Task<DescribeResult> DescribeAsync(IAdapterConnection connection, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            var response = await connection.SendCommandAsync(new DescribeCommand(), cts.Token);
            if (response is DescribeResult described)
            {
                return described;
            }
        }
        catch (Exception)
        {
            // Legacy adapters may fail to parse the command entirely; fall through.
        }

        return new DescribeResult { Name = "", ProtocolVersion = "1.0", Domains = ["roster"] };
    }
}
