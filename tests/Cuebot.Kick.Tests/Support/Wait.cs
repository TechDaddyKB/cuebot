namespace Cuebot.Kick.Tests.Support;

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string what = "condition")
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}

internal static class TestTimings
{
    public static KickTimings Fast { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(5),
        EstablishTimeout: TimeSpan.FromSeconds(5),
        PongTimeout: TimeSpan.FromMilliseconds(200),
        MaxActivityTimeout: TimeSpan.FromSeconds(120),
        ReconnectDelay: TimeSpan.FromMilliseconds(10),
        MaxReconnectDelay: TimeSpan.FromMilliseconds(50),
        MinimumPoll: TimeSpan.FromMilliseconds(50),
        DedupeWindow: TimeSpan.FromMinutes(2),
        SignInTimeout: TimeSpan.FromSeconds(10));
}

internal static class TestData
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));
}
