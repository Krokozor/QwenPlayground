using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using QwenPlayground.Core.Chat;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Состояние одной полки в UI-меню (кнопка 🗄 в тулбаре чата): название, описание (тултип)
/// и статус — on (в промпте, останется), pending (помечена к снятию, уйдёт при ближайшей
/// естественной смене промпта), off (не в промпте). Чекбокс означает «on».
/// Состояние per-session (sessions/&lt;id&gt;/shelves.json), синхронизируется из файла в
/// <see cref="MainViewModel.RefreshShelfUi"/> (открытие меню, смена сессии, каждый запрос).
/// </summary>
public sealed class ShelfUiState : INotifyPropertyChanged
{
    // Замороженные кисти: статусы могут обновляться с фонового потока (ResolveSystemPrompt
    // ходит в agent loop), кисти общие на все инстансы.
    private static readonly Brush OnBrush = Frozen(0x7E, 0xC8, 0x7E);
    private static readonly Brush PendingBrush = Frozen(0xD8, 0xB4, 0x5A);
    private static readonly Brush OffBrush = Frozen(0x77, 0x77, 0x77);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool _isOn;
    private string _status = "off";
    private Brush _statusBrush = OffBrush;

    public ShelfUiState(ToolGroup group, string description)
    {
        Group = group;
        Name = group.ToString();
        Description = description;
    }

    public ToolGroup Group { get; }

    /// <summary>Название полки в строке меню (имя enum: Browser/CSharp/Desktop/Mcp).</summary>
    public string Name { get; }

    /// <summary>Что даёт полка — тултип строки.</summary>
    public string Description { get; }

    /// <summary>Чекбокс: полка «вкл» — активна и не помечена (тулзы в промпте и останутся).</summary>
    public bool IsOn { get => _isOn; private set { if (_isOn != value) { _isOn = value; OnPropertyChanged(); } } }

    /// <summary>Статус в строке меню: «вкл» / «pending» / «выкл».</summary>
    public string StatusText => _status switch { "on" => "вкл", "pending" => "pending", _ => "выкл" };

    /// <summary>Цвет статусной метки (зелёный/жёлтый/серый).</summary>
    public Brush StatusBrush => _statusBrush;

    /// <summary>
    /// Обновить из состояния полок: active — в shelves.json, pending — помечена на
    /// staged-деактивацию (тулзы ещё в промпте, но уйдут при ближайшей смене промпта).
    /// </summary>
    public void Refresh(bool active, bool pending)
    {
        var newOn = active && !pending;
        var newStatus = active ? (pending ? "pending" : "on") : "off";
        if (_isOn != newOn)
        {
            _isOn = newOn;
            OnPropertyChanged(nameof(IsOn));
        }
        if (!string.Equals(_status, newStatus, StringComparison.Ordinal))
        {
            _status = newStatus;
            OnPropertyChanged(nameof(StatusText));
        }
        var newBrush = newStatus switch { "on" => OnBrush, "pending" => PendingBrush, _ => OffBrush };
        if (!ReferenceEquals(_statusBrush, newBrush))
        {
            _statusBrush = newBrush;
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
