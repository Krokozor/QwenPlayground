using QwenPlayground.Core.SelfBuild;

namespace QwenPlayground.Core.Heartbeat;

/// <summary>
/// Файловые wake-сигналы — внешний способ разбудить main-агента без UI:
/// любой процесс (включая самого агента через shell) кладёт .txt-файл в
/// &lt;workspace&gt;/wake/, приложение подхватывает его и передаёт агенту
/// как пользовательское сообщение. Имя файла = временная метка, поэтому
/// порядок обработки детерминирован (поименованию).
///
/// Пример из shell:  echo "проверь тесты" &gt; wake\001.txt
/// </summary>
public sealed class WakeSignalStore
{
    private readonly string _directory;

    public WakeSignalStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(SelfBuildPaths.WorkspaceRoot, "wake");
        Directory.CreateDirectory(_directory);
    }

    public int Count => Directory.EnumerateFiles(_directory, "*.txt").Count();

    /// <summary>
    /// Дропнуть сигнал (например, агенту поручили задачу извне). Запись атомарная:
    /// сначала в .tmp, затем переименование — поллер не может прочитать недописанный файл.
    /// </summary>
    public void Send(string text)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var name = stamp + ".txt";
        for (var i = 1; File.Exists(Path.Combine(_directory, name)); i++)
        {
            name = $"{stamp}-{i}.txt"; // два сигнала в одну миллисекунду — не затираем
        }
        var target = Path.Combine(_directory, name);
        var temp = target + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, target); // атомарная публикация
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    /// <summary>Забирает самый старый сигнал и удаляет файл. null, если очередь пуста.</summary>
    public (string Source, string Text)? TakeNext()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                // Файл исчез/залочен между перечислением и чтением — берём следующий.
                continue;
            }
            File.Delete(file);
            return (Path.GetFileNameWithoutExtension(file), text.Trim());
        }
        return null;
    }
}
