using System.Drawing;

namespace Derevnya.Core;

public sealed class World
{
    private WorldGrid _grid;

    public World(WorldConfig config, Size screen)
    {
        Config = config;
        _grid = CreateGrid(screen);
    }

    public WorldConfig Config { get; }

    /// <summary>Текущая сетка. Можно читать из любого потока.</summary>
    public WorldGrid Grid => Volatile.Read(ref _grid);

    /// <summary>Строит мир заново под новое разрешение (только по команде «Перезапустить мир»).</summary>
    public void Rebuild(Size screen)
    {
        Volatile.Write(ref _grid, CreateGrid(screen));
    }

    private WorldGrid CreateGrid(Size screen)
    {
        var grid = new WorldGrid(screen, Config.TileSize);
        Logger.Log(
            $"Мир: экран {screen.Width}x{screen.Height}, множитель 1 (пиксель мира = пиксель экрана), " +
            $"тайл {grid.TileSize} px, размер мира {grid.TilesX}x{grid.TilesY} тайлов");
        return grid;
    }
}