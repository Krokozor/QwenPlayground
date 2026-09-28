using System.IO;
using System.Text;
using QwenPlayground.Core.Serialization;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Agent;

/// <summary>
/// Секция «внешние инструменты» в системном промпте: собирается из README каждого
/// подкаталога external/. Инструменты опциональны — их ставит лаунчер (кнопка «Скачать»),
/// и агент обязан работать без них.
///
/// Правило простое и не знает ни про один конкретный инструмент: есть каталог с README —
/// есть секция, нет каталога — нет секции. Чтобы добавить инструмент, достаточно
/// положить external/&lt;имя&gt;/README.md; лаунчер кладёт рядом бинарники, агент сам
/// находит новую папку (кэш инвалидируется по составу зависимостей).
///
/// Часть cache-anchor — должна быть стабильной: порядок секций по имени каталога,
/// один и тот же H2-заголовок на инструмент. Кэшируется по mtime README (правка агентом
/// инвалидирует мгновенно, без ребилда). Ни одного файла нет — секции нет (null).
/// </summary>
public sealed class ExternalToolsNote
{
    /// <summary>Заголовок секции. Стабильная часть cache-anchor — не переименовывать без нужды.</summary>
    private const string SectionHeader = "# External tools (external/)";

    private readonly string _externalDir;
    private readonly FileDependentCache<string?> _cache;

    public ExternalToolsNote()
        : this(null)
    {
    }

    /// <summary>externalDir — корень с каталогами инструментов; null — autodetect (SelfBuildPaths).</summary>
    public ExternalToolsNote(string? externalDir)
    {
        _externalDir = externalDir ?? SelfBuildPaths.ExternalDir;
        _cache = new FileDependentCache<string?>(DocPaths, Build, initial: null);
    }

    public string? Get() => _cache.Get();

    /// <summary>
    /// README всех прямых подкаталогов external/ — по одному на инструмент, в порядке
    /// сортировки имён каталогов. Каталога без README не существует для промпта.
    /// Не итератор: перечисление обёрнуто в try — yield внутри try с catch недопустим.
    /// </summary>
    private IReadOnlyList<string> DocPaths()
    {
        try
        {
            if (!Directory.Exists(_externalDir))
            {
                return Array.Empty<string>();
            }
            return Directory.EnumerateDirectories(_externalDir)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(dir => Path.Combine(dir, SelfBuildPaths.ExternalDocsFileName))
                .Where(File.Exists)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Каталог external/ недоступен (нет прав, сетевой диск) — секции нет, но агент
            // продолжает работать: инструменты опциональны по определению.
            return Array.Empty<string>();
        }
    }

    private string? Build()
    {
        var sections = new List<string>();
        foreach (var path in DocPaths())
        {
            string content;
            try
            {
                content = File.ReadAllText(path).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // README пропал или заперт между перечислением и чтением — пропускаем.
                continue;
            }
            if (content.Length == 0)
            {
                continue;
            }
            var name = Path.GetFileName(Path.GetDirectoryName(path)) ?? "tool";
            sections.Add($"## {name}\n{DemoteHeadings(content, name)}");
        }

        if (sections.Count == 0)
        {
            return null;
        }
        return SectionHeader + "\n\n" + string.Join("\n\n", sections);
    }

    /// <summary>
    /// Содержимое README вставляется под общий H1 секции и заголовок инструмента (H2),
    /// поэтому собственные заголовки файла сдвигаются на уровень глубже: иначе «## Approach»
    /// внутри ffmpeg оказался бы соседом «## ffmpeg», а не подразделом. Собственный H1 файла
    /// (если он совпадает с именем каталога) выбрасывается — его роль уже играет H2 секции.
    /// Строки внутри ```-блоков не трогаются: «#» в примере кода заголовком не является.
    /// </summary>
    private static string DemoteHeadings(string content, string toolName)
    {
        var lines = content.Split('\n');
        var firstContentLine = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (firstContentLine < 0)
        {
            return content;
        }

        var sb = new StringBuilder(content.Length + 32);
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                sb.Append(line).Append('\n');
                continue;
            }
            if (!inFence && trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                var text = trimmed[level..].Trim();
                if (i == firstContentLine && level == 1 && text.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // заголовок инструмента уже отдан секции как H2
                }
                // h6 глубже некуда — оставляем как есть, лишь бы не потерять текст.
                sb.Append(level >= 6 ? trimmed : "#" + trimmed).Append('\n');
                continue;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }
}
