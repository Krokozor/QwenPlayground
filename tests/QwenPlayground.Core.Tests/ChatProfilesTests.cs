using System.Text.Json;
using QwenPlayground.Core.Runtime;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Статичное хранилище профилей чата: default гарантирован (файла нет / файл пуст /
/// кусок удалён), резолвер падает в default на неизвестном ключе, куски независимы.
/// </summary>
public sealed class ChatProfilesTests
{
    [Fact]
    public void EnsureDefaults_CreatesMissingDefaultPieces()
    {
        var set = new ChatProfileSet(); // пустой набор, как после отсутствующего файла

        set.EnsureDefaults();

        Assert.True(set.Samplers.ContainsKey(ChatProfileSet.DefaultKey));
        Assert.True(set.Prompts.ContainsKey(ChatProfileSet.DefaultKey));
        Assert.True(set.StateBlocks.ContainsKey(ChatProfileSet.DefaultKey));
    }

    [Fact]
    public void DefaultPieces_AreEmpty_WhichMeansInheritGlobals()
    {
        var set = new ChatProfileSet();
        set.EnsureDefaults();
        var sampler = set.Samplers[ChatProfileSet.DefaultKey];

        // Пустой семплер = «глобальные настройки»: все переопределяемые поля пусты.
        Assert.Equal(string.Empty, sampler.Temperature);
        Assert.Equal(string.Empty, sampler.MaxTokens);
        Assert.True(set.Prompts[ChatProfileSet.DefaultKey].Tools); // тулы по умолчанию включены
        Assert.True(set.StateBlocks[ChatProfileSet.DefaultKey].Enabled);
    }

    [Fact]
    public void Resolve_UnknownOrNullKey_FallsBackToDefault()
    {
        var set = new ChatProfileSet
        {
            Samplers = new Dictionary<string, SamplerProfile> { ["cold"] = new() { Temperature = "0.1" } },
            Prompts = new Dictionary<string, PromptProfile>(),
            StateBlocks = new Dictionary<string, StateBlockProfile>()
        };
        set.EnsureDefaults();

        Assert.Same(set.Samplers["default"], set.ResolveSampler("no-such"));
        Assert.Same(set.Samplers["default"], set.ResolveSampler(null));
        Assert.Same(set.Samplers["cold"], set.ResolveSampler("cold"));

        Assert.Same(set.Prompts["default"], set.ResolvePrompt("shaders"));
        Assert.Same(set.StateBlocks["default"], set.ResolveStateBlock("quiet"));
    }

    [Fact]
    public void Pieces_AreIndependent_SamplerFromOne_PromptFromAnother()
    {
        var set = new ChatProfileSet
        {
            Samplers = new Dictionary<string, SamplerProfile> { ["cold"] = new() { Temperature = "0.2" } },
            Prompts = new Dictionary<string, PromptProfile> { ["shaders"] = new() { Tools = false } }
        };
        set.EnsureDefaults();

        // Сессия может взять семплер из одного куска и промпт из другого.
        Assert.Equal("0.2", set.ResolveSampler("cold").Temperature);
        Assert.False(set.ResolvePrompt("shaders").Tools);
        // state-блок при этом дефолтный
        Assert.True(set.ResolveStateBlock(null).Enabled);
    }

    [Fact]
    public void JsonRoundTrip_PreservesAllThreeDictionaries()
    {
        var path = Path.Combine(Path.GetTempPath(), "qwen_chat_profiles_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var set = new ChatProfileSet
            {
                Samplers = new Dictionary<string, SamplerProfile> { ["cold"] = new() { Temperature = "0.3", MaxIterations = "120" } },
                Prompts = new Dictionary<string, PromptProfile> { ["night"] = new() { SystemPrompt = "Work alone.", AllowedTools = ["read_file"] } },
                StateBlocks = new Dictionary<string, StateBlockProfile> { ["quiet"] = new() { Enabled = false } }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(set));

            // Битый/частичный файл не роняет чтение: отсутствующие куски добираются дефолтами.
            var loaded = JsonSerializer.Deserialize<ChatProfileSet>(File.ReadAllText(path))!;
            loaded.EnsureDefaults();
            Assert.Equal("0.3", loaded.Samplers["cold"].Temperature);
            Assert.Equal("120", loaded.Samplers["cold"].MaxIterations);
            Assert.Equal("Work alone.", loaded.Prompts["night"].SystemPrompt);
            Assert.Equal(["read_file"], loaded.Prompts["night"].AllowedTools);
            Assert.False(loaded.StateBlocks["quiet"].Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
