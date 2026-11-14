using Handwritten_IDE.Analysis;
using Handwritten_IDE.Execution;
using Handwritten_IDE.Ink;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

//app.MapGet("/", () => "Hello World!");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/recognizers", () => InkRecognitionService.ListRecognizers());
app.MapPost("/api/recognize", async (RecognizeRequest request) =>
await InkRecognitionService.RecognizeAsync(request));
app.MapPost("/api/validate", (ValidateRequest request) =>
    CodeAnalysisService.Validate(request.Code ?? ""));

app.Map("/ws/run", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    await RunService.HandleAsync(ws, context.RequestAborted);
});

app.Run();