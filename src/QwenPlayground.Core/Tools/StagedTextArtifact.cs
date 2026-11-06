using QwenPlayground.Core.Security;
using QwenPlayground.Core.SelfBuild;
using QwenPlayground.Core.Sessions;

namespace QwenPlayground.Core.Tools;

/// <summary>Построчный полный вывод инструмента, привязываемый к сообщению на FinalizeAsync.</summary>
internal sealed class StagedTextArtifact : IDisposable
{
    private readonly string _fileName;
    private string? _pendingPath;
    private StreamWriter? _writer;
    private bool _keepForFinalize;

    public string? PendingPath => _pendingPath;

    public StagedTextArtifact(ToolContext context, string fileName)
    {
        _fileName = fileName;
        if (context.SessionDir is null)
        {
            return;
        }
        var pendingDir = Path.Combine(context.SessionDir, "artifacts", "pending");
        Directory.CreateDirectory(pendingDir);
        _pendingPath = Path.Combine(pendingDir, Guid.NewGuid().ToString("N") + ".txt");
    }

    public void WriteLine(string text)
    {
        if (_pendingPath is null)
        {
            return;
        }
        _writer ??= new StreamWriter(_pendingPath, append: false);
        _writer.WriteLine(SecretRedactor.Redact(text));
    }

    public string Finish(int messageId, ToolContext context)
    {
        Close();
        if (_pendingPath is null || context.SessionDir is null)
        {
            return string.Empty;
        }
        var dir = Path.Combine(new MessageMetaStore(context.SessionDir).ArtifactsDir(messageId), "attachments");
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, _fileName);
        File.Move(_pendingPath, full, overwrite: true);
        _pendingPath = null;
        return full;
    }

    /// <summary>
    /// Маркер вложения в tool-выводе. Путь — абсолютный: маркер перистируется в сообщении
    /// и должен резолвиться независимо от рабочей папки сессии (set_session_root не ломает
    /// старые сообщения). ResolvePath принимает абсолютные пути как есть.
    /// </summary>
    public static string AttachmentTag(string fullPath)
    {
        return $"<attachment path=\"{fullPath}\">";
    }

    public void KeepForFinalize() => _keepForFinalize = true;

    public void Discard()
    {
        Close();
        if (_pendingPath is not null && File.Exists(_pendingPath))
        {
            File.Delete(_pendingPath);
        }
        _pendingPath = null;
    }

    public void Dispose()
    {
        if (!_keepForFinalize)
        {
            Discard();
        }
    }

    private void Close()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
