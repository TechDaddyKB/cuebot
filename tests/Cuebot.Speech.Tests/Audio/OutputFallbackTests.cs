using Cuebot.Speech.Tests.Fakes;

namespace Cuebot.Speech.Tests.Audio;

public sealed class OutputFallbackTests
{
    private static readonly InvalidOperationException Busy = new("Unable to init device Chat (2- TC-HELICON GoXLR). Result: Busy");

    [Fact]
    public void A_busy_device_is_tried_again_then_the_default_plays_with_a_warning()
    {
        var tried = new List<int?>();
        var warnings = new List<string>();
        var waits = new List<int>();

        var (device, name) = SoundFlowBackend.OpenWithFallback<string, int>(7, "Chat (GoXLR)",
            info => { tried.Add(info); return info is null ? "default" : throw Busy; }, warnings.Add, waits.Add);

        Assert.Equal(("default", null), (device, name));
        Assert.Equal([7, 7, null], tried);
        Assert.Equal([250], waits);
        var warning = Assert.Single(warnings);
        Assert.Contains("playing on the default device instead", warning);
        Assert.Contains("exclusive control", warning);
    }

    [Fact]
    public void A_device_that_works_on_the_second_try_is_kept()
    {
        var attempts = 0;
        var (device, name) = SoundFlowBackend.OpenWithFallback<string, int>(3, "Headset",
            info => ++attempts == 1 ? throw Busy : $"device {info}", _ => Assert.Fail("no warning"), _ => { });

        Assert.Equal(("device 3", "Headset"), (device, name));
    }

    [Fact]
    public void When_the_default_is_busy_too_the_error_explains_what_to_do()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SoundFlowBackend.OpenWithFallback<string, int>(2, "Chat (GoXLR)", _ => throw Busy, _ => { }, _ => { }));

        Assert.Contains("\"Chat (GoXLR)\" and the default device refused to play", error.Message);
        Assert.Contains("exclusive control", error.Message);
        Assert.Same(Busy, error.InnerException);
    }

    [Fact]
    public async Task A_failed_text_nobody_waits_for_is_logged_once_and_not_left_unobserved()
    {
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, _) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await using var h = await SpeechHarness.StartAsync();
            h.Player.FailWith = Busy;
            _ = h.Service.SpeakAsync("Hello from Cuebot!");   // fire and forget, like the Speech page used to
            await WaitUntilAsync(() => h.Log.Recent().Any(e => e.Message.StartsWith("Could not speak", StringComparison.Ordinal)));

            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SpeakAsync("waited", wait: true));
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.Equal(0, unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
