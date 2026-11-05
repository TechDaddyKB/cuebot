using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Storage;

namespace Cuebot.Scripting.Export;

public enum CodeLanguage { CSharp, Python }

/// <summary>The code "Export to code" wrote for an action, and what the person should know about it.</summary>
public sealed record CodeExport(string Code, IReadOnlyList<string> Warnings);

/// <summary>
/// "Export to code": turns an action's steps into the body of one C# or Python code step that does the same thing.
/// The flow (If, Switch, While, Loop break/continue, Stop, Groups, random picks and, in C#, Run at the same time)
/// becomes real code. Every other step runs through <c>run_step</c> / <c>Cb.Step</c> with its settings exactly as
/// stored, so %variables% are filled in by Cuebot the same way as in the editor and the step behaves the same.
/// Python runs one thing at a time, so there "Run at the same time" stays as steps Cuebot runs (<c>run_steps</c>),
/// as does anything code cannot express (a Loop break inside a C# lane, a step type that runs its own steps).
/// </summary>
public static class ActionCodeExporter
{
    private const string SetSubActionState = "core.setSubActionState";
    private const string SetSubActionWeight = "core.setSubActionWeight";
    private const string CSharpCode = "code.csharp";
    private const string PythonCode = "code.python";

    private static readonly JsonSerializerOptions Compact = new(Json.Options) { WriteIndented = false };

    /// <param name="otherActions">The profile's actions, to warn about steps elsewhere that change this action's steps.</param>
    public static CodeExport Export(ActionDefinition action, Registry registry, CodeLanguage language,
        IEnumerable<ActionDefinition>? otherActions = null)
    {
        var writer = new Writer(registry, language);
        writer.Header(action);
        var steps = action.SubActions;
        if (action.RandomAction) writer.RandomPick(steps, comment: "The action runs ONE of its steps at random, picked by weight.");
        else if (action.ConcurrentSubActions) writer.TopLevelTogether(steps);
        else writer.Chain(steps);
        writer.Footer();

        foreach (var other in otherActions ?? [])
        {
            if (other.Id == action.Id) continue;
            if (Flatten(other.SubActions).Any(s => (s.Type is SetSubActionState or SetSubActionWeight) && TargetsAction(s, action)))
            {
                writer.Warn($"The action \"{other.Name}\" changes single steps of this action (Set sub-action state or weight). " +
                            "The exported code has no separate steps, so those changes will do nothing.");
            }
        }
        return new CodeExport(writer.ToString(), writer.Warnings);
    }

    private static bool TargetsAction(SubActionDefinition step, ActionDefinition action) =>
        step.Parameters.TryGetValue("action", out var name)
        && (string.Equals(name.Trim(), action.Name, StringComparison.OrdinalIgnoreCase) || name.Trim() == action.Id.ToString());

    internal static IEnumerable<SubActionDefinition> Flatten(IEnumerable<SubActionDefinition> steps) =>
        steps.SelectMany(s => s.Branches.Values.SelectMany(Flatten).Prepend(s));

    /// <summary>A string literal valid in both C# and Python.</summary>
    internal static string Literal(string? text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text ?? "")
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (char.IsControl(c) || c is '\u2028' or '\u2029')
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private sealed class Writer(Registry registry, CodeLanguage language)
    {
        private readonly StringBuilder _sb = new();
        private readonly List<string> _warnings = [];
        private int _indent;
        private int _counter;
        private int _statements;
        private bool _disabledNoted;

        // Where the code currently is: the C# helper object ("Cb" or a lane's), and the loops it can break out of.
        private string _cb = language == CodeLanguage.CSharp ? "Cb" : "cb";
        private int _loopDepth;

        private bool CSharp => language == CodeLanguage.CSharp;

        public IReadOnlyList<string> Warnings => _warnings;

        public void Warn(string warning)
        {
            if (!_warnings.Contains(warning)) _warnings.Add(warning);
        }

        public override string ToString()
        {
            if (_warnings.Count == 0) return _sb.ToString();
            var head = new StringBuilder();
            head.Append(Comment("Notes from the export:")).Append('\n');
            foreach (var warning in _warnings) head.Append(Comment("- " + warning)).Append('\n');
            return head.Append('\n').Append(_sb).ToString();
        }

        public void Header(ActionDefinition action)
        {
            Line(Comment($"Exported from the action \"{OneLine(action.Name)}\" with \"Export to code\"."));
            Line(Comment(CSharp
                ? "Cb.Step runs a step exactly like the action editor did (its %variables% are filled in by Cuebot)."
                : "cb.run_step runs a step exactly like the action editor did (its %variables% are filled in by Cuebot)."));
            Line(Comment(CSharp
                ? "Change anything you like: Cb.Call and the C# methods work here too. Return false to stop the action."
                : "Change anything you like: the other cb methods work here too. Return False to stop the action."));
            Line("");
        }

        public void Footer()
        {
            if (CSharp) Line("return true;");
        }

        // ---- Chains ----

        public void Chain(IReadOnlyList<SubActionDefinition> steps)
        {
            foreach (var step in steps) Step(step);
        }

        private void Step(SubActionDefinition step)
        {
            var handler = registry.GetSubAction(step.Type);
            if (!step.Enabled)
            {
                Line(Comment($"Off in the editor, so not exported: {Title(step, handler)}"));
                if (!_disabledNoted) Warn("Steps that were off in the editor are left out (each has a comment where it was).");
                _disabledNoted = true;
                return;
            }
            if (handler is null)
            {
                Warn($"The step type \"{step.Type}\" is not installed here; its line will fail the same way the step did.");
                PlainStep(step, null);
                return;
            }
            switch (step.Type)
            {
                case GroupSubAction.Id: Group(step); return;
                case "core.if": If(step); return;
                case SwitchSubAction.Id: Switch(step); return;
                case WhileSubAction.Id: While(step); return;
                case ParallelSubAction.Id: Parallel(step); return;
                case "core.stop": Stop(); return;
                case "core.loopBreak": LoopSignal(step, "break"); return;
                case "core.loopContinue": LoopSignal(step, "continue"); return;
                case "core.comment":
                    Line(Comment(step.Parameters.GetValueOrDefault("text") ?? step.Parameters.Values.FirstOrDefault() ?? ""));
                    return;
            }
            if (handler is IFlowContainerHandler)
            {
                AsSteps([step], $"{handler.DisplayName} runs its own steps, so Cuebot runs it as steps.");
                return;
            }
            if (step.Type == (CSharp ? PythonCode : CSharpCode))
            {
                Warn(CSharp
                    ? "Python code steps stay as steps (Cb.Step runs them); C# cannot run Python directly."
                    : "C# code steps stay as steps (cb.run_step runs them); Python cannot run C# directly.");
            }
            PlainStep(step, handler);
        }

        private void PlainStep(SubActionDefinition step, ISubActionHandler? handler)
        {
            var branches = handler is null ? [] : SubActionBranches.For(handler, step)
                .Where(b => step.Branches.TryGetValue(b, out var list) && list.Count > 0).ToArray();
            Line(Comment(Title(step, handler)));
            var call = CSharp
                ? $"await {_cb}.Step({Literal(step.Type)}{CSharpParameters(step)})"
                : $"{_cb}.run_step({Literal(step.Type)}{PythonParameters(step)})";
            if (branches.Length == 0)
            {
                Line(CSharp ? call + ";" : call);
                return;
            }
            var name = Name("branch");
            Line(CSharp ? $"var {name} = {call};" : $"{name} = {call}");
            for (var i = 0; i < branches.Length; i++)
            {
                var test = $"{name} == {Literal(branches[i])}";
                Block(i == 0 ? If(test) : ElseIf(test), () => Chain(step.Branches[branches[i]]));
            }
            EndIf();
        }

        // ---- Flow ----

        private void Group(SubActionDefinition step)
        {
            var name = step.Parameters.GetValueOrDefault("name") is { Length: > 0 } n ? n.Trim() : "Group";
            var children = step.Branches.GetValueOrDefault(GroupSubAction.Steps) ?? [];
            if (string.Equals(step.Parameters.GetValueOrDefault("random"), "true", StringComparison.OrdinalIgnoreCase))
            {
                RandomPick(children, comment: $"Group \"{OneLine(name)}\": runs ONE of its steps at random, picked by weight.");
                return;
            }
            Line(Comment($"Group \"{OneLine(name)}\""));
            Chain(children);
            Line(Comment($"End of group \"{OneLine(name)}\""));
        }

        public void RandomPick(IReadOnlyList<SubActionDefinition> steps, string comment)
        {
            var enabled = steps.Where(s => s.Enabled).ToArray();
            Line(Comment(comment));
            if (enabled.Length == 0) return;
            var weights = string.Join(", ", enabled.Select(s => s.Weight.ToString("R", CultureInfo.InvariantCulture)));
            var name = Name("pick");
            Line(CSharp ? $"var {name} = await {_cb}.Pick({weights});" : $"{name} = {_cb}.pick({weights})");
            for (var i = 0; i < enabled.Length; i++)
            {
                var test = $"{name} == {i}";
                var step = enabled[i];
                Block(i == 0 ? If(test) : ElseIf(test), () => Step(step));
            }
            EndIf();
        }

        private void If(SubActionDefinition step)
        {
            var test = $"{Await()}{_cb}.{(CSharp ? "Check" : "check")}({Literal(Get(step, "left", "%message%"))}, " +
                       $"{Literal(Get(step, "operator", "equals"))}, {Literal(Get(step, "right", ""))})";
            var whenTrue = step.Branches.GetValueOrDefault(IfSubAction.True) ?? [];
            var whenFalse = step.Branches.GetValueOrDefault(IfSubAction.False) ?? [];
            Line(Comment("If"));
            Block(If(test), () => Chain(whenTrue));
            if (whenFalse.Count > 0) Block(Else(), () => Chain(whenFalse));
            EndIf();
        }

        private void Switch(SubActionDefinition step)
        {
            var input = Literal(Get(step, "input", "%message%"));
            var op = Literal(Get(step, "operator", "equals"));
            var cases = SwitchSubAction.ParseCases(step.Parameters.GetValueOrDefault("cases"));
            Line(Comment("Switch: the first matching case runs, else Default. %switchCase% says which one."));
            for (var i = 0; i < cases.Count; i++)
            {
                var line = cases[i];
                var test = string.Join(CSharp ? " || " : " or ", SwitchSubAction.SplitValues(line)
                    .Select(v => $"{Await()}{_cb}.{(CSharp ? "Check" : "check")}({input}, {op}, {Literal(v)})"));
                if (test.Length == 0) test = CSharp ? "false" : "False";
                Block(i == 0 ? If(test) : ElseIf(test), () => SwitchCase(step, line));
            }
            if (cases.Count == 0) SwitchCase(step, SwitchSubAction.Default);
            else
            {
                Block(Else(), () => SwitchCase(step, SwitchSubAction.Default));
                EndIf();
            }
        }

        private void SwitchCase(SubActionDefinition step, string name)
        {
            Line(SetArg("switchCase", Literal(name)));
            Chain(step.Branches.GetValueOrDefault(name) ?? []);
        }

        private void While(SubActionDefinition step)
        {
            var max = int.TryParse(Get(step, "maxIterations", "10000").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                ? m : 10000;
            var i = Name("i");
            var outer = Name("outer");
            var test = $"{Await()}{_cb}.{(CSharp ? "Check" : "check")}({Literal(Get(step, "left", "%loopIteration%"))}, " +
                       $"{Literal(Get(step, "operator", "less than"))}, {Literal(Get(step, "right", "10"))})";
            Line(Comment(max > 0 ? $"While (at most {max} rounds). %loopIteration% counts the rounds from 0." : "While. %loopIteration% counts the rounds from 0."));
            if (CSharp)
            {
                Line($"var {outer} = {_cb}.ArgValue(\"loopIteration\");");
                Line($"var had{outer} = {_cb}.HasArg(\"loopIteration\");");
                Line("try");
                Open();
                Line($"for (var {i} = 0; ; {i}++)");
                Open();
            }
            else
            {
                Line($"{outer} = {_cb}.get_arg(\"loopIteration\")");
                Line("try:");
                _indent++;
                Line($"{i} = -1");
                Line("while True:");
                _indent++;
                Line($"{i} += 1");
            }
            Line(SetArg("loopIteration", i));
            if (max > 0)
            {
                var warn = Literal($"While loop stopped after {max} iterations (safety limit)");
                Block(If($"{i} >= {max}"), () =>
                {
                    Line(CSharp ? $"await {_cb}.Log({warn}, \"Warning\");" : $"{_cb}.log({warn}, \"Warning\")");
                    Line("break" + (CSharp ? ";" : ""));
                });
                EndIf();
            }
            Block(If(CSharp ? $"!{test}" : $"not {test}"), () => Line("break" + (CSharp ? ";" : "")));
            EndIf();
            _loopDepth++;
            Chain(step.Branches.GetValueOrDefault(WhileSubAction.Body) ?? []);
            _loopDepth--;
            if (CSharp)
            {
                Close();
                Close();
                Line("finally");
                Open();
                Line($"if (had{outer}) {_cb}.SetArg(\"loopIteration\", {outer});");
                Close();
            }
            else
            {
                _indent -= 2;
                Line("finally:");
                _indent++;
                Line($"if {outer} is not None:");
                _indent++;
                Line($"{_cb}.set_arg(\"loopIteration\", {outer})");
                _indent -= 2;
            }
        }

        private void Stop() => Line(CSharp ? "return false;" : "return False");

        private void LoopSignal(SubActionDefinition step, string keyword)
        {
            if (_loopDepth > 0) Line(CSharp ? keyword + ";" : keyword);
            else
            {
                Line(Comment($"{Title(step, registry.GetSubAction(step.Type))} outside a loop stops the action."));
                Stop();
            }
        }

        private void Parallel(SubActionDefinition step)
        {
            var lanes = new ParallelSubAction().GetBranches(step.Parameters)
                .Select(name => step.Branches.GetValueOrDefault(name) ?? [])
                .Where(lane => lane.Any(s => s.Enabled))
                .ToArray();
            if (!CSharp)
            {
                AsSteps([step], "Python runs one thing at a time, so \"Run at the same time\" stays as steps Cuebot runs together (cb.run_steps).");
                return;
            }
            if (lanes.Any(lane => EscapesLoop(lane)))
            {
                AsSteps([step], "A Loop break or Loop continue inside \"Run at the same time\" cannot leave a C# lane, so that step stays as steps (Cb.RunSteps).");
                return;
            }
            Together(lanes, "Run at the same time: the lanes start together; the next line waits for all of them.");
        }

        public void TopLevelTogether(IReadOnlyList<SubActionDefinition> steps)
        {
            var enabled = steps.Where(s => s.Enabled).ToArray();
            if (enabled.Length == 0) return;
            if (!CSharp)
            {
                AsSteps([ParallelOf(enabled)], "Python runs one thing at a time, so the action's \"Concurrent sub-actions\" stay as steps Cuebot runs together (cb.run_steps).");
                return;
            }
            if (enabled.Any(s => EscapesLoop([s])))
            {
                AsSteps([ParallelOf(enabled)], "A Loop break or Loop continue cannot leave a C# lane, so the steps stay as steps Cuebot runs together (Cb.RunSteps).");
                return;
            }
            Together(enabled.Select(s => (IReadOnlyList<SubActionDefinition>)[s]).ToArray(),
                "The action runs all of its steps at the same time (\"Concurrent sub-actions\").");
        }

        private void Together(IReadOnlyList<IReadOnlyList<SubActionDefinition>> lanes, string comment)
        {
            Line(Comment(comment));
            if (lanes.Count == 0) return;
            var parent = _cb;
            var loops = _loopDepth;
            Line($"if (!await {parent}.Together(");
            _indent++;
            for (var i = 0; i < lanes.Count; i++)
            {
                var lane = Name("lane");
                Line($"async {lane} =>");
                Open();
                _cb = lane;
                _loopDepth = 0;
                Chain(lanes[i]);
                Line("return true;");
                _cb = parent;
                _loopDepth = loops;
                _indent--;
                Line(i < lanes.Count - 1 ? "}," : "}))");
            }
            _indent--;
            Open();
            Stop();
            Close();
        }

        /// <summary>Steps Cuebot runs as they are (cb.run_steps / Cb.RunSteps), with the outcome handled.</summary>
        private void AsSteps(IReadOnlyList<SubActionDefinition> steps, string why)
        {
            Warn(why);
            var json = StepsJson(steps);
            var outcome = Name("outcome");
            Line(Comment(string.Join(" + ", steps.Select(s => Title(s, registry.GetSubAction(s.Type)))) + " (run by Cuebot as steps)"));
            if (CSharp)
            {
                var quotes = new string('"', Math.Max(3, LongestQuoteRun(json) + 1));
                Line($"var {outcome} = await {_cb}.RunSteps({quotes}{json}{quotes});");
                Block(If($"{outcome} == \"stop\""), Stop);
            }
            else
            {
                Line($"{outcome} = {_cb}.run_steps({PythonLiteral(JsonNode.Parse(json))})");
                Block(If($"{outcome} == \"stop\""), Stop);
            }
            foreach (var keyword in (string[])["break", "continue"])
            {
                Block(ElseIf($"{outcome} == \"{keyword}\""), () =>
                {
                    if (_loopDepth > 0) Line(CSharp ? keyword + ";" : keyword);
                    else Stop();
                });
            }
            EndIf();
        }

        private static SubActionDefinition ParallelOf(IReadOnlyList<SubActionDefinition> steps)
        {
            // A step has at most MaxLanes lanes; more steps go into a nested "Run at the same time" in the last lane.
            var step = new SubActionDefinition { Type = ParallelSubAction.Id };
            var direct = steps.Count <= ParallelSubAction.MaxLanes ? steps.Count : ParallelSubAction.MaxLanes - 1;
            for (var i = 0; i < direct; i++) step.Branch(ParallelSubAction.Lane(i + 1)).Add(steps[i]);
            if (direct < steps.Count) step.Branch(ParallelSubAction.Lane(direct + 1)).Add(ParallelOf([.. steps.Skip(direct)]));
            step.Parameters["lanes"] = step.Branches.Count.ToString(CultureInfo.InvariantCulture);
            return step;
        }

        /// <summary>Whether a Loop break/continue in these steps would have to leave them (no While of their own around it).</summary>
        private static bool EscapesLoop(IReadOnlyList<SubActionDefinition> steps) =>
            steps.Any(s => s.Enabled && (s.Type is "core.loopBreak" or "core.loopContinue"
                                         || (s.Type != WhileSubAction.Id && s.Branches.Values.Any(EscapesLoop))));

        private static string StepsJson(IReadOnlyList<SubActionDefinition> steps)
        {
            var node = JsonSerializer.SerializeToNode(steps, Compact)!;
            StripLayout(node);
            return node.ToJsonString(Compact);
        }

        private static void StripLayout(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    obj.Remove("layout");
                    foreach (var (_, child) in obj.ToArray()) StripLayout(child);
                    break;
                case JsonArray array:
                    foreach (var child in array) StripLayout(child);
                    break;
            }
        }

        private static int LongestQuoteRun(string text)
        {
            int best = 0, run = 0;
            foreach (var c in text)
            {
                run = c == '"' ? run + 1 : 0;
                best = Math.Max(best, run);
            }
            return best;
        }

        private static string PythonLiteral(JsonNode? node) => node switch
        {
            null => "None",
            JsonObject obj => "{" + string.Join(", ", obj.Select(p => $"{Literal(p.Key)}: {PythonLiteral(p.Value)}")) + "}",
            JsonArray array => "[" + string.Join(", ", array.Select(PythonLiteral)) + "]",
            JsonValue value => value.GetValueKind() switch
            {
                JsonValueKind.String => Literal(value.GetValue<string>()),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                JsonValueKind.Null => "None",
                _ => value.ToJsonString(),
            },
            _ => "None",
        };

        // ---- Pieces ----

        private string CSharpParameters(SubActionDefinition step) => step.Parameters.Count == 0 ? ""
            : ", new() { " + string.Join(", ", step.Parameters.Select(p => $"[{Literal(p.Key)}] = {Literal(p.Value)}")) + " }";

        private static string PythonParameters(SubActionDefinition step) => step.Parameters.Count == 0 ? ""
            : ", {" + string.Join(", ", step.Parameters.Select(p => $"{Literal(p.Key)}: {Literal(p.Value)}")) + "}";

        private string SetArg(string name, string value) =>
            CSharp ? $"{_cb}.SetArg({Literal(name)}, {value});" : $"{_cb}.set_arg({Literal(name)}, {value})";

        private string Await() => CSharp ? "await " : "";

        private static string Get(SubActionDefinition step, string key, string fallback) =>
            step.Parameters.TryGetValue(key, out var value) ? value : fallback;

        private static string Title(SubActionDefinition step, ISubActionHandler? handler) =>
            OneLine(handler?.DisplayName ?? step.Type);

        private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

        private string Name(string prefix) => $"{prefix}{++_counter}";

        private string Comment(string text) =>
            string.Join("\n", OneLine(text).Length == 0 ? [CSharp ? "//" : "#"] : SplitComment(text).Select(l => (CSharp ? "// " : "# ") + l));

        private static IEnumerable<string> SplitComment(string text) =>
            text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd());

        private string If(string test) => CSharp ? $"if ({test})" : $"if {test}:";

        private string ElseIf(string test) => CSharp ? $"else if ({test})" : $"elif {test}:";

        private string Else() => CSharp ? "else" : "else:";

        private static void EndIf()
        {
        }

        private void Block(string head, Action body)
        {
            Line(head);
            if (CSharp) Open();
            else _indent++;
            var before = _statements;
            body();
            if (!CSharp && _statements == before) Line("pass");
            if (CSharp) Close();
            else _indent--;
        }

        private void Open()
        {
            Line("{");
            _indent++;
        }

        private void Close()
        {
            _indent--;
            Line("}");
        }

        private void Line(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) _sb.Append('\n');
                else _sb.Append(' ', _indent * 4).Append(line).Append('\n');
                if (line.Length > 0 && !line.StartsWith(CSharp ? "//" : "#", StringComparison.Ordinal)) _statements++;
            }
        }
    }
}
