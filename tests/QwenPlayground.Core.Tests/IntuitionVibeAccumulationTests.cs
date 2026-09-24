using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Чистые тесты AccumulateEmoji (vibe): реконструкция кандидатов окна
/// (префикс выбранного символа + байт кандидата по id) и накопление масс.
/// byteResolver фейковый — без файла vocab и без сервера.
/// </summary>
public sealed class IntuitionVibeAccumulationTests
{
    // Фейковый словарь: id 1 → 0xA5, id 2 → 0xBB, id 3 → 0x81; остальное — не байт-токены.
    private static int? FakeByte(int id) => id switch
    {
        1 => 0xA5,
        2 => 0xBB,
        3 => 0x81,
        _ => null,
    };

    private static ProbeResult Pos(string chosen, (int Id, double P)[] window)
    {
        var tokens = window
            .Select(w => new ProbeToken(chosen, Math.Log(w.P), w.Id))
            .ToList();
        return new ProbeResult(chosen, Math.Log(window[0].P), tokens, 0.0, chosen);
    }

    [Fact]
    public void AccumulatesReconstructedEmojiAndNormalizes()
    {
        // chosen = 💥 (F0 9F 92 A5): префикс F0 9F 92.
        // Кандидаты: A5 → 💥 (0.7), BB → 📻 (0.2), 81 → 💁 (0.1).
        var positions = new[]
        {
            Pos("\U0001F4A5", new[] { (1, 0.7), (2, 0.2), (3, 0.1) })
        };

        var layers = IntuitionProbeRunner.AccumulateEmoji(positions, FakeByte);

        Assert.Equal(3, layers.Count);
        Assert.Equal(0.7, layers["\U0001F4A5"], precision: 3); // 💥
        Assert.Equal(0.2, layers["\U0001F4BB"], precision: 3); // 📻
        Assert.Equal(0.1, layers["\U0001F481"], precision: 3); // 💁
        Assert.Equal(1.0, layers.Values.Sum(), precision: 3);
    }

    [Fact]
    public void SkipsNonByteTokensAndInvalidSequences()
    {
        // chosen = 💥 (префикс F0 9F 92). Кандидаты:
        // id 99 → null (не байт-токен) → пропуск;
        // id 4 → 0x20: F0 9F 92 20 — невалидный UTF-8 → пропуск;
        // id 1 → 0xA5: 💥 → остаётся (единственный → масса 1.0).
        var positions = new[]
        {
            Pos("\U0001F4A5", new[] { (99, 0.4), (4, 0.3), (1, 0.3) })
        };

        var layers = IntuitionProbeRunner.AccumulateEmoji(positions, id => id switch
        {
            1 => 0xA5,
            4 => 0x20,
            _ => null,
        });

        Assert.Single(layers);
        Assert.Equal(1.0, layers["\U0001F4A5"], precision: 3);
    }

    [Fact]
    public void SkipsPositionsWithoutEmojiChosen()
    {
        // Позиция с не-эмодзи (например, \n или EOS) — в накопление не участвует.
        var positions = new[] { Pos("\n", new[] { (1, 1.0) }) };

        var layers = IntuitionProbeRunner.AccumulateEmoji(positions, FakeByte);

        Assert.Empty(layers);
    }

    [Fact]
    public void AccumulatesAcrossPositions()
    {
        // Две позиции с общим кандидатом 💥: массы суммируются, итог нормируется в 1.
        var positions = new[]
        {
            Pos("\U0001F4A5", new[] { (1, 0.9), (2, 0.1) }),  // 💥=0.9, 📻=0.1
            Pos("\U0001F4BB", new[] { (1, 0.5), (2, 0.5) })   // префикс 📻 (F0 9F 92 BB... нет:
        };
        // Второй chosen = 📻 (F0 9F 92 BB): префикс F0 9F 92, тот же блок кандидатов.
        var layers = IntuitionProbeRunner.AccumulateEmoji(positions, FakeByte);

        // 💥: 0.9 + 0.5 = 1.4; 📻: 0.1 + 0.5 = 0.6; сумма 2.0 → 0.7 / 0.3.
        Assert.Equal(0.7, layers["\U0001F4A5"], precision: 3);
        Assert.Equal(0.3, layers["\U0001F4BB"], precision: 3);
    }
}
