using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.Core.Agent;

/// <summary>
/// Loop-guard (дизайн владельца, v1 2026-11-05, v2 2026-11-06): хеш распарсенного
/// тул-вызова (имя + канонические аргументы) для дедупликации в пределах хода.
///
/// Луп-инцидент 2026-11-05: модель повторила один и тот же edit_file ~60 раз в одном
/// ходе (каждый виток — повторный вызов + повторный вывод в контекст) → переполнение.
/// v2 (2026-11-06): дубликаты вырезаются из assistant-сообщения ДО того, как оно
/// попадает в разговор (RemoveDuplicates) — в контекст они не попадают вовсе (нет
/// маркеров, нет грязного вывода, нет tool-сообщений: каждый оставшийся вызов имеет
/// ровно один ответ — шаблон well-formed). Ловит не только «N одинаковых подряд», а
/// чередующиеся секции [1][2][3][д2][д3][д2]: хеш хранится в per-turn HashSet
/// (AgentLoop), первое вхождение остаётся, все последующие вырезаются.
/// </summary>
public static class LoopGuard
{
    /// <summary>
    /// SHA256 hex от имени вызова + аргументов с отсортированными (Ordinal) ключами.
    /// Вложенные значения — ToJsonString как есть (модель повторяет собственный вывод
    /// с тем же вложенным порядком — нормализация глубины не нужна).
    /// </summary>
    public static string Hash(string name, JsonObject? args)
    {
        var canonical = new StringBuilder(name);
        if (args is { Count: > 0 })
        {
            // Перебор как IDictionary (JsonObject реализует его; .Properties в этом SDK
            // не резолвится — не используем). Ключи сортируются Ordinal — каноническая
            // форма не зависит от порядка, в котором модель выдала аргументы.
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var (key, value) in args)
            {
                pairs.Add(new KeyValuePair<string, string>(key, value?.ToJsonString() ?? "null"));
            }
            pairs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            canonical.Append('\n');
            for (var i = 0; i < pairs.Count; i++)
            {
                if (i > 0)
                {
                    canonical.Append(',');
                }
                canonical.Append(pairs[i].Key).Append(':').Append(pairs[i].Value);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>
    /// v2 (2026-11-06): вырезает дубликаты из списка вызовов assistant-сообщения до
    /// попадания в разговор. Первое вхождение остаётся, последующие вырезаются — и в
    /// пределах сообщения, и в пределах хода (seen-набор ведёт вызывающий, AgentLoop).
    /// Возвращает список оставшихся (null — если вырезано всё) или исходный список
    /// без изменений (ReferenceEquals — вызывающий отличает по ссылке).
    /// </summary>
    public static IReadOnlyList<ToolCall>? RemoveDuplicates(IReadOnlyList<ToolCall>? calls, HashSet<string> seen)
    {
        if (calls is not { Count: > 0 })
        {
            return calls;
        }
        var kept = new List<ToolCall>(calls.Count);
        foreach (var call in calls)
        {
            if (seen.Add(Hash(call.Name, call.Arguments as JsonObject)))
            {
                kept.Add(call);
            }
        }
        return kept.Count == calls.Count ? calls : kept.Count > 0 ? kept : null;
    }
}
