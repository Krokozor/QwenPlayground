using System.Globalization;
using System.Text;

namespace QwenPlayground.Core.MetaInfo;

/// <summary>
/// State-блок: open-close блок служебной информации, который приложение вставляет
/// в начало think-блока КАЖДОГО assistant-сообщения (и префиллит в генерацию).
/// Это снапшот статуса на момент генерации: msg_id, время, контекст cur/max, сборка,
/// nag — всё, что является «системной информацией на данный момент».
///
/// Блок ПЕРСИСТИРУЕТСЯ с сообщением: AgentLoop пришивает префилленный блок в
/// message.StateBlock (ответ модели его не содержит), шаблон рендерит его в начале
/// think каждого прошлого хода. Модель видит по каждому сообщению статус и как он
/// МЕНЯЕТСЯ (рост контекста, смена билда).
///
/// Формат — одна строка на поле, name=value, поле с пустым значением пропускается:
///   <state>
///   msg_id=2
///   time=2026-08-17 13:15:26
///   context=12345/32768
///   build=20260817-102607:success
///   </state>
/// Класс — единственный владелец блока: сборка (объект + WithNag), собственный рендер
/// (ToString), разбор (Parse), извлечение из текста (SplitLeading). Старые сообщения
/// без блока — не рендерится.
/// </summary>
public sealed class StateBlock
{
    public const string Open = "<state>";
    public const string Close = "</state>";

    /// <summary>Стабильный ID сообщения (тот же, что получит сообщение при добавлении в разговор).</summary>
    public int? MsgId { get; set; }

    /// <summary>Момент сборки блока.</summary>
    public DateTime? Time { get; set; }

    /// <summary>Использованные токены контекста на момент генерации.</summary>
    public int? ContextUsed { get; set; }

    /// <summary>Максимальный размер контекста (лимит окна).</summary>
    public int? ContextMax { get; set; }

    /// <summary>ID сборки (например, 20260817-102607).</summary>
    public string? BuildId { get; set; }

    /// <summary>Статус сборки (success / pending / ...).</summary>
    public string? BuildStatus { get; set; }

    /// <summary>Всплывшие воспоминания (ассоциативный реколл).</summary>
    public List<MemoryRef> Memories { get; set; } = new();

    /// <summary>Пары воспоминаний, которые надмозг счёл похожими: модель мерджит или разводит.</summary>
    public List<MemoryPair> SimilarPairs { get; set; } = new();

    /// <summary>Периодическое напоминание про менеджмент памяти (mem_nag).</summary>
    public string? MemoryNag { get; set; }

    /// <summary>Напоминание про sanity_check при долгой работе без самопроверки (nag).</summary>
    public string? Nag { get; set; }

    public sealed record MemoryPair(string A, string B);

    public sealed class MemoryRef
    {
        public string? Id { get; set; }
        public double? Relevance { get; set; }
        public string? Content { get; set; }

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{Id} | relevance ~{Relevance:0.00} | {Content}");
    }

    /// <summary>
    /// Собственный рендер блока: одна строка на поле (name=value), порядок фиксированный.
    /// Строковое представление = «каноничный» формат, который видит модель.
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder(128);
        builder.Append(Open).Append('\n');

        var first = true;
        void AppendField(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            // Текстовые поля однострочные по формату (парс режет блок на строки):
            // перенос внутри значения превращает хвост в мусорные поля.
            var safe = value.Replace("\r\n", " ").Replace('\n', ' ');
            if (!first)
            {
                builder.Append('\n');
            }
            builder.Append(name).Append('=').Append(safe);
            first = false;
        }

        AppendField("msg_id", MsgId?.ToString());
        AppendField("time", Time?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        // Пишем только парные значения: «context=/32768» собственный парс прочитать не может.
        AppendField("context",
            ContextUsed is null || ContextMax is null ? null : $"{ContextUsed}/{ContextMax}");
        AppendField("build", BuildId is null && BuildStatus is null ? null : $"{BuildId}:{BuildStatus}");
        foreach (var memory in Memories)
        {
            // Без Id/Relevance строка не разбирается ParseMemory — не рендерим её вовсе,
            // чтобы битая mem-строка не попала в блок.
            if (memory.Id is null || memory.Relevance is null)
            {
                continue;
            }
            AppendField("mem", memory.ToString());
        }
        foreach (var pair in SimilarPairs)
        {
            AppendField("pair", $"{pair.A} ~ {pair.B}");
        }
        AppendField("mem_nag", MemoryNag);
        AppendField("nag", Nag);

        if (!first)
        {
            builder.Append('\n');
        }
        builder.Append(Close);
        return builder.ToString();
    }

    /// <summary>
    /// Разбирает блок в объект. Принимает как полный блок с тегами, так и строку
    /// из SplitLeading (без тегов — нет, парсер ожидает именно блок). Не распарсилось
    /// ни одного поля — возвращает null.
    /// </summary>
    public static StateBlock? Parse(string? block)
    {
        if (string.IsNullOrEmpty(block))
        {
            return null;
        }
        var (parsed, _) = SplitLeading(block);
        return parsed;
    }

    /// <summary>
    /// Добавляет nag-напоминание в блок. Блок мутируется и возвращается; если блока нет —
    /// создаёт новый только с этим полем.
    /// </summary>
    public static StateBlock WithNag(StateBlock? block, string nag)
    {
        if (block is null)
        {
            block = new StateBlock();
        }
        block.Nag = nag;
        return block;
    }

    /// <summary>
    /// Выделяет state-блок из начала текста (если он там есть). Возвращает
    /// (объект блока, остаток). Если блока нет — (null, исходный текст). Используется
    /// парсером на случай, если модель всё же повторила блок в ответе, и UI — чтобы
    /// показать блок своим цветом, отдельно от мыслей модели.
    /// </summary>
    public static (StateBlock? block, string tail) SplitLeading(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (null, text ?? string.Empty);
        }
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith(Open, StringComparison.Ordinal))
        {
            return (null, text);
        }
        var end = trimmed.IndexOf(Close, Open.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return (null, text);
        }
        var block = trimmed[..(end + Close.Length)];
        var rest = trimmed[(end + Close.Length)..].TrimStart();
        return (FromBlock(block), rest);
    }

    /// <summary>Разбирает блок (с тегами) в объект. Не распарсилось — null.</summary>
    private static StateBlock? FromBlock(string block)
    {
        var state = new StateBlock();
        var any = false;
        foreach (var line in block[Open.Length..^Close.Length].Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            var name = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim();
            switch (name)
            {
                case "msg_id":
                    if (int.TryParse(value, out var msgId))
                    {
                        state.MsgId = msgId;
                        any = true;
                    }
                    break;
                case "time":
                    if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    {
                        state.Time = time;
                        any = true;
                    }
                    break;
                case "context":
                    var parts = value.Split('/');
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], out var used) &&
                        int.TryParse(parts[1], out var max))
                    {
                        state.ContextUsed = used;
                        state.ContextMax = max;
                        any = true;
                    }
                    break;
                case "build":
                    var colon = value.IndexOf(':');
                    if (colon >= 0)
                    {
                        state.BuildId = value[..colon];
                        state.BuildStatus = value[(colon + 1)..];
                    }
                    else
                    {
                        state.BuildId = value;
                    }
                    any = true;
                    break;
                case "mem":
                    var mem = ParseMemory(value);
                    if (mem is not null)
                    {
                        state.Memories.Add(mem);
                        any = true;
                    }
                    break;
                case "pair":
                    var ids = value.Split("~");
                    if (ids.Length == 2 && ids[0].Trim().Length > 0 && ids[1].Trim().Length > 0)
                    {
                        state.SimilarPairs.Add(new MemoryPair(ids[0].Trim(), ids[1].Trim()));
                        any = true;
                    }
                    break;
                case "mem_nag":
                    state.MemoryNag = value;
                    any = true;
                    break;
                case "nag":
                    state.Nag = value;
                    any = true;
                    break;
            }
        }
        return any ? state : null;
    }

    private static MemoryRef? ParseMemory(string value)
    {
        // Формат: {Id} | relevance ~{Score:0.00} | {Content}
        var pipe = value.IndexOf(" | relevance ~", StringComparison.Ordinal);
        if (pipe < 0)
        {
            return null;
        }
        var relevanceStart = pipe + " | relevance ~".Length;
        var relevanceEnd = value.IndexOf(" | ", relevanceStart, StringComparison.Ordinal);
        if (relevanceEnd < 0 || !double.TryParse(value[relevanceStart..relevanceEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out var relevance))
        {
            return null;
        }
        return new MemoryRef
        {
            Id = value[..pipe],
            Relevance = relevance,
            Content = value[(relevanceEnd + 3)..]
        };
    }
}
