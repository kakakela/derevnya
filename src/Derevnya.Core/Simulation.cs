namespace Derevnya.Core;

/// <summary>Состояние мира. Живёт дольше окна: переживает его пересоздание.</summary>
public sealed class WorldState
{
    private long _ticks;

    /// <summary>Сколько шагов симуляции выполнено.</summary>
    public long Ticks => Volatile.Read(ref _ticks);

    internal void Advance() => Interlocked.Increment(ref _ticks);

    public void Reset() => Interlocked.Exchange(ref _ticks, 0);
}

/// <summary>
/// Симуляция мира. Шаг фиксированный, частота шагов задаётся снаружи (из таблицы данных)
/// и не зависит от частоты кадров.
/// </summary>
public sealed class Simulation
{
    public Simulation(WorldState state)
    {
        State = state;
    }

    public WorldState State { get; }

    /// <summary>Один шаг симуляции.</summary>
    public void Step()
    {
        State.Advance();
    }
}