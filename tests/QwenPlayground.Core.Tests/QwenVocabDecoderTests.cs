using QwenPlayground.Core.Tokenizers;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Чистые тесты byte-fallback декодера. Схема — НЕ стандарт GPT-2, а схема токенизатора
/// этой модели (выведена из vocab GGUF): 0x21-0x7E и 0xA1-0xFF сами себе, оставшиеся 68
/// байтов [0x00..0x20, 0x7F, 0x80..0xA0, 0xAD] → U+0100..U+0143.
/// </summary>
public sealed class QwenVocabDecoderTests
{
    [Theory]
    [InlineData('!', 0x21)]
    [InlineData('~', 0x7E)]
    [InlineData('¡', 0xA1)]
    [InlineData('¥', 0xA5)]
    [InlineData('ÿ', 0xFF)]
    public void PrintableRangesMapToThemselves(char c, int expected)
    {
        Assert.Equal(expected, QwenVocabDecoder.ByteFallbackToByte(c));
    }

    [Theory]
    // сегмент 0x00..0x20
    [InlineData('\u0100', 0x00)]
    [InlineData('\u010A', 0x0A)] // проверено живой пробой: id 198 → «\n»
    [InlineData('\u0120', 0x20)] // пробел — в PUA-сегменте (id 220)
    // 0x7F
    [InlineData('\u0121', 0x7F)]
    // сегмент 0x80..0x9F (проверено живыми пробами эмодзи: последние байты)
    [InlineData('\u0133', 0x91)] // 🛑
    [InlineData('\u013B', 0x99)] // 🌙
    [InlineData('\u013D', 0x9B)] // 🐛
    [InlineData('\u0140', 0x9E)]
    [InlineData('\u0141', 0x9F)]
    // 0xA0 и 0xAD — тоже в PUA (не self-map)
    [InlineData('\u0142', 0xA0)]
    [InlineData('\u0143', 0xAD)]
    public void RemainingBytesMapInAscendingOrder(char c, int expected)
    {
        Assert.Equal(expected, QwenVocabDecoder.ByteFallbackToByte(c));
    }

    [Theory]
    [InlineData(' ')] // U+0020 — НЕ self-map в этой схеме (пробел = U+0120)
    [InlineData('\u00A0')] // NBSP — не self-map
    [InlineData('\u0144')] // за пределами PUA-схемы
    [InlineData('\u4E2D')] // CJK — не byte-fallback
    public void NonFallbackCharsReturnNull(char c)
    {
        Assert.Null(QwenVocabDecoder.ByteFallbackToByte(c));
    }

    [Fact]
    public void SelfMappedCharsStillResolve()
    {
        Assert.Equal(0x41, QwenVocabDecoder.ByteFallbackToByte('A'));
    }
}
