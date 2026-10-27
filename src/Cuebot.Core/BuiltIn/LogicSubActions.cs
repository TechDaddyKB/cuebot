using Cuebot.Core.Actions;

namespace Cuebot.Core.BuiltIn;

public sealed class IfSubAction : ISubActionHandler
{
    public const string True = "True";
    public const string False = "False";

    public string TypeId => "core.if";
    public string Category => Categories.Logic;
    public string DisplayName => "If";
    public IReadOnlyList<string> Branches { get; } = [True, False];
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("left", "Value") { DefaultValue = "%message%" },
        new("operator", "Operator", ParameterKind.Choice) { DefaultValue = "equals", Choices = Conditions.Operators },
        new("right", "Compare to"),
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        Task.FromResult(SubActionResult.TakeBranch(Conditions.Evaluate(p["left"], p["operator"], p["right"]) ? True : False));
}

