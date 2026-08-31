using QwenPlayground.App.ViewModels;

namespace QwenPlayground.App.Tests;

/// <summary>
/// Публикация стрима троттлится реальным Stopwatch (~50 мс), а хвост потока держится
/// неразрешённым до FlushStreaming (возможное начало think-маркера). Финальное состояние
/// всегда накрывает промежуточное через ApplyParsed — как в реальном ходе.
/// </summary>
public sealed class MessageViewModelStreamingTests
{
    [Fact]
    public void StreamWithoutMarker_AccumulatesIntoReasoning()
    {
        var vm = new MessageViewModel { Role = "assistant" };
        vm.BeginStreaming(string.Empty);
        vm.AppendStreamChunk("думаю ");
        vm.AppendStreamChunk("о задаче");
        vm.FlushStreaming();

        Assert.Equal("думаю о задаче", vm.Reasoning);
        Assert.Equal(string.Empty, vm.Content);
    }

    [Fact]
    public void PrefillWithMarker_ContinuesIntoContent()
    {
        var vm = new MessageViewModel { Role = "assistant" };
        // Continue-ход: raw начинается с прошлого вывода (мысль уже закрыта).
        vm.BeginStreaming("old thought</think>\nold answer");
        vm.AppendStreamChunk(" more");
        vm.FlushStreaming();

        Assert.Equal("old thought", vm.Reasoning);
        Assert.Equal("old answer more", vm.Content);
    }

    [Fact]
    public void MarkerAcrossChunks_SplitsCorrectly()
    {
        var vm = new MessageViewModel { Role = "assistant" };
        vm.BeginStreaming(string.Empty);
        vm.AppendStreamChunk("ab");
        vm.AppendStreamChunk("</thi");
        vm.AppendStreamChunk("nk>\n\nanswer");
        vm.FlushStreaming();

        Assert.Equal("ab", vm.Reasoning);
        Assert.Equal("answer", vm.Content);
    }

    [Fact]
    public void FirstChunkInsideThrottleWindow_IsDeferred()
    {
        var vm = new MessageViewModel { Role = "assistant" };
        vm.BeginStreaming(string.Empty);

        var first = new string('a', 30);
        vm.AppendStreamChunk(first); // BeginStreaming только что перезапустил порог
        Assert.Equal(string.Empty, vm.Reasoning); // публикация отложена

        Thread.Sleep(60);
        var second = new string('b', 10);
        vm.AppendStreamChunk(second); // чанк после окна публикует всё накопленное
        Assert.StartsWith(first, vm.Reasoning);

        vm.FlushStreaming();
        Assert.Equal(first + second, vm.Reasoning);
    }

    [Fact]
    public void ApplyParsed_OverridesStreamedState_AndKillsStream()
    {
        var vm = new MessageViewModel { Role = "assistant" };
        vm.BeginStreaming(string.Empty);
        vm.AppendStreamChunk("partial reasoning</think>partial content");
        vm.FlushStreaming();

        var final = ChatMessage_Assistant();
        vm.ApplyParsed(final);

        Assert.Equal("final reasoning", vm.Reasoning);
        Assert.Equal("final answer", vm.Content);
        vm.AppendStreamChunk(" ignored"); // стрим завершён: новые чанки не меняют вид
        Assert.Equal("final answer", vm.Content);
    }

    [Fact]
    public void RestartStream_ResetsBuffers()
    {
        var vm = new MessageViewModel { Role = "assistant" };
        vm.BeginStreaming("first turn</think>content");
        vm.FlushStreaming();

        vm.BeginStreaming(string.Empty);
        vm.AppendStreamChunk("new thought");
        vm.FlushStreaming();

        Assert.Equal("new thought", vm.Reasoning);
        Assert.Equal(string.Empty, vm.Content);
    }

    private static Core.Chat.ChatMessage ChatMessage_Assistant() =>
        Core.Chat.ChatMessage.Assistant("final answer", reasoning: "final reasoning");
}

/// <summary>Классификация вложений по расширению — без декодирования картинок.</summary>
public sealed class AttachmentClassificationTests
{
    [Theory]
    [InlineData("shot.png", true)]
    [InlineData("photo.JPG", true)]
    [InlineData("anim.gif", true)]
    [InlineData("doc.pdf", false)]
    [InlineData("notes.txt", false)]
    [InlineData("noext", false)]
    public void IsImage_ByExtension(string name, bool expected)
    {
        Assert.Equal(expected, new PendingAttachment(name, @"C:\tmp\" + name).IsImage);
        Assert.Equal(expected, new MessageAttachment(name, @"C:\tmp\" + name).IsImage);
    }
}
