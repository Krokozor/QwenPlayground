using QwenPlayground.Core.Probes;

namespace QwenPlayground.Core.Tests;

public sealed class LlmProbeClientTests
{
    // Реальный ответ llama.cpp (Gemma4-E4B) на ординальный проб «2+2=4».
    // Валидирован: python -m json.tool Sandbox/probe_sample.json.
    private const string SampleResponse = """
        {
          "choices": [
            {
              "finish_reason": "length",
              "index": 0,
              "message": { "role": "assistant", "content": "9" },
              "logprobs": {
                "content": [
                  {
                    "id": 236819,
                    "token": "9",
                    "bytes": [57],
                    "logprob": -0.6476725935935974,
                    "top_logprobs": [
                      { "id": 236819, "token": "9", "bytes": [57], "logprob": -0.6476725935935974 },
                      { "id": 100, "token": "<|channel>", "bytes": [60, 124, 99, 104, 97, 110, 110, 101, 108, 62], "logprob": -0.7417793273925781 },
                      { "id": 236770, "token": "1", "bytes": [49], "logprob": -8.024089813232422 }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ArgmaxAndTokens()
    {
        var result = LlmProbeClient.ParseProbeResponse(SampleResponse);

        Assert.Equal("9", result.ArgmaxToken);
        Assert.Equal(3, result.TopTokens.Count);
        Assert.Equal(-0.6476725935935974, result.ArgmaxLogProb, 10);
    }

    [Fact]
    public void Parse_Entropy_LowForPeakedDistribution()
    {
        var result = LlmProbeClient.ParseProbeResponse(SampleResponse);

        Assert.True(result.Entropy < 1.0, $"entropy={result.Entropy}");
    }

    [Fact]
    public void Parse_Entropy_HigherForFlatDistribution()
    {
        // Валидирован: python -m json.tool Sandbox/probe_flat.json.
        const string flat = """
            {
              "choices": [
                {
                  "logprobs": {
                    "content": [
                      {
                        "top_logprobs": [
                          { "token": "a", "logprob": -1.0 },
                          { "token": "b", "logprob": -1.1 },
                          { "token": "c", "logprob": -1.2 }
                        ]
                      }
                    ]
                  }
                }
              ]
            }
            """;

        var flatResult = LlmProbeClient.ParseProbeResponse(flat);
        var peaked = LlmProbeClient.ParseProbeResponse(SampleResponse);

        Assert.True(flatResult.Entropy > peaked.Entropy,
            $"flat={flatResult.Entropy} должно быть больше peaked={peaked.Entropy}");
    }

    [Fact]
    public void Parse_EmptyLogprobs_Throws()
    {
        const string empty = """{"choices":[{"logprobs":{"content":[]}}]}""";

        Assert.Throws<InvalidDataException>(() => LlmProbeClient.ParseProbeResponse(empty));
    }

    [Fact]
    public void Parse_Positions_ReturnsAllGeneratedTokens()
    {
        const string multi = """
            {
              "choices": [
                {
                  "logprobs": {
                    "content": [
                      { "token": "A", "top_logprobs": [ { "token": "A", "logprob": -0.2 }, { "token": "B", "logprob": -1.8 } ] },
                      { "token": "B", "top_logprobs": [ { "token": "B", "logprob": -0.5 }, { "token": "C", "logprob": -2.0 } ] }
                    ]
                  }
                }
              ]
            }
            """;

        var positions = LlmProbeClient.ParseProbePositions(multi);

        Assert.Equal(2, positions.Count);
        Assert.Equal("A", positions[0].ArgmaxToken);
        Assert.Equal("B", positions[1].ArgmaxToken);
    }

    // Реальный фрагмент ответа нативного /completion (llama.cpp n_probs) на классификацию
    // категорий: первая позиция — распределение ПЕРЕД генерацией (content пустой, argmax "A"),
    // дальше сгенерированные токены " I", " X".
    private const string SampleNativeResponse = """
        {
          "content": "A I X",
          "completion_probabilities": [
            {
              "content": "",
              "top_logprobs": [
                { "id": 236776, "token": "A", "bytes": [65], "logprob": -0.026127358704805374 },
                { "id": 236786, "token": "AD", "bytes": [65, 68], "logprob": -4.657983779907227 },
                { "id": 236789, "token": "AG", "bytes": [65, 71], "logprob": -4.908079147338867 },
                { "id": 236821, "token": "AS", "bytes": [65, 83], "logprob": -5.24800443649292 }
              ]
            },
            {
              "content": " I",
              "top_logprobs": [
                { "id": 236821, "token": " I", "bytes": [32, 73], "logprob": -1.231771469116211 },
                { "id": 236819, "token": " S", "bytes": [32, 83], "logprob": -1.3254172801971436 },
                { "id": 236845, "token": " S", "bytes": [32, 84], "logprob": -2.3941550254821777 }
              ]
            },
            {
              "content": " X",
              "top_logprobs": [
                { "id": 236898, "token": " X", "bytes": [32, 88], "logprob": -0.40876391530036926 },
                { "id": 236822, "token": " D", "bytes": [32, 68], "logprob": -2.8151357173919678 }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ParseNative_ReturnsPositionsWithLeadingSpaceTokens()
    {
        var positions = LlmProbeClient.ParseNativeProbePositions(SampleNativeResponse);

        Assert.Equal(3, positions.Count);
        Assert.Equal("A", positions[0].ArgmaxToken);
        Assert.Equal(" I", positions[1].ArgmaxToken);
        Assert.Equal(" X", positions[2].ArgmaxToken);
    }

    [Fact]
    public void ParseNative_MissingProbabilities_Throws()
    {
        Assert.Throws<InvalidDataException>(() => LlmProbeClient.ParseNativeProbePositions("{}"));
        Assert.Throws<InvalidDataException>(() =>
            LlmProbeClient.ParseNativeProbePositions("""{"completion_probabilities":[]}"""));
    }
}
