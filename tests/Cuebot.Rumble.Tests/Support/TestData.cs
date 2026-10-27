namespace Cuebot.Rumble.Tests.Support;

/// <summary>Sample payloads in the TestData folder (copied next to the test assembly).</summary>
internal static class TestData
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));
}
