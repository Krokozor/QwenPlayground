using System.IO;
using QwenPlayground.Core.Memory;
using QwenPlayground.Core.Settings;
using QwenPlayground.Core.Tools;
using QwenPlayground.Core.Tools.Builtins;

namespace QwenPlayground.Core.Tests;

/// <summary>
/// memory_add — базовый инструмент-«записка»: рекламируется и работает ВСЕГДА, независимо
/// от мастер-переключателя памяти. Выкл памяти гасит «умную» часть (реколл/слои/дедуп и
/// соответствующие тулы), но не саму запись факта. Мутации MemoryEnabled — только in-memory
/// (не через Update), чтобы тест не писал в общий settings.json, который читает приложение.
/// </summary>
// Коллекция memory-settings: сериализация с другими классами, мутирующими
// AppSettings.MemoryEnabled (глобальный синглтон, xUnit крутит классы параллельно).
[Collection("memory-settings")]
public sealed class MemoryToolGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qpw_memgate_" + Guid.NewGuid().ToString("N"));

    private ToolContext Context() => new(_dir);

    [Fact]
    public void ShouldAdvertise_BasicMemoryTool_Always_SmartToolsGated()
    {
        var prev = AppSettings.Get().MemoryEnabled;
        AppSettings.Get().MemoryEnabled = false;
        try
        {
            Assert.True(MemoryToolGate.ShouldAdvertise("memory_add"));  // базовый — всегда
            Assert.False(MemoryToolGate.ShouldAdvertise("memory_list")); // «умный» — выкл
            Assert.True(MemoryToolGate.ShouldAdvertise("read_file"));    // не память — всегда
        }
        finally
        {
            AppSettings.Get().MemoryEnabled = prev;
        }
    }

    [Fact]
    public void ShouldAdvertise_MemoryTools_WhenEnabled()
    {
        var prev = AppSettings.Get().MemoryEnabled;
        AppSettings.Get().MemoryEnabled = true;
        try
        {
            Assert.True(MemoryToolGate.ShouldAdvertise("memory_add"));
            Assert.True(MemoryToolGate.ShouldAdvertise("memory_list"));
        }
        finally
        {
            AppSettings.Get().MemoryEnabled = prev;
        }
    }

    [Fact]
    public async Task MemoryAdd_SavesFact_WhenMemoryDisabled()
    {
        Directory.CreateDirectory(_dir);
        var prev = AppSettings.Get().MemoryEnabled;
        AppSettings.Get().MemoryEnabled = false; // сценарий переезда: память выключена
        try
        {
            var result = await new MemoryAddTool(_dir) { Content = "факт записки" }
                .ExecuteAsync(Context(), CancellationToken.None);

            // Не «Memory is disabled», а подтверждение сохранения.
            Assert.DoesNotContain("Memory is disabled", result);
            Assert.Contains("Memory saved", result);
            // Факт реально записан в каталог (без слоёв — классификация пропущена).
            var items = new MemoryStore(_dir).List();
            var item = Assert.Single(items);
            Assert.False(item.HasSemanticLayers);
        }
        finally
        {
            AppSettings.Get().MemoryEnabled = prev;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
