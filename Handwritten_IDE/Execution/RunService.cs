using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Handwritten_IDE.Analysis;

namespace Handwritten_IDE.Execution;

// Message from the browser: type "run" (with Code) or "input" (with Text)
public record ClientMsg(string? Type, string? Code, string? Text);

// Message to the browser. Type: output | input-request | finished
// Status (finished only): ok | compile-error | runtime-error | timeout | unavailable
public record Msg(
    string Type,
    string? Text = null,
    string? Status = null,
    string? Error = null,
    bool Truncated = false,
    List<CodeDiagnostic>? Diagnostics = null);

public static class RunService
{
    private const int TimeoutSeconds = 10;      // running time, waiting for input not counted
    private const int MaxOutputChars = 100_000;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static volatile bool _stuck;

    // One call = one WebSocket session = one program run
    public static async Task HandleAsync(WebSocket ws, CancellationToken requestAborted)
    {
        var first = await ReceiveTextAsync(ws, requestAborted);
        if (first is null) return;

        ClientMsg? request = null;
        try { request = JsonSerializer.Deserialize<ClientMsg>(first, Json); } catch (JsonException) { }

        var outgoing = Channel.CreateUnbounded<Msg>();
        var sender = SendLoopAsync(ws, outgoing.Reader);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        Task? receiver = null;

        if (request?.Type != "run" || string.IsNullOrWhiteSpace(request.Code))
        {
            outgoing.Writer.TryWrite(Unavailable("Expected a run message with code."));
        }
        else if (_stuck)
        {
            outgoing.Writer.TryWrite(Unavailable(
                "An earlier program did not finish and may still be running inside the server. Restart the app to run code again."));
        }
        else if (!await RunLock.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            outgoing.Writer.TryWrite(Unavailable("Another program is still running."));
        }
        else
        {
            var input = Channel.CreateUnbounded<string>();
            receiver = ReceiveLoopAsync(ws, input.Writer, cts.Token);
            try
            {
                await Task.Run(() => Execute(request.Code, outgoing.Writer, input.Reader));
            }
            finally
            {
                RunLock.Release();
            }
        }

        // Deliver everything that is queued, then close our side
        outgoing.Writer.TryComplete();
        await sender;
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch { }

        // Give the browser a moment to answer the close, then stop listening
        if (receiver is not null)
        {
            await Task.WhenAny(receiver, Task.Delay(2000));
            cts.Cancel();
            try { await receiver; } catch { }
        }
    }

    private static void Execute(string code, ChannelWriter<Msg> outgoing, ChannelReader<string> input)
    {
        var compilation = CodeAnalysisService.CreateCompilation(code);
        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);

        if (!emit.Success)
        {
            outgoing.TryWrite(new Msg("finished", Status: "compile-error",
                Diagnostics: CodeAnalysisService.Validate(code)));
            return;
        }

        peStream.Position = 0;
        var context = new AssemblyLoadContext("UserProgram", isCollectible: true);
        var output = new ConsoleOutputWriter(outgoing, MaxOutputChars);
        var reader = new InteractiveReader(outgoing, input);
        string? error = null;

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;

        try
        {
            var assembly = context.LoadFromStream(peStream);
            var entry = assembly.EntryPoint;
            if (entry is null)
            {
                outgoing.TryWrite(new Msg("finished", Status: "compile-error",
                    Error: "The program has no entry point."));
                return;
            }

            Console.SetOut(output);
            Console.SetError(output);
            Console.SetIn(reader);

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

            // Watchdog: only time spent running counts, not time waiting for input
            var busyMs = 0;
            while (!thread.Join(100))
            {
                if (!reader.Waiting) busyMs += 100;
                if (busyMs >= TimeoutSeconds * 1000)
                {
                    _stuck = true;   // the thread cannot be stopped inside the same process
                    outgoing.TryWrite(new Msg("finished", Status: "timeout",
                        Error: $"The program ran for {TimeoutSeconds} seconds without finishing (time waiting for input is not counted). It may still be running inside the server, so restart the app before running code again.",
                        Truncated: output.Truncated));
                    return;
                }
            }

            context.Unload();
            outgoing.TryWrite(new Msg("finished",
                Status: error is null ? "ok" : "runtime-error",
                Error: error,
                Truncated: output.Truncated));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Console.SetIn(originalIn);
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().FullName}: {ex.Message}";

    private static Msg Unavailable(string message) =>
        new("finished", Status: "unavailable", Error: message);

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > 1_000_000) return null;
            if (result.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    // Reads the lines the user types while the program runs
    private static async Task ReceiveLoopAsync(WebSocket ws, ChannelWriter<string> input, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var text = await ReceiveTextAsync(ws, ct);
                if (text is null) break;
                var msg = JsonSerializer.Deserialize<ClientMsg>(text, Json);
                if (msg?.Type == "input") input.TryWrite(msg.Text ?? "");
            }
        }
        catch { }                       // connection closed or cancelled
        finally { input.TryComplete(); } // a waiting ReadLine now gets null instead of hanging
    }

    // Sends queued messages; consecutive output pieces are merged into one message
    private static async Task SendLoopAsync(WebSocket ws, ChannelReader<Msg> reader)
    {
        var pending = new StringBuilder();

        async Task FlushAsync()
        {
            if (pending.Length == 0) return;
            await SendAsync(ws, new Msg("output", pending.ToString()));
            pending.Clear();
        }

        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var m))
                {
                    if (m.Type == "output") { pending.Append(m.Text); continue; }
                    await FlushAsync();
                    await SendAsync(ws, m);
                }
                await FlushAsync();
            }
        }
        catch { }   // browser disconnected
    }

    private static Task SendAsync(WebSocket ws, Msg msg)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
        return ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}

// Console.Out / Console.Error of the running program: sends text to the browser, with a size limit
internal sealed class ConsoleOutputWriter : TextWriter
{
    private readonly ChannelWriter<Msg> _out;
    private readonly int _max;
    private readonly object _lock = new();
    private int _count;

    public ConsoleOutputWriter(ChannelWriter<Msg> output, int max)
    {
        _out = output;
        _max = max;
    }

    public bool Truncated { get; private set; }
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => Add(value.ToString());
    public override void Write(string? value) => Add(value);
    public override void Write(char[] buffer, int index, int count) => Add(new string(buffer, index, count));

    private void Add(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_lock)
        {
            var room = _max - _count;
            if (room <= 0) { Truncated = true; return; }
            if (text.Length > room) { text = text.Substring(0, room); Truncated = true; }
            _count += text.Length;
        }
        _out.TryWrite(new Msg("output", text));
    }
}

// Console.In of the running program: asks the browser for a line and waits for it
internal sealed class InteractiveReader : TextReader
{
    private readonly ChannelWriter<Msg> _out;
    private readonly ChannelReader<string> _in;
    private string _rest = "";

    public InteractiveReader(ChannelWriter<Msg> output, ChannelReader<string> input)
    {
        _out = output;
        _in = input;
    }

    public volatile bool Waiting;

    private string? RequestLine()
    {
        _out.TryWrite(new Msg("input-request"));
        Waiting = true;
        try
        {
            while (true)
            {
                if (!_in.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) return null;   // input closed
                if (_in.TryRead(out var line)) return line;
            }
        }
        finally { Waiting = false; }
    }

    public override string? ReadLine()
    {
        if (_rest.Length > 0)
        {
            var r = _rest.TrimEnd('\n');
            _rest = "";
            return r;
        }
        return RequestLine();
    }

    public override int Read()
    {
        if (_rest.Length == 0)
        {
            var line = RequestLine();
            if (line is null) return -1;
            _rest = line + "\n";
        }
        var c = _rest[0];
        _rest = _rest.Substring(1);
        return c;
    }

    public override int Peek() => _rest.Length > 0 ? _rest[0] : -1;
}