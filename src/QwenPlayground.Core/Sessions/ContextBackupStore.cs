using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Sessions;

/// <summary>
/// Бэкап сессии перед компакцией: ПРЯМОЕ копирование исходных файлов, без пере-сериализации.
/// Каждая сессия — каталог sessions/&lt;id&gt;/ (chat.json, слои, артефакты) и копируется
/// целиком; legacy-файл sessions/&lt;id&gt;.json поддерживается для отката старой структуры.
/// Бэкап кладётся в backups/&lt;sessionId&gt;-&lt;таймстамп&gt;.
/// Без успешного бэкапа компакция не запускается (см. MainViewModel.CompactAsync).
/// </summary>
public sealed class ContextBackupStore
{
    public const int KeepLast = 5;

    private readonly string _sessionsRoot;
    private readonly string _backupsRoot;

    public ContextBackupStore(string sessionsRoot, string? backupsRoot = null)
    {
        _sessionsRoot = sessionsRoot;
        _backupsRoot = backupsRoot ?? Path.Combine(SelfBuildPaths.WorkspaceRoot, "backups");
        System.IO.Directory.CreateDirectory(_backupsRoot);
    }

    public string Directory => _backupsRoot;

    /// <summary>Снимок сессии: копирует папку (main-агент) или файл (обычная сессия). Возвращает путь к бэкапу.</summary>
    public string Save(string sessionId)
    {
        var sessionDir = Path.Combine(_sessionsRoot, sessionId);
        var sessionFile = Path.Combine(_sessionsRoot, sessionId + ".json");
        var stamp = $"{sessionId}-{DateTime.Now:yyyyMMdd-HHmmss}";
        string destination;
        if (System.IO.Directory.Exists(sessionDir))
        {
            destination = Path.Combine(_backupsRoot, stamp);
            CopyDirectory(sessionDir, destination);
        }
        else if (File.Exists(sessionFile))
        {
            destination = Path.Combine(_backupsRoot, stamp + ".json");
            File.Copy(sessionFile, destination, overwrite: true);
        }
        else
        {
            throw new FileNotFoundException($"Сессия {sessionId} не найдена: {sessionDir} или {sessionFile}");
        }
        GC(sessionId);
        return destination;
    }

    /// <summary>Восстановление сессии из бэкапа: возвращает файлы на место в sessions/. Путь к результату.</summary>
    public string Restore(string backupPath)
    {
        var isFolder = System.IO.Directory.Exists(backupPath);
        var stem = Path.GetFileNameWithoutExtension(backupPath);
        // Имя: <sessionId>-<yyyyMMdd-HHmmss>[.json] — в суффиксе таймстампа ровно 15 символов + разделитель.
        var sessionId = stem.Length > 16 ? stem[..^16] : stem;
        var target = Path.Combine(_sessionsRoot, isFolder ? sessionId : sessionId + ".json");
        if (isFolder)
        {
            if (System.IO.Directory.Exists(target))
            {
                System.IO.Directory.Delete(target, recursive: true);
            }
            CopyDirectory(backupPath, target);
        }
        else
        {
            File.Copy(backupPath, target, overwrite: true);
        }
        return target;
    }

    public IReadOnlyList<string> List(string sessionId)
    {
        return Entries(sessionId).ToList();
    }

    /// <summary>Оставляет последние KeepLast бэкапов сессии (по имени = по времени).</summary>
    public void GC(string sessionId)
    {
        var stale = Entries(sessionId).Skip(KeepLast).ToList();
        foreach (var path in stale)
        {
            try
            {
                if (System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // занят файлом — оставим до следующего раза
            }
        }
    }

    /// <summary>Бэкапы сессии (новейший первый): каталоги и файлы с префиксом &lt;sessionId&gt;-.</summary>
    private IEnumerable<string> Entries(string sessionId)
    {
        if (!System.IO.Directory.Exists(_backupsRoot))
        {
            yield break;
        }
        var prefix = sessionId + "-";
        var entries = System.IO.Directory.EnumerateFileSystemEntries(_backupsRoot)
            .Where(e => Path.GetFileName(e).StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(e => e);
        foreach (var entry in entries)
        {
            yield return entry;
        }
    }

    /// <summary>Имя бэкапа (без таймстамп-суффикса) — используется тестами и отладкой.</summary>
    public static string? BaseName(string backupPath)
    {
        var stem = Path.GetFileNameWithoutExtension(backupPath);
        return stem.Length > 16 ? stem[..^16] : stem;
    }

    private static void CopyDirectory(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in System.IO.Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}