using System.Drawing;
using System.Net;
using System.Numerics;
using Windows.Foundation;
using Windows.UI.Input.Inking;

namespace Handwritten_IDE.Ink
{
    public record PointDto(double X, double Y, long T, float Pressure);
    public record StrokeDto(int Id, string PointerType, int Row, List<PointDto> Points);
    public record RecognizeRequest(List<StrokeDto> Strokes);

    public record WordResult(List<string> Candidates, double X, double Y, double Width, double Height);
    public record RowResult(int Row, string Text, List<WordResult> Words);

    public static class InkRecognitionService
    {
        public static List<string> ListRecognizers()
        {
            var container = new InkRecognizerContainer();
            return container.GetRecognizers().Select(r => r.Name).ToList();
        }

        public static async Task<List<RowResult>> RecognizeAsync(RecognizeRequest request)
        {
            var results = new List<RowResult>();

            foreach (var rowGroup in request.Strokes.GroupBy(s => s.Row).OrderBy(g => g.Key))
            {
                var strokeContainer = new InkStrokeContainer();
                var builder = new InkStrokeBuilder();

                foreach (var s in rowGroup.OrderBy(s => s.Id))
                {
                    // The browser sends each point twice, so drop consecutive duplicates
                    var points = new List<InkPoint>();
                    PointDto? prev = null;
                    foreach (var p in s.Points)
                    {
                        if (prev != null && prev.X == p.X && prev.Y == p.Y && prev.T == p.T) continue;
                        points.Add(new InkPoint(new Windows.Foundation.Point(p.X, p.Y), p.Pressure));
                        prev = p;
                    }
                    if (points.Count < 2) continue;

                    strokeContainer.AddStroke(builder.CreateStrokeFromInkPoints(points, Matrix3x2.Identity));
                }

                var container = new InkRecognizerContainer();
                var recognized = await container.RecognizeAsync(strokeContainer, InkRecognitionTarget.All);

                var words = recognized.Select(r =>
                {
                    var b = r.BoundingRect;
                    return new WordResult(r.GetTextCandidates().ToList(), b.X, b.Y, b.Width, b.Height);
                }).ToList();

                var text = string.Join(" ", words.Select(w => w.Candidates.FirstOrDefault() ?? ""));
                results.Add(new RowResult(rowGroup.Key, text, words));
            }

            return results;
        }
    }
}
