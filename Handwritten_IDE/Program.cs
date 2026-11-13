using Handwritten_IDE.Analysis;
using Handwritten_IDE.Ink;
using Handwritten_IDE.Execution;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

//app.MapGet("/", () => "Hello World!");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/recognizers", () => InkRecognitionService.ListRecognizers());

app.MapPost("/api/recognize", async (RecognizeRequest request) => 
await InkRecognitionService.RecognizeAsync(request));

app.MapPost("/api/validate", (ValidateRequest request) =>
    CodeAnalysisService.Validate(request.Code ?? ""));

app.MapPost("/api/run", async (RunRequest request) =>
    await RunService.RunAsync(request.Code ?? ""));

app.Run();
