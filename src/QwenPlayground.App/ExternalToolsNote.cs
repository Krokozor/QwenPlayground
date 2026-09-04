using System.IO;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.App;

/// <summary>
/// Секция «внешние инструменты» в системном промпте: содержимое external/README.md
/// (что скачано в external/ и как пользоваться). Часть cache-anchor — должна быть
/// стабильной. Кэшируется по mtime файла: правка md агентом/владельцем инвалидирует
/// кэш мгновенно, без ребилда. Файла нет → секции нет (null).
/// </summary>
public sealed class ExternalToolsNote
{
    private readonly string _filePath;
    private readonly FileDependentCache<string?> _cache;

    public ExternalToolsNote()
    {
        _filePath = Path.Combine(SelfBuildPaths.ExternalDir, "README.md");
        _cache = new FileDependentCache<string?>(new[] { _filePath }, Build, initial: null);
    }

    public string? Get() => _cache.Get();

    private string? Build()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }
        // Заголовок секции — собственный H1 файла («# External tools (external/)») — в стиле
        // «# Tools» эталонного шаблона; код заголовок не добавляет, чтобы не дублировать.
        var content = File.ReadAllText(_filePath).Trim();
        return content.Length == 0 ? null : content;
    }
}
