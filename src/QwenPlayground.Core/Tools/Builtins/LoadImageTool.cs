using System.IO;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tools.Builtins;

/// <summary>
/// Грузит изображения из файлов и прикрепляет их к СВОЕМУ tool-ответу: в следующем рендере
/// модель увидит картинки прямо в ответе инструмента (tool-ответ рендерится внутри user-блока,
/// поэтому маркеры валидны для сервера). Замыкает круг «посмотрел → remove_attachments → чисто».
///
/// Связь «чат ⇔ сообщение ⇔ инструмент» реализована через этап финализации
/// (<see cref="AgentTool.FinalizeAsync"/>): во время ExecuteAsync инструмент ещё не знает
/// стабильный ID своего будущего tool-сообщения, поэтому только валидирует пути и запоминает их.
/// Как только AgentLoop добавил результат в разговор и присвоил ID — вызывается FinalizeAsync,
/// которая копирует файлы в каталог msg_&lt;id&gt; этой сессии.
/// </summary>
[Tool("load_image",
    "Load one or more images from files and attach them to this tool response so you can SEE them " +
    "in the next render. Pass absolute or workspace-relative file paths. After you are done looking, " +
    "call remove_attachments to free context. Use for debugging UI, inspecting screenshots, or reviewing " +
    "known images.")]
public sealed class LoadImageTool : AgentTool
{
    [ToolParameter("File paths to images (jpg/png). Absolute or workspace-relative.", Required = true)]
    public string[] Paths { get; set; } = Array.Empty<string>();

    private readonly List<string> _staged = new();

    public override Task<string> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (Paths is null || Paths.Length == 0)
            return Task.FromResult("No paths provided.");

        if (context.SessionDir is null)
            return Task.FromResult("Error: no session directory (cannot store artifacts).");

        var errors = new List<string>();

        foreach (var path in Paths)
        {
            string source;
            try
            {
                source = context.ResolvePath(path);
            }
            catch (Exception exception)
            {
                errors.Add($"{path}: {exception.Message}");
                continue;
            }
            if (!File.Exists(source))
            {
                errors.Add($"{path}: not found");
                continue;
            }
            _staged.Add(source);
        }

        if (_staged.Count == 0)
            return Task.FromResult($"Failed to load any image. Errors: {string.Join("; ", errors)}");

        return Task.FromResult(
            $"Loaded {_staged.Count} image(s). They will appear in this tool response in the next render. " +
            $"Call remove_attachments when done to free context.");
    }

    public override Task FinalizeAsync(ToolContext context, int messageId, CancellationToken cancellationToken)
    {
        if (_staged.Count == 0 || messageId <= 0 || context.SessionDir is null)
            return Task.CompletedTask;

        var store = new MessageMetaStore(context.SessionDir);
        try
        {
            foreach (var source in _staged)
            {
                store.AddArtifact(messageId, source);
            }
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
        return Task.CompletedTask;
    }
}