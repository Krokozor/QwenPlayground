using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Inference;

public sealed class GenerationOptions
{
    public int MaxTokens { get; init; } = 1024;
    public double Temperature { get; init; } = 0.7;
    public double TopP { get; init; } = 0.8;
    public int TopK { get; init; } = 20;
    public double MinP { get; init; }
    public double RepeatPenalty { get; init; } = 1.05;
    public int? Seed { get; init; }
    public IReadOnlyList<string> Stop { get; init; } = [QwenSpecialTokens.ImEnd, QwenSpecialTokens.EndOfText];

    /// <summary>
    /// Пиннинг слота llama.cpp (id_slot в /completion): main → 0, субагент → 1,
    /// побочное окно → 2, сервисные вызовы → 3 (см. <see cref="SlotAllocation"/>).
    /// null — сервер выбирает сам (LRU). Мутабельный сознательно: это маршрутный
    /// метаданные, проставляемые точкой запуска хода (AgentLoop), а не параметр
    /// генерации; инстанс GenerationOptions на ход создаётся свежий и не шарится.
    /// </summary>
    public int? IdSlot { get; set; }
}
