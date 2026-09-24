using System.IO;
using QwenPlayground.Core.Memory;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// Записная книжка проекта (refactoring.md): свежий клон — нейтральный шаблон создаётся
/// сам (паттерн TrajectoryStore), существующий файл не перезаписывается.
/// </summary>
public sealed class ProjectNotebookStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qpw_nb_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFile_CreatesNeutralTemplate()
    {
        Directory.CreateDirectory(_dir);

        var text = new ProjectNotebookStore(_dir).Load();

        Assert.True(File.Exists(Path.Combine(_dir, ProjectNotebookStore.FileName)));
        Assert.Contains("Backlog", text);
        Assert.Contains("Changelog", text);
    }

    [Fact]
    public void ExistingFile_IsNotOverwritten()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, ProjectNotebookStore.FileName);
        File.WriteAllText(path, "мои собственные заметки");

        var text = new ProjectNotebookStore(_dir).Load();

        Assert.Equal("мои собственные заметки", text);
    }

    [Fact]
    public void EmptyFile_IsTreatedAsMissing_AndSeeded()
    {
        // Пустой файл (нулевой байт от клона без LFS/разрешений) — как отсутствие:
        // Load() возвращает шаблон, но НЕ затирает файл (Trim пустого = пусто).
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, ProjectNotebookStore.FileName);
        File.WriteAllText(path, string.Empty);

        var text = new ProjectNotebookStore(_dir).Load();

        Assert.Contains("Backlog", text);
        Assert.Equal(string.Empty, File.ReadAllText(path)); // файл не затёрт
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
