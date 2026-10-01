using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Панель сессий в тулбаре чата (селектор + кнопки «+»/«×»): список, выбор, команды.
/// Логика — в SessionController (Core); здесь только вид (список/выбор) и UI-гарды
/// (IsGenerating, статус — делегаты, класс не знает о MainViewModel). Реакции на смену
/// сессии за пределами модуля (превью, полки) оркестрирует MainViewModel по событию
/// контроллера — см. OnSessionChanged там.
/// </summary>
public sealed partial class SessionListViewModel : ObservableObject {
    private readonly SessionController _sessions;
    private readonly ChatLog _log;
    private readonly Func<bool> _isGenerating;
    private readonly Action<string> _status;
    // Общий KV-контроллер и спавнер Main (фаза 3, план 2026-09-28): не свои new.
    private readonly QwenPlayground.Core.Inference.KvCacheController _kv;
    private readonly QwenPlayground.Core.Subagents.SubagentSpawner _subagents;

    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    /// <summary>Опция слота в селекторе: Value — id слота (null = LRU сервера), Display — подпись.</summary>
    public sealed record SlotOption(int? Value, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>
    /// Слоты llama.cpp (сервер запущен с 5 слотами): 0 — main, 1-2 — окна (1 — дефолт не-main,
    /// 2 — ручная разводка), 3 — субагент и 4 — пробы — зарезервированы (не для сессий).
    /// Сессии доступны 0-2 + LRU: пинг сессии на зарезервированный слот коллидировал бы
    /// с субагентом/пробами (один слот — один владелец KV).
    /// </summary>
    public IReadOnlyList<SlotOption> SlotOptions { get; } = new[]
    {
        new SlotOption(null, "— (LRU)"),
        new SlotOption(0, "0 (main)"),
        new SlotOption(1, "1 (окно)"),
        new SlotOption(2, "2 (окно)"),
    };

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    [ObservableProperty]
    private SlotOption _selectedSlot = new(null, "— (LRU)");

    // Рабочая папка текущей сессии (root инструментов, план 2026-10-01): поле — своя
    // папка сессии (пусто — действует глобальный дефолт из настроек). «…» — пикер
    // существующей папки, Enter — применить введённое, «✕» — сброс к дефолту.
    // Ошибка валидации — красным под строкой; тул set_session_root меняет root
    // параллельно — поле синхронизируется событием RootChanged.
    [ObservableProperty]
    private string _sessionRoot = string.Empty;

    [ObservableProperty]
    private string? _sessionRootError;

    /// <summary>Красная строка ошибки видна, только если валидация не прошла.</summary>
    public bool HasSessionRootError => !string.IsNullOrEmpty(SessionRootError);

    partial void OnSessionRootErrorChanged(string? value) => OnPropertyChanged(nameof(HasSessionRootError));

    /// <summary>
    /// Кнопка «×» (удаление сессии) видна только для не-main сессий: main удалить нельзя,
    /// поэтому нажимать на кнопку у main незачем.
    /// </summary>
    public bool CanDeleteSelectedSession =>
        SelectedSession is not null && SelectedSession.Id != MainAgent.SessionId;

    /// <summary>main-сессия управляется идентичностью — настройка чата для неё закрыта.</summary>
    public bool IsMainSession => _sessions.CurrentId == MainAgent.SessionId;

    public SessionListViewModel(
        SessionController sessions, ChatLog log, Func<bool> isGenerating, Action<string> status,
        QwenPlayground.Core.Inference.KvCacheController kv,
        QwenPlayground.Core.Subagents.SubagentSpawner subagents) {
        _sessions = sessions;
        _log = log;
        _kv = kv;
        _subagents = subagents;
        _isGenerating = isGenerating;
        _status = status;
        // Агент сменил root инструментом set_session_root — синхронизируем поле.
        _sessions.RootChanged += SyncSessionRootField;
        SyncSessionRootField();
    }

    partial void OnSelectedSessionChanged(SessionInfo? value) {
        OnPropertyChanged(nameof(CanDeleteSelectedSession));
        if (value is null || value.Id == _sessions.CurrentId || _isGenerating())
            return;

        if (_sessions.Load(value.Id))
            _status(string.Empty);
        // Реакции вида (список/превью/полки) — в OnSessionChanged (событие контроллера).
        SyncSessionRootField();
    }

    /// <summary>
    /// Выбор слота в UI: применяем к текущей сессии (SetCurrentSlot сохраняет сразу).
    /// Программные обновления (Refresh после смены сессии) не пишут — значение уже совпадает.
    /// </summary>
    partial void OnSelectedSlotChanged(SlotOption value) {
        if (value.Value != _sessions.CurrentSlotId)
            _ = ApplySlotAsync(value);
    }

    /// <summary>
    /// Применить выбранный слот с проверкой, что он ЕСТЬ на сервере: б10353 тихо перемапит
    /// невалидный id_slot на слот 0 (проверено живой пробой) — без гварда сессия на «слоте 4»
    /// молча ходила бы в чужой KV. Нет слота — откат выбора + статус.
    /// </summary>
    private async Task ApplySlotAsync(SlotOption value) {
        if (value.Value is { } slotId) {
            // Занятость (фаза 3, план 2026-09-28): другой ЖИВОЙ рантайм на этом слоте =
            // KV-трешинг двух чатов. Сегодня единственный другой живой рантайм — субагент
            // (слот 3); откреплённые окна (стадия C) без центрального реестра — аллокатор
            // фазы 4 закроет.
            if (slotId == QwenPlayground.Core.Inference.SlotAllocation.Subagent && _subagents.Current is not null) {
                _status($"слот {slotId} занят субагентом (он работает); выбор откатился");
                SelectedSlot = SlotOptions.FirstOrDefault(o => o.Value == _sessions.CurrentSlotId)
                               ?? SlotOptions[0];
                return;
            }
            var slots = await _kv.GetSlotsAsync();
            if (slots.All(s => s.Id != slotId)) {
                _status($"на сервере нет слота {slotId} (перезапустите llama.cpp с --slots {slotId + 1}+); выбор откатился");
                SelectedSlot = SlotOptions.FirstOrDefault(o => o.Value == _sessions.CurrentSlotId)
                               ?? SlotOptions[0];
                return;
            }
        }
        _sessions.SetCurrentSlot(value.Value);
    }

    /// <summary>
    /// «✕» рядом с селектором: вычистить KV слота ТЕКУЩЕЙ сессии (следующий ход пере-евалюирует
    /// промпт — осознанный сброс кэша, например после «чужого» замедления промпт-обработки).
    /// Во время генерации не трогаем (ход уже привязан к слоту).
    /// </summary>
    [RelayCommand]
    private async Task EraseCurrentSlotAsync() {
        if (_isGenerating())
            return;

        if (_sessions.CurrentSlotId is not { } slotId) {
            _status("слот не назначен (LRU) — чистить нечего");
            return;
        }

        var ok = await _kv.EraseSlotAsync(slotId);
        _status(ok ? $"слот {slotId} вычищен" : $"не удалось вычистить слот {slotId} (сервер/флаг)");
    }

    [RelayCommand]
    private void NewSession() {
        if (_isGenerating())
            return;

        if (_log.Count > 0)
            _sessions.SaveCurrent();

        _log.Clear();
        _sessions.StartNew();
        // Реакции вида (список/выбор/превью/полки) — в OnSessionChanged (событие контроллера).
    }

    [RelayCommand]
    private void DeleteSession() {
        if (_isGenerating() || SelectedSession is null)
            return;

        if (SelectedSession.Id == MainAgent.SessionId) {
            _status("основную сессию нельзя удалить");
            return;
        }

        // Удаление необратимо (chat.json + artifacts сессии) — подтверждаем.
        var confirm = new Views.ConfirmWindow($"Удалить сессию «{SelectedSession.Title}»?")
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (confirm.ShowDialog() != true)
            return;

        _sessions.Delete(SelectedSession.Id);
        // Реакции вида (список/превью/полки) — в OnSessionChanged (событие контроллера).
    }

    /// <summary>
    /// Сохранить текущую сессию (chat.json). Вызывается после каждого изменения чата;
    /// список обновляется — заголовок/время в списке могли измениться.
    /// </summary>
    public void SaveCurrent() {
        _sessions.SaveCurrent();
        Refresh();
    }

    /// <summary>
    /// Пересобрать список сессий (вид): список + выбор (селектор следует за CurrentId)
    /// + флаг main.
    /// </summary>
    public void Refresh() {
        _sessions.RefreshList();
        Sessions.Clear();
        foreach (var info in _sessions.List) {
            Sessions.Add(info);
        }
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == _sessions.CurrentId);
        // Селектор слота следует за текущей сессией (совпадение по Value — без записи).
        SelectedSlot = SlotOptions.FirstOrDefault(o => o.Value == _sessions.CurrentSlotId)
                       ?? SlotOptions[0];
        OnPropertyChanged(nameof(IsMainSession));
        SyncSessionRootField();
    }

    /// <summary>Поле root ← текущая рабочая папка сессии (пусто — не задано, дефолт).</summary>
    private void SyncSessionRootField()
    {
        SessionRoot = _sessions.Root ?? string.Empty;
        SessionRootError = null;
    }

    /// <summary>
    /// Применить рабочую папку из поля (Enter или «✓»): валидация у сессии
    /// (абсолютный путь, папка существует, создание НЕЛЬЗЯ). Ошибка — красным,
    /// root не меняется.
    /// </summary>
    [RelayCommand]
    private void ApplySessionRoot()
    {
        var error = _sessions.SetRoot(SessionRoot);
        SessionRootError = error;
        if (error is null)
        {
            SyncSessionRootField();
        }
    }

    /// <summary>«…»: пикер существующей папки, затем применить.</summary>
    [RelayCommand]
    private void BrowseSessionRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Рабочая папка сессии" };
        if (dialog.ShowDialog() == true)
        {
            SessionRoot = dialog.FolderName;
            ApplySessionRoot();
        }
    }

    /// <summary>«✕»: сбросить рабочую папку — вернуться к глобальному дефолту из настроек.</summary>
    [RelayCommand]
    private void ClearSessionRoot()
    {
        _sessions.ClearRoot();
        SyncSessionRootField();
    }

    /// <summary>
    /// Восстановить последнюю открытую сессию (из settings.json). Логика — в
    /// SessionController; реакции вида — в OnSessionChanged.
    /// </summary>
    public void RestoreLast() => _sessions.RestoreLast();

    /// <summary>
    /// Гарантировать существование main-сессии (старт). Логика — в SessionController;
    /// здесь только обновление списка сессий (вид).
    /// </summary>
    public void EnsureMain() {
        _sessions.EnsureMain();
        Refresh();
    }
}
