using System.Text.Json;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Tokenizers;

/// <summary>
/// Декодер id токенов модели сервера: id → строка (byte-fallback GPT-2).
/// Источник — assets/server_vocab.json: массив tokenizer.ggml.tokens, извлечённый из GGUF
/// модели (test_probe19.py). ВНИМАНИЕ: assets/tokenizer.json — ДРУГОЙ токенизатор
/// (id не совпадают: там id 98 = «¥»-фрагмент другого словаря, эмодзи-токенов нет) —
/// не использовать.
/// Зачем: в этом билде llama.cpp-сервера строка «token» для не-ASCII в окне top_logprobs
/// разрывается (U+FFFD), а «id» цел — id + vocab дают кандидата окна обратно.
/// Best-effort: файла нет / сломан — методы возвращают null, тулы деградируют до
/// позиционного уровня.
/// </summary>
public static class QwenVocabDecoder
{
    private static Dictionary<int, string>? _byId;
    private static bool _loaded;

    /// <summary>Словарь загружен (файл найден и распарсен).</summary>
    public static bool IsLoaded => _byId is not null && _byId.Count > 0;

    private static Dictionary<int, string> EnsureLoaded()
    {
        if (_loaded)
        {
            return _byId ?? new Dictionary<int, string>();
        }
        _loaded = true;
        _byId = new Dictionary<int, string>();
        try
        {
            var path = Path.Combine(SelfBuildPaths.WorkspaceRoot, "assets", "server_vocab.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Name, out var id))
                {
                    _byId[id] = prop.Value.GetString()!;
                }
            }
        }
        catch
        {
            // best-effort: нет файла / сломан JSON — словарь пуст, декодирование выключено
        }
        return _byId;
    }

    /// <summary>id → строка из vocab (byte-fallback-символы или целые для merged-токенов), null — неизвестен.</summary>
    public static string? Get(int id) => EnsureLoaded().GetValueOrDefault(id);

    /// <summary>
    /// Для байт-токенов (ровно один символ byte-fallback) — значение байта; иначе null
    /// (merged-токен, спец-токен, неизвестный id).
    /// </summary>
    public static int? ByteValue(int id)
    {
        var s = Get(id);
        if (s is null || s.Length != 1)
        {
            return null;
        }
        return ByteFallbackToByte(s[0]);
    }

    /// <summary>
    /// Обратное отображение byte-fallback ТОКЕНИЗАТОРА ЭТОЙ МОДЕЛИ (схема выведена
    /// напрямую из vocab GGUF — test_probe24.py, НЕ стандарт GPT-2!):
    /// 0x21-0x7E и 0xA1-0xFF — сами себе (пробел 0x20 и 0xA0/0xAD — НЕТ);
    /// оставшиеся 68 байтов в порядке возрастания [0x00..0x20, 0x7F, 0x80..0x9F, 0xA0, 0xAD]
    /// — U+0100..U+0143. Проверено по живым пробам: U+010A→0x0A, U+0133→0x91,
    /// U+013B→0x99, U+013D→0x9B (последние байты сгенерированных эмодзи).
    /// Символ вне схемы → null.
    /// </summary>
    public static int? ByteFallbackToByte(char c)
    {
        if (c is >= '\u0021' and <= '\u007E' or >= '\u00A1' and <= '\u00FF')
        {
            return (int)c;
        }
        if (c is >= '\u0100' and <= '\u0143')
        {
            var i = (int)c - 0x100; // 0..67
            if (i < 33)
            {
                return i; // 0x00..0x20
            }
            if (i == 33)
            {
                return 0x7F;
            }
            if (i < 66)
            {
                return i - 34 + 0x80; // 0x80..0x9F
            }
            if (i == 66)
            {
                return 0xA0;
            }
            return 0xAD; // i == 67
        }
        return null;
    }
}
