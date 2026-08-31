using Microsoft.CodeAnalysis;

namespace QwenPlayground.Core.Roslyn;

/// <summary>
/// Валидация после правки: после каждого изменения .cs файла собирает ошибки
/// компиляции по ВСЕМ проектам солюшена, а не только по отредактированному файлу.
/// Так ломка в других файлах (например, смена сигнатуры метода, которую используют
/// вызывающие стороны) видна сразу в результате edit_file, а не после rebuild_self.
/// Общий workspace уже тёплый, поэтому полная проверка дёшева по сравнению со сборкой.
/// </summary>
public static class EditDiagnostics
{
    private const int MaxErrors = 50;

    public static async Task<string> AppendRoslynErrorsAsync(string fullPath, string toolResult, CancellationToken cancellationToken)
    {
        if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return toolResult;
        }
        try
        {
            var solution = await RoslynService.Shared.GetSolutionAsync(cancellationToken);
            var errors = new List<string>();
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation is null)
                {
                    continue;
                }
                foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
                {
                    if (diagnostic.Severity != DiagnosticSeverity.Error)
                    {
                        continue;
                    }
                    var position = diagnostic.Location.GetLineSpan();
                    var path = position.Path is not null
                        ? Path.GetRelativePath(SelfBuild.SelfBuildPaths.WorkspaceRoot, position.Path)
                        : "?";
                    errors.Add($"{diagnostic.Id} {path}:{position.StartLinePosition.Line + 1}: {diagnostic.GetMessage()}");
                    if (errors.Count >= MaxErrors)
                    {
                        break;
                    }
                }
                if (errors.Count >= MaxErrors)
                {
                    break;
                }
            }
            return errors.Count > 0
                ? toolResult + "\n[roslyn errors]\n" + string.Join('\n', errors)
                : toolResult;
        }
        catch (OperationCanceledException)
        {
            // Отмена после записи файла не должна превращаться в «успех без диагностики».
            throw;
        }
        catch
        {
            // Workspace не готов (первый тёплый ап ещё строится и т.п.) — диагностика
            // недоступна, отдаём результат без неё: это best-effort надстройка.
            return toolResult;
        }
    }
}
