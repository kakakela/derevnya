using System.Text.Json;

namespace Derevnya.Core;

public sealed class WorldConfig
{
    /// <summary>Шагов симуляции в секунду (не зависит от частоты кадров).</summary>
    public int StepsPerSecond { get; init; } = 10;

    /// <summary>Кадров в секунду.</summary>
    public int FramesPerSecond { get; init; } = 30;

    /// <summary>Как часто писать в журнал измеренные частоты кадров и шагов, секунд.</summary>
    public int StatsLogIntervalSeconds { get; init; } = 10;

    /// <summary>Сторона тайла, пикселей.</summary>
    public int TileSize { get; init; } = 32;

    /// <summary>Зерно генератора случайных чисел: одно зерно даёт одно и то же поведение.</summary>
    public int Seed { get; init; } = 12345;

    /// <summary>Отладочная картинка: сетка и маска поверх земли.</summary>
    public bool DebugOverlay { get; init; } = false;

    public override string ToString() =>
        $"шагов/с {StepsPerSecond}, кадров/с {FramesPerSecond}, журнал частот раз в {StatsLogIntervalSeconds} с, " +
        $"тайл {TileSize}, зерно {Seed}, отладка {(DebugOverlay ? "вкл" : "выкл")}";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Читает data\world.json рядом с программой. При любой проблеме возвращает значения по умолчанию.</summary>
    public static WorldConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "data", "world.json");

        if (!File.Exists(path))
        {
            Logger.Log($"Конфигурация: файл не найден ({path}), беру значения по умолчанию");
            return Validate(new WorldConfig());
        }

        try
        {
            string json = File.ReadAllText(path);
            WorldConfig? cfg = JsonSerializer.Deserialize<WorldConfig>(json, Options);
            if (cfg == null)
            {
                Logger.Log($"ОШИБКА конфигурации: {path} пуст, беру значения по умолчанию");
                return Validate(new WorldConfig());
            }

            Logger.Log($"Конфигурация прочитана: {path}");
            return Validate(cfg);
        }
        catch (Exception ex)
        {
            Logger.Log($"ОШИБКА конфигурации ({path}): {ex.Message}. Беру значения по умолчанию");
            return Validate(new WorldConfig());
        }
    }

    /// <summary>Ограничивает значения разумными пределами и пишет в журнал итог.</summary>
    private static WorldConfig Validate(WorldConfig cfg)
    {
        var fixedCfg = new WorldConfig
        {
            StepsPerSecond = Math.Clamp(cfg.StepsPerSecond, 1, 60),
            FramesPerSecond = Math.Clamp(cfg.FramesPerSecond, 1, 60),
            StatsLogIntervalSeconds = Math.Clamp(cfg.StatsLogIntervalSeconds, 1, 600),
            TileSize = Math.Clamp(cfg.TileSize, 8, 128),
            Seed = cfg.Seed,
            DebugOverlay = cfg.DebugOverlay
        };

        Logger.Log($"Конфигурация: {fixedCfg}");
        return fixedCfg;
    }
}