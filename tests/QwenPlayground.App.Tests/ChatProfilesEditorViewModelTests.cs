using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Runtime;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Редактор статичных профилей чата: default всегда есть и не удаляется, добавление
/// с уникальным ключом, удаление куска, сохранение всего набора write-through.
/// Работает на изолированном экземпляре набора (статичное хранилище из тестов не трогаем).
/// </summary>
public sealed class ChatProfilesEditorViewModelTests
{
    private static ChatProfileSet FreshSet() => new();

    [Fact]
    public void Ctor_GuaranteesDefaultPieces_First()
    {
        var editor = new ChatProfilesEditorViewModel(FreshSet());

        Assert.Equal("default", editor.Samplers[0].Name);
        Assert.Equal("default", editor.Prompts[0].Name);
        Assert.Equal("default", editor.StateBlocks[0].Name);
        Assert.Same(editor.Samplers[0], editor.SelectedSampler);
    }

    [Fact]
    public void Add_UniqueNames()
    {
        var editor = new ChatProfilesEditorViewModel(FreshSet());
        var before = editor.Samplers.Count;

        editor.AddSamplerCommand.Execute(null);
        editor.AddSamplerCommand.Execute(null);

        Assert.Equal(before + 2, editor.Samplers.Count);
        Assert.NotEqual(editor.Samplers[^1].Name, editor.Samplers[^2].Name);
        Assert.StartsWith("profile-", editor.Samplers[^1].Name);
    }

    [Fact]
    public void Delete_DefaultRejected_CustomRemoved()
    {
        var editor = new ChatProfilesEditorViewModel(FreshSet());
        editor.AddPromptCommand.Execute(null);
        var custom = editor.SelectedPrompt!;

        // default удалить нельзя (команда молча выходит)
        editor.SelectedPrompt = editor.Prompts[0];
        editor.DeletePromptCommand.Execute(null);
        Assert.Contains(editor.Prompts, i => i.Name == "default");

        // кастомный удаляется
        editor.SelectedPrompt = custom;
        editor.DeletePromptCommand.Execute(null);
        Assert.DoesNotContain(editor.Prompts, i => i.Name == custom.Name);
    }

    [Fact]
    public void Save_WriteThrough_AllThreeKinds()
    {
        ChatProfileSet? saved = null;
        var set = FreshSet();
        var editor = new ChatProfilesEditorViewModel(set, s => saved = s);

        editor.AddSamplerCommand.Execute(null);
        editor.SelectedSampler!.Temperature = "0.25";
        editor.SelectedSampler.MaxIterations = "120";

        editor.AddPromptCommand.Execute(null);
        editor.SelectedPrompt!.SystemPrompt = "Be terse.";
        editor.SelectedPrompt.AllowedToolsText = "read_file\r\nglob";
        editor.SelectedPrompt.ToolsEnabled = true;

        editor.SelectedStateBlock!.Enabled = false;
        editor.SaveProfilesCommand.Execute(null);

        Assert.NotNull(saved);
        Assert.Equal("0.25", saved!.Samplers[editor.SelectedSampler.Name].Temperature);
        Assert.Equal("120", saved.Samplers[editor.SelectedSampler.Name].MaxIterations);
        Assert.Equal("Be terse.", saved.Prompts[editor.SelectedPrompt.Name].SystemPrompt);
        Assert.Equal(["read_file", "glob"], saved.Prompts[editor.SelectedPrompt.Name].AllowedTools);
        Assert.False(saved.StateBlocks[editor.SelectedStateBlock.Name].Enabled);
        // default-куски остались на месте
        Assert.True(saved.Samplers.ContainsKey("default"));
    }

    [Fact]
    public void Reload_NormalizesFromSet()
    {
        var set = FreshSet();
        set.EnsureDefaults();
        set.Samplers["cold"] = new SamplerProfile { Temperature = "0.1" };
        var editor = new ChatProfilesEditorViewModel(set);

        // Правка в UI без сохранения отбрасывается перезагрузкой.
        editor.SelectedSampler!.Temperature = "мусор в поле";
        editor.Reload();

        Assert.Equal(string.Empty, editor.Samplers.First(i => i.Name == "default").Temperature);
        Assert.Equal("0.1", editor.Samplers.First(i => i.Name == "cold").Temperature);
    }
}
