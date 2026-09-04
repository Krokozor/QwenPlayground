using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Tokenizers;

// МЁРТВЫЙ КОД (аудит 2026-09-04): ни одного вызова в проекте (Count/IsAvailable не
// используются). Держим как фундамент: если понадобится точный подсчёт токенов «на
// клиенте» — подключить сюда. Кандидат на удаление при чистке.
/// <summary>
/// Локальный счётчик токенов на BPE-токенизаторе модели (assets/tokenizer.json).
/// Даёт фактическое количество токенов «по щелчку пальцев» — без раундтрипа к
/// llama.cpp-серверу, что упрощает многопоточность (нет HTTP, нет ожидания).
///
/// Токенизатор грузится лениво один раз и кэшируется. Count — под общим замком
/// (внутри BPETokenizer есть мутабельный _cache), поэтому вызовы тред-сейфны.
/// Если файл токенизатора недоступен — fallback на грубую оценку chars/4.
/// </summary>
public static class TokenCounter
{
    private static readonly object Lock = new();
    private static BPETokenizer? _tokenizer;
    private static bool _loadFailed;

    /// <summary>Фактическое число токенов в тексте (или оценка, если токенизатор не загрузился).</summary>
    public static int Count(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        if (!TryGetTokenizer(out var tokenizer))
        {
            return Estimate(text);
        }
        lock (Lock)
        {
            return tokenizer.CountTokens(text);
        }
    }

    /// <summary>Загружен ли реальный токенизатор (false → работаем на оценке).</summary>
    public static bool IsAvailable => TryGetTokenizer(out _);

    private static bool TryGetTokenizer(out BPETokenizer tokenizer)
    {
        if (_tokenizer is not null)
        {
            tokenizer = _tokenizer;
            return true;
        }
        if (_loadFailed)
        {
            tokenizer = null!;
            return false;
        }
        lock (Lock)
        {
            if (_tokenizer is not null)
            {
                tokenizer = _tokenizer;
                return true;
            }
            if (_loadFailed)
            {
                tokenizer = null!;
                return false;
            }
            try
            {
                var path = Path.Combine(SelfBuildPaths.WorkspaceRoot, "assets", "tokenizer.json");
                _tokenizer = BPETokenizer.FromFile(path);
                tokenizer = _tokenizer;
                return true;
            }
            catch
            {
                _loadFailed = true;
                tokenizer = null!;
                return false;
            }
        }
    }

    /// <summary>Оценка chars/4 — та же, что ContextCompactor.EstimateTokens использует.</summary>
    private static int Estimate(string text) => text.Length / 4;
}
