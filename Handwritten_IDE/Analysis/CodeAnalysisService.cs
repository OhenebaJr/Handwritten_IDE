using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Handwritten_IDE.Analysis;

public record ValidateRequest(string? Code);

public record CodeDiagnostic(
    string Severity, string Id, string Message,
    int StartLine, int StartColumn, int EndLine, int EndColumn);

public static class CodeAnalysisService
{
    // Only the core .NET libraries (System.*, netstandard, mscorlib) are given to the
    // user's code, not the Windows-specific assemblies this project references.
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        return tpa.Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p))
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase);
            })
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    });

    public static CSharpCompilation CreateCompilation(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code,
            new CSharpParseOptions(LanguageVersion.Latest));

        return CSharpCompilation.Create(
            "UserProgram",
            new[] { tree },
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                nullableContextOptions: NullableContextOptions.Disable));
    }

    public static List<CodeDiagnostic> Validate(string code)
    {
        var compilation = CreateCompilation(code);

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                     || d.Severity == DiagnosticSeverity.Warning)
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return new CodeDiagnostic(
                    d.Severity.ToString(),
                    d.Id,
                    d.GetMessage(),
                    span.StartLinePosition.Line + 1,       // Monaco is 1-based
                    span.StartLinePosition.Character + 1,
                    span.EndLinePosition.Line + 1,
                    span.EndLinePosition.Character + 1);
            })
            .ToList();
    }
}