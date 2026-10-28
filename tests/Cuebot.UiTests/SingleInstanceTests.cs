using Cuebot.App.Shell;

namespace Cuebot.UiTests;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cuebot-si-").FullName;

    [Fact]
    public async Task Second_instance_cannot_claim_the_folder_and_activates_the_first()
    {
        using var first = SingleInstance.TryAcquire(_root);
        Assert.NotNull(first);
        Assert.Null(first.ListenError);
        var activated = new TaskCompletionSource();
        first.ActivationRequested += () => activated.TrySetResult();

        Assert.Null(SingleInstance.TryAcquire(_root));   // the lock is held

        Assert.True(await SingleInstance.ActivateExistingAsync(_root, TimeSpan.FromSeconds(5)));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_request_reaches_the_running_instance_and_not_activation()
    {
        using var first = SingleInstance.TryAcquire(_root);
        Assert.NotNull(first);
        var shutdown = new TaskCompletionSource();
        var activated = 0;
        first.ShutdownRequested += () => shutdown.TrySetResult();
        first.ActivationRequested += () => Interlocked.Increment(ref activated);
        Assert.True(await SingleInstance.ShutdownExistingAsync(_root, TimeSpan.FromSeconds(5)));
        await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref activated));
    }

    [Fact]
    public async Task Folder_can_be_claimed_again_after_the_first_instance_exits()
    {
        var first = SingleInstance.TryAcquire(_root);
        Assert.NotNull(first);
        first.Dispose();

        using var second = SingleInstance.TryAcquire(_root);
        Assert.NotNull(second);
        var activated = 0;
        second.ActivationRequested += () => Interlocked.Increment(ref activated);
        Assert.True(await SingleInstance.ActivateExistingAsync(_root, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref activated));
    }

    [Fact]
    public void A_stale_socket_file_from_a_crash_does_not_block_startup()
    {
        File.WriteAllText(Path.Combine(_root, SingleInstance.SocketFileName), "stale");
        using var instance = SingleInstance.TryAcquire(_root);
        Assert.NotNull(instance);
        Assert.Null(instance.ListenError);
    }

    [Fact]
    public async Task Activation_fails_quickly_when_nobody_listens()
    {
        var socket = Path.Combine(_root, "nobody.sock");
        Assert.False(await SingleInstance.ActivateExistingAsync(socket, TimeSpan.FromSeconds(1), retryUntilTimeout: false));
        Assert.False(await SingleInstance.ActivateExistingAsync(socket, TimeSpan.FromMilliseconds(600), retryUntilTimeout: true));
    }

    [Fact]
    public void Different_data_folders_are_independent()
    {
        var other = Directory.CreateTempSubdirectory("cuebot-si-other-").FullName;
        try
        {
            using var a = SingleInstance.TryAcquire(_root);
            using var b = SingleInstance.TryAcquire(other);
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.NotEqual(a.SocketPath, b.SocketPath);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void Long_data_paths_use_a_short_hashed_socket_name()
    {
        var deep = Path.Combine(_root, new string('x', 60), new string('y', 60));
        var path = SingleInstance.SocketPathFor(deep);
        Assert.StartsWith(Path.GetTempPath(), path);
        Assert.Equal(path, SingleInstance.SocketPathFor(deep));   // stable, so both launches agree
        Assert.Equal(Path.Combine(_root, SingleInstance.SocketFileName), SingleInstance.SocketPathFor(_root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
