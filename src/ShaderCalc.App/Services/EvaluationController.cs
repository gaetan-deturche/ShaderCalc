using System.Windows.Threading;
using ShaderCalc.Reference;

namespace ShaderCalc.App.Services;

/// <summary>
/// Evaluates the worksheets in the background after edits (debounced, a newer request cancels the older one), then
/// checks every result against DXC + WARP, the active tab first. Events are raised on the UI thread.
/// </summary>
internal sealed class EvaluationController
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<IReadOnlyList<WorksheetDocument>> _snapshot;
    private readonly Func<string?> _activeDocument;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cancellation;

    public EvaluationController(Dispatcher dispatcher, Func<IReadOnlyList<WorksheetDocument>> snapshot, Func<string?> activeDocument)
    {
        _dispatcher = dispatcher;
        _snapshot = snapshot;
        _activeDocument = activeDocument;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => RunNow(), dispatcher) { IsEnabled = false };
    }

    public event Action<WorksheetResult>? Evaluated;

    public event Action<WorksheetLine, ReferenceOutcome>? ReferenceChecked;

    /// <summary>Reference progress: lines checked, lines to check.</summary>
    public event Action<int, int>? ReferenceProgress;

    public event Action<Exception>? Failed;

    public void Request()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public async void RunNow()
    {
        _debounce.Stop();
        _cancellation?.Cancel();
        CancellationTokenSource cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IReadOnlyList<WorksheetDocument> documents = _snapshot();

        WorksheetResult result;
        try
        {
            result = await Task.Run(() => Worksheet.Evaluate(documents, options: new Evaluation.EvaluationOptions { Cancellation = cancellation.Token }),
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Failed?.Invoke(exception);
            return;
        }
        if (cancellation.IsCancellationRequested)
        {
            return;
        }
        Evaluated?.Invoke(result);

        string? active = _activeDocument();
        List<string> order = documents.Select(document => document.Name).ToList();
        List<WorksheetLine> lines = result.Lines.Where(line => line.Value != null)
            .OrderBy(line => line.Document == active ? -1 : order.IndexOf(line.Document))
            .ThenBy(line => line.Line)
            .ToList();
        ReferenceProgress?.Invoke(0, lines.Count);
        try
        {
            await Task.Run(() =>
            {
                for (int index = 0; index < lines.Count; index++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    WorksheetLine line = lines[index];
                    ReferenceOutcome outcome;
                    try
                    {
                        outcome = ReferenceChecker.Check(line.Result);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        outcome = new ReferenceOutcome(ReferenceVerdict.NotChecked, null, 0, false, exception.Message, string.Empty, null);
                    }
                    int done = index + 1;
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (!cancellation.IsCancellationRequested)
                        {
                            ReferenceChecked?.Invoke(line, outcome);
                            ReferenceProgress?.Invoke(done, lines.Count);
                        }
                    });
                }
            }, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
