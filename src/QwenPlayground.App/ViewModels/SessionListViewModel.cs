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

    /// <summary>
    /// Кнопка «×» (удаление сессии) видна только для не-main сессий: main удалить нельзя,
    /// поэтому нажимать на кнопку у main незачем.
    /// </summary>
    public bool CanDeleteSelectedSession =>
        SelectedSession is not null && SelectedSession.Id != MainAgent.SessionId;

    /// <summary>main-сессия управляется идентичностью — настройка чата для неё закрыта.</summary>
    public bool IsMainSession => _sessions.CurrentId == MainAgent.SessionId;

    public SessionListViewModel(SessionController sessions, ChatLog log, Func<bool> isGenerating, Action<string> status) {
        _sessions = sessions;
        _log = log;
        _isGenerating = isGenerating;
        _status = status;
    }

    partial void OnSelectedSessionChanged(SessionInfo? value) {
        OnPropertyChanged(nameof(CanDeleteSelectedSession));
        if (value is null || value.Id == _sessions.CurrentId || _isGenerating())
            return;

        if (_sessions.Load(value.Id))
            _status(string.Empty);
        // Реакции вида (список/превью/полки) — в OnSessionChanged (событие контроллера).
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
            var kv = new QwenPlayground.Core.Inference.KvCacheController();
            var slots = await kv.GetSlotsAsync();
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

        var kv = new QwenPlayground.Core.Inference.KvCacheController();
        var ok = await kv.EraseSlotAsync(slotId);
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
