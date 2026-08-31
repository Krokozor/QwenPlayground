using System.IO;
using QwenPlayground.App.ViewModels;

namespace QwenPlayground.App.Tests;

public sealed class FileDependentCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qpw_fdcache_" + Guid.NewGuid().ToString("N"));

    private string WriteFile(string name, string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void FirstGet_Builds_And_SubsequentGets_ReturnSameInstance()
    {
        var path = WriteFile("dep.txt", "v1");
        var builds = 0;
        var cache = new FileDependentCache<object>([path], () => new object(), initial: null!);

        var first = cache.Get();
        builds++;
        Assert.Same(first, cache.Get());
        Assert.Same(first, cache.Get());
        Assert.Equal(1, builds);
    }

    [Fact]
    public void MtimeChange_TriggersRebuild()
    {
        var path = WriteFile("dep.txt", "v1");
        var cache = new FileDependentCache<string>([path], () => File.ReadAllText(path), initial: null!);
        Assert.Equal("v1", cache.Get());

        File.WriteAllText(path, "v2");
        // Гарантируем другой mtime: файловая система может хранить время с точностью до тика.
        var stamp = DateTime.UtcNow.AddSeconds(2);
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal("v2", cache.Get());
    }

    [Fact]
    public void DisappearAndReappear_TriggersRebuildTwice()
    {
        var path = WriteFile("dep.txt", "v1");
        var counter = 0;
        var cache = new FileDependentCache<string?>([path], () => (++counter).ToString(), initial: null);
        cache.Get();

        File.Delete(path);
        cache.Get(); // пересборка по «файл исчез»

        File.WriteAllText(path, "v2");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        cache.Get(); // пересборка по «файл появился»

        Assert.Equal(3, counter);
    }

    [Fact]
    public void SecondDependencyChange_Rebuilds_EvenWhenFirstUntouched()
    {
        var path1 = WriteFile("a.txt", "1");
        var path2 = WriteFile("b.txt", "1");
        var readSecond = false;
        var cache = new FileDependentCache<string>(
            [path1, path2],
            () => { readSecond = true; return File.ReadAllText(path2); },
            initial: null!);
        cache.Get();

        File.WriteAllText(path2, "2");
        File.SetLastWriteTimeUtc(path2, DateTime.UtcNow.AddSeconds(2));

        Assert.Equal("2", cache.Get());
        Assert.True(readSecond);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
