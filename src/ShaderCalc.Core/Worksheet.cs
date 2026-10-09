using System.Diagnostics;
using ShaderCalc.Binding;
using ShaderCalc.Evaluation;
using ShaderCalc.Syntax;
using ShaderCalc.Units;

namespace ShaderCalc;

/// <summary>One worksheet file (a tab): its name, used in diagnostics, and its text.</summary>
public sealed record WorksheetDocument(string Name, string Text);

/// <summary>The outcome of one top-level line, to show at the end of its statement (1-based <see cref="Line"/>).</summary>
public sealed record WorksheetLine(string Document, int Line, SourceSpan Span, LineResult Result)
{
    public Value? Value => Result.Value;
}

public sealed record WorksheetResult(BoundProgram Program, IReadOnlyList<WorksheetLine> Lines, IReadOnlyList<Diagnostic> Diagnostics, TimeSpan Duration)
{
    public IEnumerable<WorksheetLine> LinesOf(string document) => Lines.Where(line => line.Document == document);

    public IEnumerable<Diagnostic> DiagnosticsOf(string document) => Diagnostics.Where(diagnostic => diagnostic.Source == document);
}

/// <summary>
/// Evaluates worksheet files as one program, like headers included in tab order: declarations (functions, structs,
/// macros, globals) are shared by every file, top-level lines run in file then line order, and their variables are
/// globals later lines and functions can read. A line break ends a top-level line.
/// </summary>
public static class Worksheet
{
    public static WorksheetResult Evaluate(IReadOnlyList<WorksheetDocument> documents, SemanticsProfile? profile = null, EvaluationOptions? options = null)
    {
        profile ??= SemanticsProfile.Hlsl;
        options ??= new EvaluationOptions();
        Stopwatch clock = Stopwatch.StartNew();

        // `2 h` is hours, unless the worksheet names something h
        (List<WorksheetSource> sources, IReadOnlyList<Diagnostic> parseDiagnostics) = Parse(documents, UnitTable.Units.ContainsKey);
        HashSet<string> declaredNames = sources.SelectMany(source => DeclaredNames(source.Items)).ToHashSet(StringComparer.Ordinal);
        if (declaredNames.Any(UnitTable.Units.ContainsKey))
        {
            (sources, parseDiagnostics) = Parse(documents, name => UnitTable.Units.ContainsKey(name) && !declaredNames.Contains(name));
        }

        BoundWorksheet bound = Binder.BindWorksheet(sources, profile, parseDiagnostics);
        DiagnosticBag allDiagnostics = new DiagnosticBag();
        allDiagnostics.AddRange(bound.Program.Diagnostics);

        Dictionary<VariableSymbol, Value> storage = new Dictionary<VariableSymbol, Value>();
        new Evaluator(profile, options, storage, new DiagnosticBag(), string.Empty).ZeroGlobals(bound.Program);
        Dictionary<string, int[]> lineStarts = documents.ToDictionary(document => document.Name, document => LineStarts(document.Text));

        List<WorksheetLine> lines = new List<WorksheetLine>();
        foreach (ScriptLine line in bound.Lines)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            // What the line starts from, for its reference check
            Dictionary<VariableSymbol, Value> inputs = bound.Program.Globals
                .Where(global => global.ConstantValue == null && storage.ContainsKey(global))
                .ToDictionary(global => global, global => storage[global].Clone());

            DiagnosticBag lineDiagnostics = new DiagnosticBag();
            // The line's own compile problems belong to it; a line that doesn't compile doesn't run
            lineDiagnostics.AddRange(bound.Program.Diagnostics.Where(diagnostic => diagnostic.Source == line.Source && diagnostic.Function == null
                && diagnostic.Span.Offset >= line.Span.Offset && diagnostic.Span.Offset < line.Span.End + 1));
            Evaluator evaluator = new Evaluator(profile, options, storage, lineDiagnostics, line.Source) { CurrentSource = line.Source };
            Value? value = null;
            try
            {
                value = lineDiagnostics.HasErrors ? null : evaluator.RunLine(line);
                if (value != null)
                {
                    value = ShaderSession.Materialize(value, profile, out string? note);
                    if (note != null)
                    {
                        lineDiagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, note, line.Source, line.Span));
                    }
                }
            }
            catch (EvaluationException exception)
            {
                lineDiagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, exception.Message, exception.SourceName ?? line.Source, exception.Span,
                    exception.FunctionName));
            }
            if (value?.Type is VoidType)
            {
                value = null;
            }
            allDiagnostics.AddRange(lineDiagnostics.Items);

            VariableSymbol? shownVariable = line.Result == null && line.Declared.Count > 0 ? line.Declared[^1] : null;
            BoundInteractive interactive = new BoundInteractive(new[] { line.Statement }, line.Result, shownVariable, line.Declared);
            LineResult result = new LineResult(value, lineDiagnostics.Items, interactive) { Program = bound.Program, Inputs = inputs };
            int lastLine = lineStarts.TryGetValue(line.Source, out int[]? starts) ? LineOf(starts, Math.Max(line.Span.End - 1, line.Span.Offset)) : line.Span.Line;
            lines.Add(new WorksheetLine(line.Source, lastLine, line.Span, result));
        }
        return new WorksheetResult(bound.Program, lines, allDiagnostics.Items, clock.Elapsed);
    }

    private static (List<WorksheetSource> Sources, IReadOnlyList<Diagnostic> Diagnostics) Parse(IReadOnlyList<WorksheetDocument> documents, Func<string, bool> isUnitName)
    {
        DiagnosticBag diagnostics = new DiagnosticBag();
        Preprocessor preprocessor = Preprocessor.CreateShared();
        List<string> typeNames = new List<string>();
        List<WorksheetSource> sources = new List<WorksheetSource>();
        foreach (WorksheetDocument document in documents)
        {
            List<Token> tokens = preprocessor.ProcessFile(Lexer.Tokenize(document.Text, document.Name, diagnostics), document.Name, diagnostics);
            List<SyntaxNode> items = Parser.ParseWorksheet(tokens, document.Name, diagnostics, typeNames, isUnitName);
            typeNames.AddRange(items.OfType<StructSyntax>().Select(structure => structure.Name));
            typeNames.AddRange(items.OfType<TypedefSyntax>().Select(typedef => typedef.Name));
            sources.Add(new WorksheetSource(document.Name, items));
        }
        return (sources, diagnostics.Items);
    }

    private static IEnumerable<string> DeclaredNames(IEnumerable<SyntaxNode> items)
    {
        foreach (SyntaxNode item in items)
        {
            switch (item)
            {
                case FunctionSyntax function:
                    yield return function.Name;
                    break;
                case StructSyntax structure:
                    yield return structure.Name;
                    break;
                case TypedefSyntax typedef:
                    yield return typedef.Name;
                    break;
                case GlobalVariableSyntax global:
                    foreach (DeclaratorSyntax declarator in global.Declaration.Declarators)
                    {
                        yield return declarator.Name;
                    }
                    break;
                case VariableDeclarationSyntax declaration:
                    foreach (DeclaratorSyntax declarator in declaration.Declarators)
                    {
                        yield return declarator.Name;
                    }
                    break;
                case ExpressionStatementSyntax { Expression: AssignmentSyntax { Target: NameSyntax name } }:
                    yield return name.Name;
                    break;
            }
        }
    }

    private static int[] LineStarts(string text)
    {
        List<int> starts = new List<int> { 0 };
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }
        return starts.ToArray();
    }

    /// <summary>1-based line of a character offset.</summary>
    private static int LineOf(int[] lineStarts, int offset)
    {
        int index = Array.BinarySearch(lineStarts, offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }
}
