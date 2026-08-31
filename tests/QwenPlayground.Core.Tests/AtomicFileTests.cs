using System.IO;
using QwenPlayground.Core.Serialization;

namespace QwenPlayground.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qpw_atomic_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_CreatesFile_WithoutTempLeftover()
    {
        var path = Path.Combine(_dir, "nested", "state.json");

        AtomicFile.WriteAllText(path, "{\"v\":1}");

        Assert.True(File.Exists(path));
        Assert.Equal("{\"v\":1}", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Write_OverwritesExistingFileAtomically()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_Failure_PreservesPreviousContent()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "precious");
        // Целевой файл залочен монопольно → File.Replace упадёт, прежнее содержимое цело.
        // (Имена temp уникальны, поэтому коллизией по имени временный файл сломать нельзя.)
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var threw = Record.Exception(() => AtomicFile.WriteAllText(path, "boom"));

            Assert.NotNull(threw);
        }
        Assert.Equal("precious", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
