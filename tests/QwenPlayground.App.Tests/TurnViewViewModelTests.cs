using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using QwenPlayground.App.ViewModels;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Compaction;
using QwenPlayground.Core.Inference;
using QwenPlayground.Core.Main;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.MetaInfo;
using QwenPlayground.Core.Runtime;
using QwenPlayground.Core.Sessions;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

namespace QwenPlayground.App.Tests;

public sealed class TurnViewViewModelTests
{
    [Fact]
    public async Task CancelDuringTool_MarksPendingToolCanceled()
    {
        var root = Path.Combine(Path.GetTempPath(), "turn-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtime = new ChatRuntime(() => MainAgent.SessionId, new ServerProps());
            var tools = new ToolRegistry(typeof(AgentTool).Assembly);
            var surfacer = runtime.MemorySurfacer;
            runtime.PromptAssembler = new SystemPromptAssembler(
                runtime.SessionId, () => null, () => root, () => root,
                new InjectedIdentity(), new ExternalToolsNote(), tools);
            runtime.StateBlocks = new StateBlockBuilder(
                () => { }, () => 1, () => 100_000, new ServerProps(),
                () => runtime.Log, () => surfacer.GetSurfacedForStateBlock(), []);
            runtime.Maintenance = new ContextMaintenance(
                runtime.Log,
                runtime.ChatState,
                new CompactionPreview(),
                (user, system, onChunk, ct) => Task.FromResult<string?>(null),
                new MemoryLayerStore(root),
                surfacer,
                ct => Task.FromResult(0),
                () => Task.FromResult(100_000),
                runtime.SessionId,
                new ContextBackupStore(root),
                new ContextMaintenance.Ui(_ => { }, _ => { }, () => { }));
            runtime.Turns = new TurnPipeline(
                runtime.Log,
                runtime.ChatState,
                tools,
                runtime.StateBlocks,
                runtime.Maintenance,
                new ServerProps(),
                new TurnSessionView(
                    runtime.SessionId,
                    () => root,
                    () => null,
                    () => null,
                    () => null,
                    () => { },
                    () => null,
                    () => root),
                runtime.PromptAssembler,
                surfacer,
                _ => { },
                _ => { },
                () => { },
                CancelAfterToolStart);
            var messages = new ObservableCollection<MessageViewModel>();
            var view = new TurnViewViewModel(
                runtime, new BackgroundWork(_ => { }), messages, () => root, _ => { });

            await view.GenerateAsync();

            var tool = Assert.Single(messages);
            Assert.Equal("отменено", tool.Content);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async IAsyncEnumerable<AgentEvent> CancelAfterToolStart(AgentLoopRequest request)
    {
        yield return new ToolCallStartedEvent("shell", new JsonObject());
        await Task.Yield();
        throw new OperationCanceledException();
    }
}
