using System.Net;
using System.Text;
using System.Text.Json;
using QwenPlayground.Core.Agent;
using QwenPlayground.Core.Chat;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Templates;
using QwenPlayground.Core.Tools;

Console.OutputEncoding = Encoding.UTF8;

var scenario = args.Length > 0 ? args[0] : "tool-list";
var endpoint = args.Length > 1 ? args[1] : "http://127.0.0.1:5001";

return scenario switch
{
    "tool-list" => await Scenarios.ToolList(endpoint),
    "write-read" => await Scenarios.WriteRead(endpoint),
    "continue-thought" => await Scenarios.ContinueThought(endpoint),
    "self-rebuild" => await Scenarios.SelfRebuild(),
    "vm-probe" => Scenarios.VmProbe(),
    "prompt-dump" => Scenarios.PromptDump(),
    "cancel-continue" => await Scenarios.CancelContinue(),
    "memory-smoke" => await Scenarios.MemorySmoke(endpoint),
    "rollback-400" => await Scenarios.RollbackAfter400(),
    _ => Usage(scenario)
};

static int Usage(string scenario)
{
    Console.WriteLine($"unknown scenario: {scenario}");
    Console.WriteLine("available: tool-list, write-read, continue-thought, prompt-dump, memory-smoke");
    return 2;
}

internal sealed class Trace
{
    public int ToolCallsStarted;
    public bool Finished;
}

internal static class Scenarios
{
    public static async Task<int> ToolList(string endpoint)
    {
        var root = CreateProjectDir(("readme.txt", "hello from harness"), ("src/main.cs", "class Main {}"));
        var conversation = new ChatLog
        {
            ChatMessage.User("List the files in this project using the glob tool, then briefly tell me what you found.")
        };

        var passed = await Run(endpoint, root, conversation, trace => trace.ToolCallsStarted >= 1 && trace.Finished);
        Cleanup(root);
        return passed ? 0 : 1;
    }

    public static async Task<int> WriteRead(string endpoint)
    {
        var root = CreateProjectDir();
        var conversation = new ChatLog
        {
            ChatMessage.User("Create a file named hello.txt containing exactly: agent was here. Use the write_file tool.")
        };

        var file = Path.Combine(root, "hello.txt");
        var passed = await Run(endpoint, root, conversation,
            _ => File.Exists(file) && File.ReadAllText(file).Contains("agent was here"));
        Cleanup(root);
        return passed ? 0 : 1;
    }

    public static async Task<int> ContinueThought(string endpoint)
    {
        var root = CreateProjectDir();
        var conversation = new ChatLog
        {
            ChatMessage.User("Think step by step about whether 177 is a prime number, then answer.")
        };
        var loop = new AgentLoop(new ToolRegistry());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var truncated = new QwenPlayground.Core.Inference.GenerationOptions { MaxTokens = 40 };
        AppSettings.Get().Endpoint = endpoint;
        AppSettings.Get().ProjectRoot = root;
        await Drain(loop.RunAsync(new AgentLoopRequest
        {
            Conversation = conversation,
            Generation = truncated,
            MaxIterations = 1,
            CancellationToken = timeout.Token
        }));

        var assistant = conversation[^1];
        var reasoningBefore = assistant.Reasoning ?? string.Empty;
        Console.WriteLine($"--- phase 1: truncated thought ({reasoningBefore.Length} chars, closed={assistant.ThinkingClosed}) ---");
        Console.WriteLine(Truncate(reasoningBefore, 300));

        if (assistant.ThinkingClosed)
        {
            Console.WriteLine("SKIP: thought closed too early, increase MaxTokens or the model was too fast");
            Cleanup(root);
            return 2;
        }

        var continued = new QwenPlayground.Core.Inference.GenerationOptions { MaxTokens = 1024 };
        AppSettings.Get().Endpoint = endpoint;
        AppSettings.Get().ProjectRoot = root;
        await Drain(loop.RunAsync(new AgentLoopRequest
        {
            Conversation = conversation,
            Generation = continued,
            MaxIterations = 1,
            ContinueLastAssistant = true,
            CancellationToken = timeout.Token
        }));

        var reasoningAfter = assistant.Reasoning ?? string.Empty;
        Console.WriteLine($"--- phase 2: continued thought ({reasoningAfter.Length} chars, closed={assistant.ThinkingClosed}) ---");
        Console.WriteLine(Truncate(reasoningAfter, 600));
        Console.WriteLine("--- final content ---");
        Console.WriteLine(Truncate(assistant.Content, 300));

        var passed = reasoningAfter.Length > reasoningBefore.Length &&
                     reasoningAfter.StartsWith(reasoningBefore[..Math.Min(reasoningBefore.Length, 100)]) &&
                     assistant.ThinkingClosed;
        Console.WriteLine(passed ? "PASS" : "FAIL");
        Cleanup(root);
        return passed ? 0 : 1;
    }

    private static async Task Drain(IAsyncEnumerable<AgentEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    public static async Task<int> SelfRebuild()
    {
        Console.WriteLine("staging build (dotnet build + test gate)...");
        var result = await QwenPlayground.Core.SelfBuild.SelfBuildService.BuildNextAsync(CancellationToken.None);
        Console.WriteLine($"build id: {result.Id}, exit code: {result.ExitCode}");
        if (result.ExitCode != 0)
        {
            Console.WriteLine(result.OutputTail);
            Console.WriteLine("FAIL");
            return 1;
        }

        QwenPlayground.Core.SelfBuild.SelfBuildService.RequestRestart(result.Id);
        Console.WriteLine("restart requested; journal entry:");
        var last = QwenPlayground.Core.SelfBuild.BuildJournal.Load(QwenPlayground.Core.SelfBuild.SelfBuildPaths.RunRoot).LastOrDefault();
        Console.WriteLine($"  {last?.Id} status={last?.Status}");
        Console.WriteLine("PASS");
        return 0;
    }

    public static int VmProbe()
    {
        var viewModel = new QwenPlayground.App.ViewModels.MainViewModel();
        var message = new QwenPlayground.App.ViewModels.MessageViewModel { Role = "assistant" };
        viewModel.Messages.Add(message);

        Console.WriteLine($"IsGenerating: {viewModel.IsGenerating}");
        Console.WriteLine($"EditMessage(msg): {viewModel.EditMessageCommand.CanExecute(message)}");
        Console.WriteLine($"Rollback(msg): {viewModel.RollbackCommand.CanExecute(message)}");
        Console.WriteLine($"InspectPrompt(msg): {viewModel.InspectPromptCommand.CanExecute(message)}");
        Console.WriteLine($"CopyChat(null): {viewModel.CopyChatCommand.CanExecute(null)}");
        Console.WriteLine($"Continue(null): {viewModel.ContinueCommand.CanExecute(null)}");
        return 0;
    }

    public static int PromptDump()
    {
        var registry = new ToolRegistry();
        var conversation = new ChatLog
        {
            ChatMessage.System("You are a coding agent."),
            ChatMessage.User("List files.")
        };
        Console.WriteLine(QwenChatTemplate.Render(conversation, registry.Definitions, addGenerationPrompt: true));
        return 0;
    }

    /// <summary>
    /// Смоук ассоциативной памяти вживую: пара тестовых фактов классифицируется на компаньоне
    /// (категории + эмодзи), затем фейковый вектор диалога прогоняется через реколл.
    /// Валидирует весь путь: нативный /completion → слои → overlap. Использует временную папку,
    /// боевое memories/ не трогает.
    /// </summary>
    public static async Task<int> MemorySmoke(string endpoint)
    {
        var dir = Path.Combine(Path.GetTempPath(), "qwen_memory_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new MemoryStore(dir);

        var memories = new[]
        {
            "The owner prefers the GUI in WPF with CommunityToolkit.Mvvm and MVVM conventions.",
            "The companion model runs llama.cpp on RX570 (Vulkan) with Gemma4-E4B at http://192.168.0.109:8001.",
            "Associative memory recall in QwenPlayground uses semantic layers (categories A-Z + emoji) and normalized overlap scoring, no embeddings."
        };

        Console.WriteLine($"--- enriching {memories.Length} facts on {endpoint} ---");
        foreach (var (content, i) in memories.Select((c, i) => (c, i)))
        {
            var item = store.Add(content, source: "smoke");
            await MemoryClassifier.EnrichAsync(item, endpoint);
            if (item.HasSemanticLayers)
            {
                store.Update(item); // персист слоёв — как в боевом SaveMemoryClassifiedAsync
            }
            var top = string.Join(", ", item.CategoryLayers
                .OrderByDescending(kv => kv.Value).Take(3)
                .Select(kv => $"{kv.Key}={kv.Value:0.00}"));
            var topEmoji = item.EmojiLayers.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
            Console.WriteLine($"[{i}] {item.Id} cat={top} emoji={topEmoji} category='{MemoryClassifier.TopName(item.CategoryLayers)}'");
        }

        var dialogue = "Продолжаем ассоциативную память: витрина, чтобы валидировать классификацию. Проверяем реколл против компаньона на RX570.";
        Console.WriteLine($"--- recall (pass 1, no rerank) on: {dialogue} ---");
        var hits = await MemoryRecall.RecallAsync(dialogue, store, endpoint, topX: 5, minScore: 0.05);

        var i2 = 0;
        foreach (var hit in hits)
        {
            var content = hit.Item.Content.Length <= 70 ? hit.Item.Content : hit.Item.Content[..70] + "…";
            Console.WriteLine($"[{i2++}] {hit.Item.Id} [{(hit.Item.CategoryLayers.Count > 0 ? MemoryClassifier.TopName(hit.Item.CategoryLayers) : "?")}] {(hit.Item.CategoryLayers.Count > 0 ? MemoryClassifier.TopEmojiOf(hit.Item.EmojiLayers) : "")} relevance={hit.Score:0.00}: {content}");
        }

        Console.WriteLine("--- recall (SecondPass rerank) ---");
        var reranked = await MemoryRecall.RecallAsync(dialogue, store, endpoint, topX: 5, minScore: 0.05, rerank: true);
        var i3 = 0;
        foreach (var hit in reranked)
        {
            var content = hit.Item.Content.Length <= 70 ? hit.Item.Content : hit.Item.Content[..70] + "…";
            Console.WriteLine($"[{i3++}] {hit.Item.Id} [{(hit.Item.CategoryLayers.Count > 0 ? MemoryClassifier.TopName(hit.Item.CategoryLayers) : "?")}] {(hit.Item.CategoryLayers.Count > 0 ? MemoryClassifier.TopEmojiOf(hit.Item.EmojiLayers) : "")} relevance={hit.Score:0.00}: {content}");
        }
        if (reranked.Count == 0)
        {
            Console.WriteLine("(rerank: None of the above)");
        }

        var pass1Ids = hits.Select(h => h.Item.Id).ToHashSet();
        var passed = hits.Count > 0 && reranked.Count > 0 && pass1Ids.Contains(reranked[0].Item.Id);
        Console.WriteLine($"(pass1 count={hits.Count}, rerank count={reranked.Count}, rerank top-1 in pass1={passed})");
        try { Directory.Delete(dir, recursive: true); } catch { }
        Console.WriteLine(passed ? "PASS" : "FAIL");
        return passed ? 0 : 1;
    }

    /// <summary>
    /// Репро «невозможно откатить чат после HTTP 400 от llamacpp»:
    /// фейковый сервер отвечает 400 на /completion, затем проверяем состояние FSM,
    /// доступность команды отката и что произойдёт после её выполнения.
    /// </summary>
    public static async Task<int> RollbackAfter400()
    {
        var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        var prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }
                try
                {
                    using var requestStream = ctx.Request.InputStream;
                    var buffer = new byte[4096];
                    while (await requestStream.ReadAsync(buffer.AsMemory()) > 0) { }

                    if (ctx.Request.Url?.AbsolutePath == "/completion")
                    {
                        ctx.Response.StatusCode = 400;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.OutputStream.WriteAsync(
                            Encoding.UTF8.GetBytes("""{"error":{"message":"Bad Request"}}"""));
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                    }
                    ctx.Response.Close();
                }
                catch { }
            }
        });

        var vm = new QwenPlayground.App.ViewModels.MainViewModel();
        vm.NewSessionCommand.Execute(null); // уводим в свежую сессию, не трогаем main
        // Режимы и ForceNag убраны из VM (2026-08-22): всегда агент, nag без tool-вызовов отключён.
        vm.ReasoningEffortIndex = 1;
        vm.Endpoint = prefix.TrimEnd('/');
        vm.MaxTokens = 512;
        vm.ContextSize = 32768;
        vm.HeartbeatEnabled = false;
        vm.InputText = "простое сообщение";
        var before = vm.Messages.Count;

        // Ловим момент сброса IsGenerating: если FSM ещё busy, кнопка остаётся серой навсегда
        // (NotifyCanExecuteChanged стреляет раньше, чем переход в Idle).
        var canExecWhenReset = "?";
        var fsmWhenReset = "?";
        System.ComponentModel.PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsGenerating) && !vm.IsGenerating)
            {
                canExecWhenReset = vm.RollbackCommand.CanExecute(vm.Messages.LastOrDefault()).ToString();
                fsmWhenReset = vm.Diagnostics.ChatStateName;
                vm.PropertyChanged -= handler;
            }
        };
        vm.PropertyChanged += handler;

        vm.SendCommand.Execute(null);
        var finished = await WaitCondition(() => !vm.IsGenerating, 15000);
        await Task.Delay(300);
        Console.WriteLine($"finished: {finished}");
        Console.WriteLine($"canExec(Rollback) В МОМЕНТ сброса IsGenerating: {canExecWhenReset} (FSM={fsmWhenReset})");
        Console.WriteLine($"status: '{vm.StatusText}'");
        Console.WriteLine($"messages after 400: {vm.Messages.Count} (before={before})");

        if (vm.Messages.Count == 0)
        {
            Console.WriteLine("SKIP: контекст пуст (ошибка до добавления сообщения)");
            listener.Stop();
            await Task.Delay(200);
            return 2;
        }

        var target = vm.Messages[^1];
        Console.WriteLine($"last message role={target.Role}, CanExecute(Rollback)={vm.RollbackCommand.CanExecute(target)}");
        var canSendAfter = vm.SendCommand.CanExecute(null);
        Console.WriteLine($"CanExecute(Send)={canSendAfter}");
        Console.WriteLine($"IsGenerating={vm.IsGenerating}, IsBusy={vm.IsBusy}");

        if (vm.RollbackCommand.CanExecute(target))
        {
            vm.RollbackCommand.Execute(target);
            Console.WriteLine($"after rollback: messages={vm.Messages.Count}, conversation persisted via SaveCurrent");
        }

        listener.Stop();
        await Task.Delay(200);
        return 0;
    }

    private static async Task<bool> Run(string endpoint, string projectRoot, ChatLog conversation, Func<Trace, bool> assert)
    {
        // Цикл читает конфиг из синглтона настроек: сценарий настраивает его сам
        // (endpoint приходит из argv — гоняем против произвольных машин).
        AppSettings.Get().Endpoint = endpoint;
        AppSettings.Get().ProjectRoot = projectRoot;
        var trace = new Trace();
        var loop = new AgentLoop(new ToolRegistry());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var generation = new QwenPlayground.Core.Inference.GenerationOptions { MaxTokens = 2048 };

        try
        {
            await foreach (var agentEvent in loop.RunAsync(new AgentLoopRequest
            {
                Conversation = conversation,
                Generation = generation,
                MaxIterations = 6,
                CancellationToken = timeout.Token
            }))
            {
                switch (agentEvent)
                {
                    case AssistantMessageEvent assistant:
                        Console.WriteLine("--- assistant ---");
                        if (assistant.Message.Reasoning is { Length: > 0 } reasoning)
                        {
                            Console.WriteLine("[reasoning] " + Truncate(reasoning, 500));
                        }
                        if (assistant.Message.Content.Length > 0)
                        {
                            Console.WriteLine(Truncate(assistant.Message.Content, 500));
                        }
                        break;
                    case ToolCallStartedEvent started:
                        trace.ToolCallsStarted++;
                        Console.WriteLine($">>> tool: {started.Name} {started.Arguments.ToJsonString()}");
                        break;
                    case ToolCallFinishedEvent finished:
                        Console.WriteLine($"<<< result: {Truncate(finished.Result, 300)}");
                        break;
                    case NagEvent nag:
                        Console.WriteLine($"[nag] {nag.Text}");
                        break;
                    case AgentDoneEvent:
                        trace.Finished = true;
                        Console.WriteLine("=== done ===");
                        break;
                    case AgentErrorEvent error:
                        trace.Finished = true;
                        Console.WriteLine($"=== error: {error.Message} ===");
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"EXCEPTION: {exception.Message}");
        }

        var passed = assert(trace);
        if (!passed)
        {
            var generation0 = conversation.LastOrDefault(m => m.Generation is not null)?.Generation;
            if (generation0 is not null)
            {
                Console.WriteLine("=== last prompt (debug) ===");
                Console.WriteLine(Truncate(generation0.Prompt, 3000));
                Console.WriteLine("=== raw output ===");
                Console.WriteLine(Truncate(generation0.RawOutput, 1500));
            }
        }
        Console.WriteLine(passed ? "PASS" : "FAIL");
        return passed;
    }

    private static string CreateProjectDir(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "qwen_harness_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + $"... (+{text.Length - limit} chars)";

    public static async Task<int> CancelContinue()
    {
        var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        var prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var requestNo = 0;
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                try
                {
                    using var requestStream = ctx.Request.InputStream;
                    var buffer = new byte[4096];
                    while (await requestStream.ReadAsync(buffer.AsMemory()) > 0)
                    {
                    }

                    var path = ctx.Request.Url?.AbsolutePath;
                    if (path == "/tokenize")
                    {
                        await WriteJson(ctx, """{"tokens":[]}""");
                    }
                    else if (path == "/api/extra/tokencount")
                    {
                        await WriteJson(ctx, """{"value":0}""");
                    }
                    else if (path == "/v1/completions")
                    {
                        requestNo++;
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "text/event-stream";
                        var chunks = requestNo == 1
                            ? new[] { "Hello ", "world ", "one " }
                            : new[] { "two ", "three ", "four " };
                        foreach (var chunk in chunks)
                        {
                            var payload = JsonSerializer.Serialize(new { choices = new[] { new { text = chunk } } });
                            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes($"data: {payload}\n\n"));
                            await ctx.Response.OutputStream.FlushAsync();
                            await Task.Delay(300);
                        }
                        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
                        await ctx.Response.OutputStream.FlushAsync();
                        ctx.Response.Close();
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }
                catch
                {
                }
            }
        });

        var vm = new QwenPlayground.App.ViewModels.MainViewModel();
        vm.ReasoningEffortIndex = 1;
        vm.Endpoint = prefix.TrimEnd('/');
        vm.MaxTokens = 512;
        vm.ContextSize = 32768;
        vm.Clear();
        vm.InputText = "test";

        vm.SendCommand.Execute(null);
        var started = await WaitCondition(() => vm.IsGenerating, 5000);
        if (!started)
        {
            Console.WriteLine("FAIL: generation did not start");
            listener.Stop();
            return 1;
        }

        await Task.Delay(700);
        vm.CancelCommand.Execute(null);
        await WaitCondition(() => !vm.IsGenerating, 5000);
        var first = vm.Messages[^1].Content;
        Console.WriteLine($"after stop1: '{first}'");

        if (!vm.ContinueCommand.CanExecute(null))
        {
            Console.WriteLine("FAIL: Continue disabled after stop");
            listener.Stop();
            return 1;
        }

        vm.ContinueCommand.Execute(null);
        started = await WaitCondition(() => vm.IsGenerating, 5000);
        if (!started)
        {
            Console.WriteLine("FAIL: continue did not start");
            listener.Stop();
            return 1;
        }

        await Task.Delay(700);
        vm.CancelCommand.Execute(null);
        await WaitCondition(() => !vm.IsGenerating, 5000);
        var second = vm.Messages[^1].Content;
        Console.WriteLine($"after stop2: '{second}'");

        listener.Stop();
        await Task.Delay(200);

        var passed = first.Length > 0 && second.StartsWith(first, StringComparison.Ordinal) && second.Length > first.Length;
        Console.WriteLine(passed ? "PASS" : "FAIL");
        return passed ? 0 : 1;
    }

    private static async Task<bool> WaitCondition(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return condition();
    }

    private static async Task WriteJson(HttpListenerContext ctx, string json)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
        ctx.Response.Close();
    }
}
