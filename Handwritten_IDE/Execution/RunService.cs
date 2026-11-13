using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Handwritten_IDE.Analysis;

namespace Handwritten_IDE.Execution;

public record RunRequest(string? Code);

// Status: ok | compile-error | runtime-error | timeout | unavailable
public record RunResult(
    string Status,
    string Output,
    bool OutputTruncated,
    string? Error,
    List<CodeDiagnostic> Diagnostics);

public static class RunService
{
    private const int TimeoutSeconds = 10;
    private const int MaxOutputChars = 100_000;

    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static volatile bool _stuck;

    public static async Task<RunResult> RunAsync(string code)
    {
        if (_stuck)
            return Unavailable("An earlier program did not finish and may still be running inside the server. Restart the app to run code again.");

        if (!await RunLock.WaitAsync(0))
            return Unavailable("Another program is still running.");

        try
        {
            return await Task.Run(() => Execute(code));
        }
        finally
        {
            RunLock.Release();
        }
    }

    private static RunResult Execute(string code)
    {
        var compilation = CodeAnalysisService.CreateCompilation(code);
        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);

        if (!emit.Success)
            return new RunResult("compile-error", "", false, null,
                CodeAnalysisService.Validate(code));

        peStream.Position = 0;
        var context = new AssemblyLoadContext("UserProgram", isCollectible: true);
        var output = new LimitedWriter(MaxOutputChars);
        string? error = null;

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;

        try
        {
            var assembly = context.LoadFromStream(peStream);
            var entry = assembly.EntryPoint;
            if (entry is null)
                return new RunResult("compile-error", "", false,
                    "The program has no entry point.", new List<CodeDiagnostic>());

            // Everything the program prints goes into 'output'; ReadLine returns null for now
            Console.SetOut(output);
            Console.SetError(output);
            Console.SetIn(TextReader.Null);

            var thread = new Thread(() =>
            {
                try
                {
                    var args = entry.GetParameters().Length == 0
                        ? null
                        : new object[] { Array.Empty<string>() };
                    var result = entry.Invoke(null, args);
                    if (result is Task task) task.GetAwaiter().GetResult();   // async Main
                }
                catch (TargetInvocationException ex) { error = Describe(ex.InnerException ?? ex); }
                catch (Exception ex) { error = Describe(ex); }
            })
            { IsBackground = true };

            thread.Start();
            var finished = thread.Join(TimeSpan.FromSeconds(TimeoutSeconds));

            if (!finished)
            {
                _stuck = true;   // the thread cannot be stopped inside the same process
                return new RunResult("timeout", output.ToString(), output.Truncated,
                    $"The program did not finish within {TimeoutSeconds} seconds. It may still be running inside the server, so restart the app before running code again.",
                    new List<CodeDiagnostic>());
            }

            context.Unload();
            return new RunResult(error is null ? "ok" : "runtime-error",
                output.ToString(), output.Truncated, error, new List<CodeDiagnostic>());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Console.SetIn(originalIn);
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().FullName}: {ex.Message}";

    private static RunResult Unavailable(string message) =>
        new("unavailable", "", false, message, new List<CodeDiagnostic>());
}

// Collects console output but stops growing after a limit
internal sealed class LimitedWriter : TextWriter
{
    private readonly StringBuilder _sb = new();
    private readonly int _max;

    public LimitedWriter(int max) => _max = max;

    public bool Truncated { get; private set; }
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_sb)
        {
            if (_sb.Length < _max) _sb.Append(value);
            else Truncated = true;
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_sb)
        {
            var room = _max - _sb.Length;
            if (value.Length <= room) _sb.Append(value);
            else
            {
                if (room > 0) _sb.Append(value, 0, room);
                Truncated = true;
            }
        }
    }

    public override string ToString()
    {
        lock (_sb) return _sb.ToString();
    }
}