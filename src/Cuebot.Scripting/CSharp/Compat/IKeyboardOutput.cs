namespace Cuebot.Scripting.Compat;

/// <summary>
/// Sends simulated key presses for <c>CPH.KeyboardPress</c>. Register an implementation in DI (singleton) to enable it;
/// without one the method throws <see cref="NotSupportedException"/>.
/// </summary>
public interface IKeyboardOutput
{
    /// <summary>
    /// Presses the keys described by <paramref name="keys"/>, in Windows Forms SendKeys syntax as Streamer.bot uses
    /// it: keys in braces (<c>{W}</c>, <c>{DOWN}</c>, <c>{F5}</c>), plain characters, and the modifier prefixes
    /// <c>+</c> (Shift), <c>^</c> (Ctrl) and <c>%</c> (Alt), e.g. <c>^{W}</c>.
    /// </summary>
    Task SendKeysAsync(string keys, CancellationToken ct = default);
}
