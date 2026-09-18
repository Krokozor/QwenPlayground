using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.App.Desktop;
using QwenPlayground.App.Tools;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Меню полок в тулбаре чата (кнопка 🗄 + Popup): состояние полок, переключение и реакция
/// на доменное событие ShelfState.Deactivated. Тот же механизм, что и у тулов агента:
/// состояние — sessions/&lt;id&gt;/shelves.json, активация немедленная, деактивация staged
/// (снимется при ближайшей естественной смене промпта). Каталог сессии приходит функцией —
/// класс не знает ни о MainViewModel, ни о хранилище сессий.
/// </summary>
public sealed partial class ShelfUiViewModel : ObservableObject {
    private readonly Func<string> _sessionDir;

    private readonly ShelfUiState[] _shelfUi =
    {
        new(ToolGroup.Browser, "WebView2-браузер: навигация, клики, ввод текста, скриншоты, JS, консоль и сетевые логи"),
        new(ToolGroup.CSharp, "Анализ кода Roslyn: символы, ссылки, диагностика, outline, class map"),
        new(ToolGroup.Desktop, "Рабочий стол: мышь, клавиатура, скриншоты, окна"),
        new(ToolGroup.Mcp, "Инструменты управления MCP (mcp_status, mcp_reload) и тулы подключённых MCP-серверов"),
    };

    public ShelfUiState BrowserShelf => _shelfUi[0];
    public ShelfUiState CSharpShelf => _shelfUi[1];
    public ShelfUiState DesktopShelf => _shelfUi[2];
    public ShelfUiState McpShelf => _shelfUi[3];

    private int _shelfCount;
    /// <summary>Сколько полок реально в промпте (on + pending) — счётчик на кнопке «🗄 N».</summary>
    public int ShelfCount {
        get => _shelfCount;
        private set {
            if (_shelfCount == value) return;
            _shelfCount = value;
            OnPropertyChanged(nameof(ShelfCount));
            OnPropertyChanged(nameof(HasActiveShelves));
        }
    }
    public bool HasActiveShelves => ShelfCount > 0;

    public ShelfUiViewModel(Func<string> sessionDir) {
        _sessionDir = sessionDir;
    }

    /// <summary>
    /// Переключить полку из UI-меню — тот же вход, что и у тулов агента (ShelfState.Activate/
    /// Deactivate): отметка → немедленная активация (отменяет pending), снятие → staged-
    /// деактивация. Направление — по состоянию чекбокса («on» = активна И не помечена):
    /// on → помечаем к снятию; off/pending → активируем (pending-группа всё ещё в active,
    /// смотреть только на active нельзя — иначе повторный клик по pending снова пометит её).
    /// </summary>
    [RelayCommand]
    private void ToggleShelf(string? group) {
        if (!ActivateShelfTool.TryParseGroup(group ?? string.Empty, out var g))
            return;
        var state = new ShelfState(_sessionDir());
        var isOn = state.Load().Contains(g) && !state.LoadPending().Contains(g);
        var result = isOn ? state.Deactivate(g) : state.Activate(g);
        Debug.WriteLine($"[shelf-cache] UI: {g} → {result}");
        Refresh();
    }

    /// <summary>
    /// Полка снята (staged-деактивация) — реакция UI на доменное событие ShelfState.Deactivated:
    /// desktop-полка текущей сессии ушла → скрываем оверлей курсора (пользователь закончил
    /// управление десктопом). Оба вызывающих места (тул агента и меню) идут через событие.
    /// Потоки: и тул (инвариант — agent-код на UI-потоке), и меню — UI-поток.
    /// </summary>
    public void OnDeactivated(ToolGroup group, string sessionDir) {
        if (group == ToolGroup.Desktop && sessionDir == _sessionDir()) {
            DesktopOverlay.Hide();
        }
    }

    /// <summary>
    /// Синхронизировать состояние меню полок с shelves.json текущей сессии. Dispatcher-safe:
    /// вызывается из UI (смена сессии, переключение, открытие меню) и из agent loop
    /// (на каждый запрос — подхватывает переключения тулами агента).
    /// </summary>
    public void Refresh() {
        void Do() {
            var shelf = new ShelfState(_sessionDir());
            var active = shelf.Load();
            var pending = shelf.LoadPending();
            var count = 0;
            foreach (var s in _shelfUi) {
                var inPrompt = active.Contains(s.Group) || pending.Contains(s.Group);
                s.Refresh(active.Contains(s.Group), pending.Contains(s.Group));
                if (inPrompt) count++;
            }
            ShelfCount = count;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Do);
        else
            Do();
    }
}
