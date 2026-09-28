using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Один пункт TODO-панели: номер (1-based, тот же, что у TODO_manage), чекбокс, текст
/// (редактируется inline, сейв по LostFocus). Мутации шлют в TodoStore с источником
/// «user» — агент в state-напоминании увидит «edited by user».
/// </summary>
public sealed partial class TodoItemViewModel : ObservableObject
{
    private readonly TodoViewModel _owner;
    internal TodoItem Model { get; }

    public TodoItemViewModel(TodoViewModel owner, TodoItem model)
    {
        _owner = owner;
        Model = model;
    }

    /// <summary>
    /// Инициализация полей БЕЗ сейва (перестроение коллекции в Reload): сеттеры
    /// Text/IsDone вызвали бы SaveFromUi на частично собранной коллекции — файл
    /// затёрся бы неполным списком с источником «user». Модель здесь уже содержит
    /// актуальные значения, поля лишь подтягиваются для отображения.
    /// </summary>
    internal void Initialize(string text, bool done)
    {
        _text = text;
        _isDone = done;
        Model.Text = text;
        Model.Done = done;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsCompleted));
    }

    /// <summary>1-based номер в списке (индекс для TODO_manage).</summary>
    public int Number { get; set; }

    private bool _isDone;
    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone == value)
            {
                return;
            }
            _isDone = value;
            Model.Done = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCompleted));
            _owner.SaveFromUi();
        }
    }

    private string _text = string.Empty;
    /// <summary>Текст цели; правка inline, сейв — по LostFocus (не на каждый символ).</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }
            _text = value;
            Model.Text = value;
            OnPropertyChanged();
            _owner.SaveFromUi();
        }
    }

    /// <summary>Читабельность: выполненные пункты — перечёркнутые (TextDecorations в XAML).</summary>
    public bool IsCompleted => IsDone;
}

/// <summary>
/// Панель TODO над чатом: ручной просмотр/правка TODO-списка текущей сессии
/// (sessions/&lt;id&gt;/TODO.json — тот же файл, что и у тулов TODO_add/TODO_manage).
/// Живое обновление: подписка на TodoStore.Saved (агент изменил список — панель
/// перерисовывается; свои сейвы не триггерят цикл через флаг _savingFromUi).
/// Каталог сессии приходит функцией — класс не знает ни о MainViewModel, ни о хранилище.
/// </summary>
public sealed partial class TodoViewModel : ObservableObject
{
    private readonly Func<string> _sessionDir;
    private bool _savingFromUi;

    [ObservableProperty]
    private bool _panelVisible;

    [ObservableProperty]
    private string _newItemText = string.Empty;

    [ObservableProperty]
    private string _countText = string.Empty;

    public ObservableCollection<TodoItemViewModel> Items { get; } = new();

    /// <summary>Есть ли пункты (кнопка «очистить» видна только с пунктами).</summary>
    public bool HasItems => Items.Count > 0;

    public TodoViewModel(Func<string> sessionDir)
    {
        _sessionDir = sessionDir;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
        TodoStore.Saved += OnTodoSaved;
        Reload();
    }

    /// <summary>Показать/скрыть панель (кнопка «TODO» в тулбаре).</summary>
    [RelayCommand]
    private void TogglePanel() => PanelVisible = !PanelVisible;

    [RelayCommand]
    private void CollapsePanel() => PanelVisible = false;

    /// <summary>Добавить цель из окошка ввода (Enter или «+»). Источник — user.</summary>
    [RelayCommand]
    private void AddItem()
    {
        var text = NewItemText.Trim();
        if (text.Length == 0)
        {
            return;
        }
        NewItemText = string.Empty;
        var list = new TodoStore(_sessionDir()).LoadOrCreate();
        list.Items.Add(new TodoItem { Text = text });
        _savingFromUi = true;
        try
        {
            new TodoStore(_sessionDir()).Save(list, TodoStore.SourceUser);
        }
        finally
        {
            _savingFromUi = false;
        }
        Reload();
    }

    /// <summary>Удалить пункт (кнопка «×»). Источник — user.</summary>
    [RelayCommand]
    private void RemoveItem(TodoItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }
        var list = new TodoStore(_sessionDir()).LoadOrCreate();
        var index = list.Items.IndexOf(item.Model);
        if (index < 0)
        {
            return;
        }
        list.Items.RemoveAt(index);
        _savingFromUi = true;
        try
        {
            new TodoStore(_sessionDir()).Save(list, TodoStore.SourceUser);
        }
        finally
        {
            _savingFromUi = false;
        }
        Reload();
    }

    /// <summary>Очистить весь список (удаляет TODO.json: «нет файла — нет списка»).</summary>
    [RelayCommand]
    private void ClearAll()
    {
        var list = new TodoStore(_sessionDir()).LoadOrCreate();
        list.Items.Clear();
        _savingFromUi = true;
        try
        {
            new TodoStore(_sessionDir()).Save(list, TodoStore.SourceUser);
        }
        finally
        {
            _savingFromUi = false;
        }
        Reload();
    }

    /// <summary>
    /// Сейв от имени UI (чекбокс/правка текста): собирает список из живых моделей и
    /// пишет с источником «user». Вызывается из TodoItemViewModel — там же мутация модели,
    /// файлы синхронны.
    /// </summary>
    internal void SaveFromUi()
    {
        var list = new TodoList { Items = Items.Select(i => i.Model).ToList() };
        _savingFromUi = true;
        try
        {
            new TodoStore(_sessionDir()).Save(list, TodoStore.SourceUser);
        }
        finally
        {
            _savingFromUi = false;
        }
    }

    /// <summary>
    /// Агент (или другое окно) сохранил TODO.json: перерисовать панель, если это текущая
    /// сессия. Свои сейвы не триггерят (флаг _savingFromUi) — иначе правка текста в
    /// TextBox'е сбрасывала бы фокус на каждой сохранённой букве.
    /// </summary>
    private void OnTodoSaved(string sessionDir)
    {
        if (_savingFromUi)
        {
            return;
        }
        if (!string.Equals(sessionDir, _sessionDir(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Reload();
    }

    /// <summary>
    /// Перечитать TODO.json текущей сессии и перестроить Items. Dispatcher-safe (вызовы
    /// из UI и из agent loop). Смена сессии — тоже сюда (ChatViewModel.OnSessionChanged).
    /// </summary>
    public void Reload()
    {
        void Do()
        {
            var list = new TodoStore(_sessionDir()).Load();
            Items.Clear();
            if (list is not null)
            {
                for (var i = 0; i < list.Items.Count; i++)
                {
                    var item = new TodoItemViewModel(this, list.Items[i]) { Number = i + 1 };
                    item.Initialize(list.Items[i].Text, list.Items[i].Done); // без сейва!
                    Items.Add(item);
                }
            }
            var done = Items.Count(i => i.IsDone);
            CountText = Items.Count == 0 ? string.Empty : $"{done}/{Items.Count}";
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Do);
        }
        else
        {
            Do();
        }
    }

    /// <summary>Смена сессии: список пер-сессия — перечитать новый каталог.</summary>
    public void OnSessionChanged() => Reload();
}
