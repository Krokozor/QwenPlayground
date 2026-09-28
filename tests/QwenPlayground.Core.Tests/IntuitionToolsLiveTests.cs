using QwenPlayground.Core.Probes;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Live-проверка тулов «интуиции» end-to-end (собственная модель, слот пробы SlotAllocation.Probe):
/// промпт → SelfProbePositionsAsync (erase + id_slot) → парсинг → ответ тула.
/// Гоняется ТОЛЬКО когда сервер (AppSettings.Endpoint) доступен И запущен с --slots 5
/// (есть слот пробы); иначе — skip (гейт зелёный).
/// [Collection("LiveProbes")] — общий с LlmProbeClientSelfTests: все live-пробы делят один
/// слот пробы и должны идти последовательно (xUnit параллелит разные классы).
/// </summary>
[Collection("LiveProbes")]
public sealed class IntuitionToolsLiveTests
{
    private static string Endpoint => AppSettings.Get().Endpoint.TrimEnd('/');

    private static bool ServerReachable()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.GetAsync(Endpoint + "/props").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Есть ли на сервере слот пробы (GET /slots). false — skip (сервер без --slots 5).</summary>
    private static bool ProbeSlotExists()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = http.GetAsync(Endpoint + "/slots").GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(
                response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("id", out var id) &&
                    id.GetInt32() == QwenPlayground.Core.Inference.SlotAllocation.Probe)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeAvailable() => ServerReachable() && ProbeSlotExists();

    private static ToolContext Context() => new(SelfBuildPaths.WorkspaceRoot);

    [Fact]
    public void Choice_Live_ReturnsChosenOptionWithConfidence()
    {
        if (!ProbeAvailable())
        {
            return;
        }
        var tool = new IntuitionChoiceTool
        {
            Question = "Which tool of this app is used to take screenshots of the screen?",
            Options = new[] { "web browser automation", "desktop mouse and keyboard", "C# code analysis" }
        };

        var result = tool.ExecuteAsync(Context(), CancellationToken.None).GetAwaiter().GetResult();

        Assert.StartsWith("Intuition (choice):", result);
        Assert.Contains("p=", result);
        Assert.Contains("Distribution", result);
        Assert.DoesNotContain("Probe failed", result);
    }

    [Fact]
    public void Rating_Live_ReturnsDigitWithDistribution()
    {
        if (!ProbeAvailable())
        {
            return;
        }
        var tool = new IntuitionRatingTool
        {
            Statement = "A unit test that never fails is still useful.",
            Scale = "0 = strongly disagree, 9 = strongly agree"
        };

        var result = tool.ExecuteAsync(Context(), CancellationToken.None).GetAwaiter().GetResult();

        Assert.StartsWith("Intuition (rating):", result);
        Assert.Contains("digit", result);
        Assert.Contains("Distribution", result);
        Assert.DoesNotContain("no clear digit", result);
        Assert.DoesNotContain("Probe failed", result);
    }

    [Fact]
    public void Vibe_Live_ReturnsEmojiSequenceAndDistribution()
    {
        if (!ProbeAvailable())
        {
            return;
        }
        var tool = new IntuitionVibeTool
        {
            Text = "The build failed at 3am because of a null pointer in the test suite."
        };

        var result = tool.ExecuteAsync(Context(), CancellationToken.None).GetAwaiter().GetResult();

        Assert.StartsWith("Intuition (vibe):", result);
        Assert.DoesNotContain("Probe failed", result);
        Assert.DoesNotContain("no single emoji tokens", result);
        // assets/server_vocab.json в workspace — декодирование окон доступно.
        Assert.Contains("Distribution (top 10)", result);
    }

    [Fact]
    public void Choice_Live_RejectsTooFewOptions()
    {
        var tool = new IntuitionChoiceTool
        {
            Question = "q",
            Options = new[] { "only one" }
        };

        var result = tool.ExecuteAsync(Context(), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Contains("at least 2 options", result);
    }
}
