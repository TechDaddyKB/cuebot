namespace Cuebot.Scripting;

/// <summary>An <see cref="IProgress{T}"/> that reports synchronously on the calling thread, preserving order.</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
