using Acornima;
using Acornima.Ast;

namespace CodeFlow.Orchestration.Scripting;

/// <summary>
/// Pure helper: parse a single JS script via Acornima (the same parser Jint uses to execute
/// workflow scripts) and extract the static signals F2 cares about — every
/// <c>setWorkflow(key, ...)</c> / <c>setContext(key, ...)</c> / <c>setInput(...)</c> /
/// <c>setOutput(...)</c> call together with whether each call sits inside a conditional or
/// loop block.
///
/// Conservative analysis: any string-literal first argument counts as a definite key write
/// when it sits at top level (or unconditionally inside a function declared at top level).
/// Calls inside <c>if</c>, <c>for</c>, <c>while</c>, <c>do</c>, <c>switch</c>, <c>try</c>, or
/// any block conditional on a runtime expression are treated as <see cref="DataflowConfidence.Conditional"/>.
/// Computed keys (non-literal first argument) are flagged as a diagnostic but not added to the
/// scope — they're rare in practice and dynamic-key validation belongs to a future card.
/// </summary>
public static class ScriptDataflowExtractor
{
    public static ScriptDataflowResult Extract(string? script, string scriptKind)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return ScriptDataflowResult.Empty(scriptKind);
        }

        Script ast;
        try
        {
            var parser = new Parser();
            ast = parser.ParseScript(script);
        }
        catch (ParseErrorException ex)
        {
            return new ScriptDataflowResult(
                scriptKind,
                Array.Empty<ExtractedKeyWrite>(),
                Array.Empty<ExtractedKeyWrite>(),
                CallsSetOutput: false,
                CallsSetInput: false,
                Diagnostics: new[] { $"Script parse error: {ex.Description}" });
        }

        var visitor = new Visitor(scriptKind);
        visitor.Visit(ast);
        return visitor.ToResult();
    }

    private sealed class Visitor
    {
        private readonly string scriptKind;
        private readonly List<ExtractedKeyWrite> workflowWrites = new();
        private readonly List<ExtractedKeyWrite> contextWrites = new();
        private readonly List<string> diagnostics = new();
        private bool callsSetOutput;
        private bool callsSetInput;
        private int conditionalDepth;

        public Visitor(string scriptKind)
        {
            this.scriptKind = scriptKind;
        }

        public ScriptDataflowResult ToResult() => new(
            ScriptKind: scriptKind,
            WorkflowWrites: workflowWrites,
            ContextWrites: contextWrites,
            CallsSetOutput: callsSetOutput,
            CallsSetInput: callsSetInput,
            Diagnostics: diagnostics);

        public void Visit(Node? node)
        {
            if (node is null)
            {
                return;
            }

            switch (node)
            {
                case CallExpression call:
                    HandleCall(call);
                    foreach (var child in call.ChildNodes)
                    {
                        Visit(child);
                    }
                    break;

                case IfStatement ifNode:
                    Visit(ifNode.Test);
                    EnterConditional();
                    try
                    {
                        Visit(ifNode.Consequent);
                        Visit(ifNode.Alternate);
                    }
                    finally
                    {
                        LeaveConditional();
                    }
                    break;

                case ForStatement forNode:
                case ForInStatement forIn:
                case ForOfStatement forOf:
                case WhileStatement whileNode:
                case DoWhileStatement doWhileNode:
                case SwitchStatement switchNode:
                case ConditionalExpression ternary:
                case LogicalExpression logical:
                case TryStatement tryNode:
                    EnterConditional();
                    try
                    {
                        foreach (var child in node.ChildNodes)
                        {
                            Visit(child);
                        }
                    }
                    finally
                    {
                        LeaveConditional();
                    }
                    break;

                default:
                    foreach (var child in node.ChildNodes)
                    {
                        Visit(child);
                    }
                    break;
            }
        }

        private void HandleCall(CallExpression call)
        {
            if (call.Callee is not Identifier callee)
            {
                return;
            }

            switch (callee.Name)
            {
                case "setWorkflow":
                    RecordKeyWrite(call, workflowWrites, "setWorkflow");
                    break;
                case "setContext":
                    RecordKeyWrite(call, contextWrites, "setContext");
                    break;
                case "setOutput":
                    callsSetOutput = true;
                    break;
                case "setInput":
                    callsSetInput = true;
                    break;
            }
        }

        private void RecordKeyWrite(CallExpression call, List<ExtractedKeyWrite> sink, string verb)
        {
            if (call.Arguments.Count < 1)
            {
                diagnostics.Add($"{verb}() called with no arguments — required (key, value).");
                return;
            }

            if (call.Arguments[0] is not StringLiteral literal)
            {
                diagnostics.Add($"{verb}() called with a non-literal key — dataflow analyzer cannot resolve dynamic key names.");
                return;
            }

            sink.Add(new ExtractedKeyWrite(
                Key: literal.Value,
                Confidence: conditionalDepth > 0
                    ? DataflowConfidence.Conditional
                    : DataflowConfidence.Definite));
        }

        private void EnterConditional() => conditionalDepth++;

        private void LeaveConditional() => conditionalDepth--;
    }
}

public sealed record ScriptDataflowResult(
    string ScriptKind,
    IReadOnlyList<ExtractedKeyWrite> WorkflowWrites,
    IReadOnlyList<ExtractedKeyWrite> ContextWrites,
    bool CallsSetOutput,
    bool CallsSetInput,
    IReadOnlyList<string> Diagnostics)
{
    public static ScriptDataflowResult Empty(string scriptKind) => new(
        scriptKind,
        Array.Empty<ExtractedKeyWrite>(),
        Array.Empty<ExtractedKeyWrite>(),
        CallsSetOutput: false,
        CallsSetInput: false,
        Diagnostics: Array.Empty<string>());
}

public sealed record ExtractedKeyWrite(string Key, DataflowConfidence Confidence);
