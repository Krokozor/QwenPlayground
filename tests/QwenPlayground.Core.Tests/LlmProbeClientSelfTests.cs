using System.Net.Http;
using System.Text.Json;
using QwenPlayground.Core.Probes;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Live-проверка само-пробы (собственная модель, слот пробы SlotAllocation.Probe). Гоняется
/// ТОЛЬКО когда llama.cpp-сервер (AppSettings.Endpoint) доступен И запущен с нужным числом
/// слотов (≥ 5: --slots 5); иначе тест прогоняется как skip (гейт остаётся зелёным).
/// Проверяет полный C#-путь: erase слота пробы → /completion с id_slot + n_probs →
/// парсинг completion_probabilities → слот 0 не тронут.
/// [Collection("LiveProbes")] — все live-пробы идут через ОДИН слот пробы: xUnit по
/// умолчанию гоняет разные классы параллельно, и параллельные пробы гоняют erase/context
/// слота друг у друга (ответ одной пробы может прийти из контекста другой).
/// </summary>
[Collection("LiveProbes")]
public sealed class LlmProbeClientSelfTests
{
    private static string Endpoint => AppSettings.Get().Endpoint.TrimEnd('/');

    private static bool ServerReachable()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.GetAsync(Endpoint + "/props").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static int? SlotTokens(int slotId)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = http.GetAsync(Endpoint + "/slots").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("id", out var id) && id.GetInt32() == slotId &&
                    element.TryGetProperty("n_prompt_tokens", out var tokens))
                {
                    return tokens.GetInt32();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// No-think проба в шаблоне Qwen: маркеры — из QwenSpecialTokens (единый источник),
    /// пустой блок размышления в префилле ассистента (аналог enable_thinking=false в шаблоне).
    /// Промпт просит буквы категорий — как MemoryClassifier, но в формате своей модели.
    /// </summary>
    private static string BuildProbePrompt()
    {
        return QwenSpecialTokens.ImStart + QwenSpecialTokens.System + "\n"
            + "You are an assistant for classifying concepts by categories (A-Z). "
            + "Answer with category letters only, no words.\n"
            + QwenSpecialTokens.ImEnd + "\n"
            + QwenSpecialTokens.ImStart + QwenSpecialTokens.User + "\n"
            + "Categories: A: code B: build C: test D: debug E: refactor F: architecture G: tool H: project\n"
            + "Concept: compiling source code into an executable.\n"
            + "Name the relevant categories, one letter at a time, starting with the most important.\n"
            + QwenSpecialTokens.ImEnd + "\n"
            + QwenSpecialTokens.ImStart + QwenSpecialTokens.Assistant + "\n"
            + QwenSpecialTokens.ThinkStart + "\n\n" + QwenSpecialTokens.ThinkEnd + "\n\n";
    }

    [Fact]
    public void SelfProbe_Live_ReturnsPositions_ProbeSlotOnly()
    {
        if (!ServerReachable())
        {
            return; // сервера нет — skip, гейт остаётся зелёным
        }
        if (SlotTokens(QwenPlayground.Core.Inference.SlotAllocation.Probe) is null)
        {
            return; // сервер запущен без слота пробы (нужен --slots 5) — skip
        }

        var mainBefore = SlotTokens(0);

        var positions = LlmProbeClient.SelfProbePositionsAsync(BuildProbePrompt(), nProbs: 20, nPredict: 6)
            .GetAwaiter().GetResult();

        Assert.NotEmpty(positions);
        Assert.All(positions, p => Assert.NotEmpty(p.TopTokens));

        // Семантический sanity: для «compiling source code» буква B (build) должна быть в
        // топ-5 первой позиции — распределение осмысленное, а не мусор.
        var first = positions[0].TopTokens.Select(t => t.Token.Trim()).ToList();
        Assert.Contains("B", first.Take(5));

        // Проба закреплена за слотом пробы: KV main-чата не тронут.
        var mainAfter = SlotTokens(0);
        if (mainBefore is { } before && mainAfter is { } after)
        {
            Assert.Equal(before, after);
        }
        // Проба реально легла в слот пробы.
        Assert.True(SlotTokens(QwenPlayground.Core.Inference.SlotAllocation.Probe) is > 0);
    }
}
