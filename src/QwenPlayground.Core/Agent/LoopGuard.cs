using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace QwenPlayground.Core.Agent;

/// <summary>
/// Loop-guard (дизайн владельца, 2026-11-05): хеш распарсенного тул-вызова
/// (имя + канонические аргументы) для дедупликации в пределах хода.
///
/// Луп-инцидент 2026-11-05: модель повторила один и тот же edit_file ~60 раз в одном
/// ходе (каждый виток — повторный вызов + повторный вывод в контекст) → переполнение.
/// Guard ловит не только «N одинаковых подряд», а чередующиеся секции
/// [1][2][3][д2][д3][д2]: хеш хранится в per-turn HashSet (AgentLoop), первое
/// вхождение выполняется, все последующие дубликаты режутся (не выполняются,
/// вместо результата — однострочный маркер).
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
}
