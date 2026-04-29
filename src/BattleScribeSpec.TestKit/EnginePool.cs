using System.Threading.Channels;

namespace BattleScribeSpec;

/// <summary>
/// Pool of engine instances that can be acquired and released for parallel test execution.
/// Engines are pre-created and reused across tests via a channel-based pool.
/// </summary>
public sealed class EnginePool<T> : IAsyncDisposable where T : IRosterEngine
{
    private readonly Channel<T> _pool;
    private readonly List<T> _allEngines;
    private bool _disposed;

    private EnginePool(IReadOnlyList<T> engines)
    {
        _allEngines = [.. engines];
        _pool = Channel.CreateBounded<T>(engines.Count);
        foreach (var engine in engines)
        {
            _pool.Writer.TryWrite(engine);
        }
    }

    /// <summary>
    /// Create a pool from pre-built engine instances.
    /// </summary>
    public static EnginePool<T> Create(IReadOnlyList<T> engines)
    {
        if (engines.Count == 0)
        {
            throw new ArgumentException("At least one engine instance is required.", nameof(engines));
        }

        return new EnginePool<T>(engines);
    }

    /// <summary>
    /// Number of engine instances in the pool.
    /// </summary>
    public int Size => _allEngines.Count;

    /// <summary>
    /// Acquire an engine from the pool. Returns a <see cref="PooledEngine{T}"/>
    /// that releases the engine back to the pool on dispose.
    /// </summary>
    public async ValueTask<PooledEngine<T>> AcquireAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = await _pool.Reader.ReadAsync(ct);
        return new PooledEngine<T>(engine, this);
    }

    internal void Release(T engine)
    {
        if (!_disposed)
        {
            _pool.Writer.TryWrite(engine);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pool.Writer.Complete();

        foreach (var engine in _allEngines)
        {
            if (engine is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                engine.Dispose();
            }
        }
    }
}

/// <summary>
/// A pooled engine wrapper. Disposing returns the engine to its pool.
/// </summary>
public readonly struct PooledEngine<T> : IDisposable where T : IRosterEngine
{
    private readonly EnginePool<T> _pool;

    internal PooledEngine(T engine, EnginePool<T> pool)
    {
        Engine = engine;
        _pool = pool;
    }

    /// <summary>
    /// The engine instance. Valid until this struct is disposed.
    /// </summary>
    public T Engine { get; }

    public void Dispose() => _pool.Release(Engine);
}
