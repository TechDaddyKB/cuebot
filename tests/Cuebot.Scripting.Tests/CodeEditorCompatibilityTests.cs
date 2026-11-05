using Cuebot.Scripting.Compat;

namespace Cuebot.Scripting.Tests;

/// <summary>
/// Code written in a code editor (Visual Studio, VS Code, Rider…) against Cuebot's DLLs, as the scripting guide
/// describes, runs in Cuebot unchanged.
/// </summary>
public sealed class CodeEditorCompatibilityTests
{
    [Fact]
    public void A_CPHInline_class_that_names_its_base_class_compiles()
    {
        using var host = new CphInlineHost();

        host.Get("""
            public class CPHInline : CPHInlineBase
            {
                public bool Execute()
                {
                    CPH.LogInfo("Hello from my editor");
                    return true;
                }
            }
            """);
    }

    [Fact]
    public void A_CPHInline_class_with_the_full_base_class_name_compiles()
    {
        using var host = new CphInlineHost();

        host.Get("""
            using Streamer.bot.Plugin.Interface;

            public class CPHInline : Streamer.bot.Plugin.Interface.CPHInlineBase
            {
                public bool Execute() => true;
            }
            """);
    }

    /// <summary>A code-editor project holds many scripts, so each file puts its CPHInline in its own namespace.</summary>
    [Fact]
    public void A_CPHInline_class_inside_a_namespace_compiles()
    {
        using var host = new CphInlineHost();

        host.Get("""
            namespace MyScripts.RaidWelcome;

            public class CPHInline : CPHInlineBase
            {
                public bool Execute() => true;
            }
            """);
    }
}
