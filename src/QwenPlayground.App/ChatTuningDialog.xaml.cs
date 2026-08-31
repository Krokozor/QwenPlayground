using System.Collections.ObjectModel;
using System.Windows;
using QwenPlayground.Core.Runtime;

namespace QwenPlayground.App;

public partial class ChatTuningDialog : Window
{
    /// <summary>Строка выбора: null-ключ = кусок default («как общие настройки»).</summary>
    public sealed record PickerItem(string? Key)
    {
        public string Label => Key ?? "default";
    }

    public ObservableCollection<PickerItem> SamplerOptions { get; } = new();
    public ObservableCollection<PickerItem> PromptOptions { get; } = new();
    public ObservableCollection<PickerItem> StateBlockOptions { get; } = new();

    public PickerItem? SelectedSampler { get; set; }
    public PickerItem? SelectedPrompt { get; set; }
    public PickerItem? SelectedStateBlock { get; set; }

    public string? SelectedSamplerKey => SelectedSampler?.Key;
    public string? SelectedPromptKey => SelectedPrompt?.Key;
    public string? SelectedStateBlockKey => SelectedStateBlock?.Key;

    /// <summary>Переход к пресетам (вкладка «Настройки» — единственное место их правки).</summary>
    public Action? GoToSettings { get; set; }

    public ChatTuningDialog(
        IEnumerable<string> samplerKeys,
        IEnumerable<string> promptKeys,
        IEnumerable<string> stateBlockKeys,
        string? currentSamplerKey,
        string? currentPromptKey,
        string? currentStateBlockKey)
    {
        InitializeComponent();
        SelectedSampler = Fill(SamplerOptions, samplerKeys, currentSamplerKey);
        SelectedPrompt = Fill(PromptOptions, promptKeys, currentPromptKey);
        SelectedStateBlock = Fill(StateBlockOptions, stateBlockKeys, currentStateBlockKey);
        DataContext = this;
    }

    /// <summary>
    /// Наполнить список: default (null-ключ) первым, затем остальные по алфавиту;
    /// вернуть строку, соответствующую текущему ключу сессии (неизвестный → default).
    /// </summary>
    private static PickerItem Fill(ObservableCollection<PickerItem> target, IEnumerable<string> keys, string? current)
    {
        var items = new List<PickerItem> { new(null) };
        items.AddRange(keys.Where(k => k != ChatProfileSet.DefaultKey).OrderBy(k => k, StringComparer.Ordinal).Select(k => new PickerItem(k)));
        foreach (var item in items)
        {
            target.Add(item);
        }
        return items.FirstOrDefault(i => i.Key == current) ?? items[0];
    }

    private void Presets_Click(object sender, RoutedEventArgs e)
    {
        GoToSettings?.Invoke();
        DialogResult = false; // диалог закрывается: правим пресеты в настройках, потом открываем заново
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
