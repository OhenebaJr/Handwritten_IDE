using Handwritten_IDE.Ink;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

//app.MapGet("/", () => "Hello World!");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/recognizers", () => InkRecognitionService.ListRecognizers());
app.MapPost("/api/recognize", async (RecognizeRequest request) => 
await InkRecognitionService.RecognizeAsync(request));

app.Run();
