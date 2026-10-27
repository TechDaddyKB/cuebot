namespace Cuebot.YouTube.Tests.Support;

public static class Wait
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    public static async Task<T> ForAsync<T>(Func<T?> value, string what) where T : class
    {
        T? result = null;
        await UntilAsync(() => (result = value()) is not null, what);
        return result!;
    }
}
