using System.Diagnostics;
using Cuebot.Core.Actions;

namespace Cuebot.Core.BuiltIn;

public sealed class RunProgramSubAction : ISubActionHandler
{
    public string TypeId => "core.runProgram";
    public string Category => Categories.System;
    public string DisplayName => "Run program";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("path", "Program or file"),
        new("arguments", "Arguments"),
        new("workingDirectory", "Working directory"),
        new("wait", "Wait for exit", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var info = new ProcessStartInfo(p["path"], p["arguments"]) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(p["workingDirectory"])) info.WorkingDirectory = p["workingDirectory"];
        using var process = Process.Start(info);
        if (process is not null && p.GetBool("wait"))
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            context.Args["exitCode"] = process.ExitCode;
        }
        return SubActionResult.Next;
    }
}
