using System.Text;

namespace QwenPlayground.Core.Templates;

/// <summary>
/// Инкрементальное разделение живого стрима токенов на reasoning/content по закрывающему
/// think-маркеру. Дополняет <see cref="QwenOutputParser"/> (тот разбирает полный текст
/// постфактум): здесь сканируется только новый чанк — O(длина чанка), а не O(весь вывод),
/// что и требуется хот-путу UI при десятках чанков в секунду.
///
/// Маркер может разорваться между чанками («…&lt;/thi» + «nk&gt;…»): хвост длиной до
/// маркера-1 держится неразрешённым до следующего чанка. Семантика совпадает с парсером:
/// ведущие \n после маркера в контент не входят; повторный маркер внутри контента —
/// обычный текст.
/// </summary>
public sealed class ThinkStreamSplitter
{
    private const string ThinkClose = "</think>";

    private readonly StringBuilder _reasoning = new();
    private readonly StringBuilder _content = new();
    private string _tail = string.Empty;
    private bool _closed;

    /// <summary>Закрылся ли think-блок (после этого всё идёт в контент).</summary>
    public bool ThinkClosed => _closed;

    /// <summary>Накопленная мысль (триммированная, без маркеров — как у парсера полного текста).</summary>
    public string Reasoning => _reasoning.ToString().Trim();

    /// <summary>Накопленный контент после маркера (пуст, пока think не закрыт).</summary>
    public string Content => _content.ToString();

    public void Reset()
    {
        _reasoning.Clear();
        _content.Clear();
        _tail = string.Empty;
        _closed = false;
    }

    /// <summary>Уже накопленный вывод (prefill continue-хода) прогоняется общим путём.</summary>
    public void AppendPrefill(string prefill)
    {
        if (!string.IsNullOrEmpty(prefill))
        {
            Append(prefill);
        }
    }

    /// <summary>
    /// Разрешает отложенный хвост (возможное начало маркера) как обычный текст. Только
    /// в конце потока: вызов посреди стрима лишил бы следующий чанк шанса закрыть маркер.
    /// </summary>
    public void Flush()
    {
        if (_closed || _tail.Length == 0)
        {
            return;
        }
        _reasoning.Append(_tail);
        _tail = string.Empty;
    }

    public void Append(string chunk)
    {
        if (_closed)
        {
            _content.Append(chunk);
            return;
        }

        var combined = _tail + chunk;
        var index = combined.IndexOf(ThinkClose, StringComparison.Ordinal);
        if (index >= 0)
        {
            _reasoning.Append(combined, 0, index);
            _content.Append(combined[(index + ThinkClose.Length)..].TrimStart('\n'));
            _tail = string.Empty;
            _closed = true;
            return;
        }

        var keep = Math.Min(combined.Length, ThinkClose.Length - 1);
        var safe = combined.Length - keep;
        if (safe > 0)
        {
            _reasoning.Append(combined, 0, safe);
            _tail = combined[safe..];
        }
        else
        {
            _tail = combined;
        }
    }
}
