using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.Settings;

namespace QwenPlayground.App.ViewModels;

/// <summary>
/// Редактор статичного хранилища профилей чата (config/chat-profiles.json) во вкладке
/// «Суммаризация». Три независимых списка — по куску конфигурации; запись default
/// всегда есть и не удаляется. Ключи после создания не переименовываются в UI: на них
/// ссылаются сессии. Сохранение — write-through всего набора одним файлом.
/// </summary>
public partial class ChatProfilesEditorViewModel : ObservableObject
{
    private readonly ChatProfileSet _set;

    // Тесты подменяют запись (статичное хранилище трогать из тестов нельзя — оно процессное).
    private readonly Action<ChatProfileSet>? _saveOverride;

    public ObservableCollection<SamplerItem> Samplers { get; } = new();
    public ObservableCollection<PromptItem> Prompts { get; } = new();
    public ObservableCollection<StateBlockItem> StateBlocks { get; } = new();

    [ObservableProperty]
    private SamplerItem? _selectedSampler;

    [ObservableProperty]
    private PromptItem? _selectedPrompt;

    [ObservableProperty]
    private StateBlockItem? _selectedStateBlock;

    [ObservableProperty]
    private string _status = string.Empty;

    public ChatProfilesEditorViewModel() : this(ChatProfileSet.Get())
    {
    }

    public ChatProfilesEditorViewModel(ChatProfileSet set, Action<ChatProfileSet>? saveOverride = null)
    {
        _set = set;
        _saveOverride = saveOverride;
        Reload();
    }

    public void Reload()
    {
        _set.EnsureDefaults();
        Samplers.Clear();
        foreach (var pair in Ordered(_set.Samplers))
        {
            Samplers.Add(new SamplerItem(pair.Key, pair.Value));
        }
        Prompts.Clear();
        foreach (var pair in Ordered(_set.Prompts))
        {
            Prompts.Add(new PromptItem(pair.Key, pair.Value));
        }
        StateBlocks.Clear();
        foreach (var pair in Ordered(_set.StateBlocks))
        {
            StateBlocks.Add(new StateBlockItem(pair.Key, pair.Value));
        }
        SelectedSampler = Samplers.FirstOrDefault();
        SelectedPrompt = Prompts.FirstOrDefault();
        SelectedStateBlock = StateBlocks.FirstOrDefault();
    }

    private static IEnumerable<KeyValuePair<string, T>> Ordered<T>(Dictionary<string, T> source) =>
        source.OrderBy(p => p.Key == ChatProfileSet.DefaultKey ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal);

    [RelayCommand]
    private void AddSampler()
    {
        var item = new SamplerItem(NextFreeName(Samplers.Select(i => i.Name)), new SamplerProfile());
        Samplers.Add(item);
        SelectedSampler = item;
        Status = $"Добавлен семплер «{item.Name}».";
    }

    [RelayCommand]
    private void AddPrompt()
    {
        var item = new PromptItem(NextFreeName(Prompts.Select(i => i.Name)), new PromptProfile());
        Prompts.Add(item);
        SelectedPrompt = item;
        Status = $"Добавлен промпт-профиль «{item.Name}».";
    }

    [RelayCommand]
    private void AddStateBlock()
    {
        var item = new StateBlockItem(NextFreeName(StateBlocks.Select(i => i.Name)), new StateBlockProfile());
        StateBlocks.Add(item);
        SelectedStateBlock = item;
        Status = $"Добавлен профиль state-блока «{item.Name}».";
    }

    [RelayCommand]
    private void DeleteSampler()
    {
        if (SelectedSampler is null || SelectedSampler.Name == ChatProfileSet.DefaultKey)
        {
            return;
        }
        _set.Samplers.Remove(SelectedSampler.Name);
        Samplers.Remove(SelectedSampler);
        SelectedSampler = Samplers.FirstOrDefault();
        Status = "Семплер удалён. Сессии с этим ключом вернутся на default.";
    }

    [RelayCommand]
    private void DeletePrompt()
    {
        if (SelectedPrompt is null || SelectedPrompt.Name == ChatProfileSet.DefaultKey)
        {
            return;
        }
        _set.Prompts.Remove(SelectedPrompt.Name);
        Prompts.Remove(SelectedPrompt);
        SelectedPrompt = Prompts.FirstOrDefault();
        Status = "Промпт-профиль удалён. Сессии с этим ключом вернутся на default.";
    }

    [RelayCommand]
    private void DeleteStateBlock()
    {
        if (SelectedStateBlock is null || SelectedStateBlock.Name == ChatProfileSet.DefaultKey)
        {
            return;
        }
        _set.StateBlocks.Remove(SelectedStateBlock.Name);
        StateBlocks.Remove(SelectedStateBlock);
        SelectedStateBlock = StateBlocks.FirstOrDefault();
        Status = "Профиль state-блока удалён. Сессии с этим ключом вернутся на default.";
    }

    /// <summary>Записать весь набор в config/chat-profiles.json одним файлом.</summary>
    [RelayCommand]
    private void SaveProfiles()
    {
        foreach (var item in Samplers)
        {
            _set.Samplers[item.Name] = item.ToProfile();
        }
        foreach (var item in Prompts)
        {
            _set.Prompts[item.Name] = item.ToProfile();
        }
        foreach (var item in StateBlocks)
        {
            _set.StateBlocks[item.Name] = item.ToProfile();
        }
        if (_saveOverride is { } save)
        {
            save(_set); // тесты
        }
        else
        {
            _set.Save();
        }
        Status = "Профили сохранены. Действует со следующего хода.";
    }

    private static string NextFreeName(IEnumerable<string> used)
    {
        var taken = used.ToHashSet(StringComparer.Ordinal);
        var n = 1;
        while (taken.Contains($"profile-{n}"))
        {
            n++;
        }
        return $"profile-{n}";
    }
}

/// <summary>Вид записи-семплера; поля строковые — пусто = унаследовать общие настройки.</summary>
public sealed class SamplerItem
{
    public string Name { get; }

    public SamplerItem(string name, SamplerProfile profile)
    {
        Name = name;
        MaxTokens = profile.MaxTokens;
        Temperature = profile.Temperature;
        TopP = profile.TopP;
        TopK = profile.TopK;
        MinP = profile.MinP;
        RepeatPenalty = profile.RepeatPenalty;
        Seed = profile.Seed;
        MaxIterations = profile.MaxIterations;
        SanityCheckInterval = profile.SanityCheckInterval;
    }

    public string MaxTokens { get; set; } = string.Empty;
    public string Temperature { get; set; } = string.Empty;
    public string TopP { get; set; } = string.Empty;
    public string TopK { get; set; } = string.Empty;
    public string MinP { get; set; } = string.Empty;
    public string RepeatPenalty { get; set; } = string.Empty;
    public string Seed { get; set; } = string.Empty;
    public string MaxIterations { get; set; } = string.Empty;
    public string SanityCheckInterval { get; set; } = string.Empty;

    public SamplerProfile ToProfile() => new()
    {
        MaxTokens = MaxTokens.Trim(),
        Temperature = Temperature.Trim(),
        TopP = TopP.Trim(),
        TopK = TopK.Trim(),
        MinP = MinP.Trim(),
        RepeatPenalty = RepeatPenalty.Trim(),
        Seed = Seed.Trim(),
        MaxIterations = MaxIterations.Trim(),
        SanityCheckInterval = SanityCheckInterval.Trim()
    };
}

/// <summary>Вид записи-промпта.</summary>
public sealed class PromptItem
{
    public string Name { get; }

    public PromptItem(string name, PromptProfile profile)
    {
        Name = name;
        SystemPrompt = profile.SystemPrompt;
        ResultContract = profile.ResultContract;
        ToolsEnabled = profile.Tools;
        AllowedToolsText = string.Join(Environment.NewLine, profile.AllowedTools);
        ReasoningEffort = profile.ReasoningEffort;
    }

    public string SystemPrompt { get; set; } = string.Empty;
    public string ResultContract { get; set; } = string.Empty;
    public bool ToolsEnabled { get; set; } = true;
    /// <summary>Имена по одной на строку или через запятую.</summary>
    public string AllowedToolsText { get; set; } = string.Empty;
    public string ReasoningEffort { get; set; } = string.Empty;

    public PromptProfile ToProfile() => new()
    {
        SystemPrompt = SystemPrompt,
        ResultContract = ResultContract,
        Tools = ToolsEnabled,
        ReasoningEffort = ReasoningEffort.Trim(),
        AllowedTools = AllowedToolsText
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
    };
}

/// <summary>Вид записи state-блока.</summary>
public sealed class StateBlockItem
{
    public string Name { get; }

    public StateBlockItem(string name, StateBlockProfile profile)
    {
        Name = name;
        Enabled = profile.Enabled;
    }

    public bool Enabled { get; set; } = true;

    public StateBlockProfile ToProfile() => new() { Enabled = Enabled };
}
