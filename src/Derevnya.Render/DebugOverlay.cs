using System.Drawing;
using Derevnya.Core;

namespace Derevnya.Render;

public static class DebugOverlay
{
    /// <summary>Рисует маску препятствий и сетку тайлов в координатах мира (пиксель мира = пиксель экрана).</summary>
    public static void Draw(Graphics g, WorldGrid grid)
    {
        ObstacleMask mask = grid.Mask;
        int ts = grid.TileSize;

        var blocked = new List<Rectangle>();
        for (int ty = 0; ty < grid.TilesY; ty++)
        {
            for (int tx = 0; tx < grid.TilesX; tx++)
            {
                if (mask.IsBlocked(tx, ty))
                {
                    blocked.Add(new Rectangle(tx * ts, ty * ts, ts, ts));
                }
            }
        }

        if (blocked.Count > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb(90, 255, 140, 0));
            g.FillRectangles(fill, blocked.ToArray());
        }

        int width = grid.TilesX * ts;
        int height = grid.TilesY * ts;
        using var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
        for (int x = 0; x <= grid.TilesX; x++)
        {
            g.DrawLine(pen, x * ts, 0, x * ts, height);
        }
        for (int y = 0; y <= grid.TilesY; y++)
        {
            g.DrawLine(pen, 0, y * ts, width, y * ts);
        }
    }
}