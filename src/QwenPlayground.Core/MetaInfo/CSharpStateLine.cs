using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Roslyn;

namespace QwenPlayground.Core.MetaInfo;

/// <summary>
/// csharp-строка state-блока (фаза 2, 2026-11-05, дизайн владельца): живое состояние
/// C#-трека живёт НЕ в системном промпте (строка полки статична — тулы не должны
/// инвалидировать KV-кеш), а в &lt;state&gt;: только при АКТИВНОЙ csharp-полке,
/// каждые 10 рендеров или при изменении трека.
///
/// Значение — CSharpProjects.DescribeTarget: трек чата («{file} (N projects)»),
/// home-преднастройка («home (QwenPlayground solution)») или «nothing tracked».
/// Счётчик проектов — из csharp.json (CSharpSessionTrack), НЕ из воркспейса
/// (холодная загрузка ради счётчика запрещена).
///
/// Паттерн TodoReminder: Peek — чистый (превью/подсчёт токенов не двигает счётчик),
/// OnRendered — только после РЕАЛЬНОГО рендера (TurnPipeline).
/// </summary>
public sealed class CSharpStateLine
{
    /// <summary>Период повторного показа неизменённой строки (рендеров).</summary>
    public const int Interval = 10;

    private readonly Func<string> _sessionDirectory;
    private readonly Func<string?> _root;
    private readonly Func<bool> _shelfActive;

    private string? _lastShown;
    private int _rendersSinceShown;

    public CSharpStateLine(Func<string> sessionDirectory, Func<string?> root, Func<bool> shelfActive)
    {
        _sessionDirectory = sessionDirectory;
        _root = root;
        _shelfActive = shelfActive;
    }

    /// <summary>Чистая проверка: какая строка (если нужна) попадает в блок сейчас.</summary>
    public string? Peek()
    {
        if (!_shelfActive())
        {
            return null;
        }
        var line = CSharpProjects.DescribeTarget(_root(), _sessionDirectory());
        if (line != _lastShown)
        {
            return line; // изменено (или впервые) — показываем сразу
        }
        return _rendersSinceShown >= Interval ? line : null;
    }

    /// <summary>После реального рендера (TurnPipeline): сдвиг счётчика.</summary>
    public void OnRendered()
    {
        if (!_shelfActive())
        {
            // Полка выключена: сброс, чтобы при повторном включении строка вышла сразу.
            _lastShown = null;
            _rendersSinceShown = 0;
            return;
        }
        _rendersSinceShown++;
        if (Peek() is not null)
        {
            _lastShown = CSharpProjects.DescribeTarget(_root(), _sessionDirectory());
            _rendersSinceShown = 0;
        }
    }
}
