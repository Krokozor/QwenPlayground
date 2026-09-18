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

    [ObservableProperty]
    private SessionInfo? _selectedSession;

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
