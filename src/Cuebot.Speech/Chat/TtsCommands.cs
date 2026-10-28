using System.Globalization;
using Cuebot.Core.Actions;

namespace Cuebot.Speech;

/// <summary>A speaking command found in a message: what to say, with which alias, and who may use it.</summary>
internal sealed record SpeakCommandMatch(string Text, string? Alias, ChatAudience Permission);

/// <summary>
/// The "!tts" chat command (renameable). Viewer sub-commands: voices, voice, speed, pitch, nick, me, commands,
/// about, status. Moderator sub-commands: pause, resume, clear, stop/skip, on/off, mode, ignore, ignored, reg,
/// events, random, set, assign, profile, voicegate. Permissions and cooldowns are configurable per sub-command.
/// </summary>
internal sealed class TtsCommands(SpeechService speech, TimeProvider time)
{
    private sealed record Command(ChatAudience DefaultPermission, bool ViewerCommand, string Usage);

    private static readonly Dictionary<string, Command> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["voices"] = new(ChatAudience.Everyone, true, "voices"),
        ["voice"] = new(ChatAudience.Everyone, true, "voice [alias|reset]"),
        ["speed"] = new(ChatAudience.Everyone, true, "speed <-10..10|reset>"),
        ["pitch"] = new(ChatAudience.Everyone, true, "pitch <-10..10|reset>"),
        ["nick"] = new(ChatAudience.Everyone, true, "nick <nickname|reset>"),
        ["me"] = new(ChatAudience.Everyone, true, "me on|off"),
        ["commands"] = new(ChatAudience.Everyone, true, "commands"),
        ["about"] = new(ChatAudience.Everyone, true, "about"),
        ["status"] = new(ChatAudience.Everyone, true, "status"),
        ["pause"] = new(ChatAudience.Moderators, false, "pause"),
        ["resume"] = new(ChatAudience.Moderators, false, "resume"),
        ["clear"] = new(ChatAudience.Moderators, false, "clear"),
        ["skip"] = new(ChatAudience.Moderators, false, "skip [user]"),
        ["on"] = new(ChatAudience.Moderators, false, "on"),
        ["off"] = new(ChatAudience.Moderators, false, "off"),
        ["mode"] = new(ChatAudience.Moderators, false, "mode all|command"),
        ["ignore"] = new(ChatAudience.Moderators, false, "ignore add|del <user>"),
        ["ignored"] = new(ChatAudience.Moderators, false, "ignored"),
        ["reg"] = new(ChatAudience.Moderators, false, "reg add|del <user>"),
        ["events"] = new(ChatAudience.Moderators, false, "events on|off"),
        ["random"] = new(ChatAudience.Moderators, false, "random reset <user>"),
        ["set"] = new(ChatAudience.Moderators, false, "set nickname <user> [nick] | set sticky on|off | set voice <user> <alias>"),
        ["assign"] = new(ChatAudience.Moderators, false, "assign last <user> | assign <user> <alias>"),
        ["profile"] = new(ChatAudience.Moderators, false, "profile [list|<name>|none]"),
        ["voicegate"] = new(ChatAudience.Moderators, false, "voicegate [<profile>|none]"),
    };

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aboot"] = "about",
        ["enable"] = "on",
        ["disable"] = "off",
        ["stop"] = "skip",
        ["self"] = "me",
        ["nickname"] = "nick",
        ["help"] = "commands",
        ["rate"] = "speed",
    };

    private const int MaxNicknameLength = 30;

    private readonly Dictionary<string, DateTimeOffset> _lastUse = new(StringComparer.OrdinalIgnoreCase);

    private TtsCommandSettings Settings => speech.Settings.Commands;

    /// <summary>Sub-commands, for help texts and the UI.</summary>
    public static IReadOnlyCollection<string> SubCommands => Commands.Keys;

    /// <summary>Finds a speaking command ("!say text" or a custom command) at the start of a message.</summary>
    public static SpeakCommandMatch? MatchSpeakCommand(string message, TtsCommandSettings settings)
    {
        var trimmed = message.TrimStart();
        foreach (var custom in settings.CustomCommands.OrderByDescending(c => c.Command.Length))
        {
            if (Rest(trimmed, custom.Command) is { } text) return new SpeakCommandMatch(text, custom.Alias, custom.Permission);
        }
        foreach (var prefix in settings.SpeakCommands.OrderByDescending(p => p.Length))
        {
            if (Rest(trimmed, prefix) is { } text) return new SpeakCommandMatch(text, null, settings.SpeakCommandPermission);
        }
        return null;
    }

    /// <summary>Whether the message is a <c>!tts</c> command or a speaking command.</summary>
    public static bool IsCommand(string message, TtsCommandSettings settings)
    {
        var prefix = settings.Prefix.Trim().Length > 0 ? settings.Prefix.Trim() : "!tts";
        return Rest(message.Trim(), prefix, allowEmpty: true) is not null || MatchSpeakCommand(message, settings) is not null;
    }

    /// <summary>Whether a chatter belongs to <paramref name="audience"/>. The broadcaster always does.</summary>
    public static bool Allows(ChatAudience audience, ChatContext chat, SpeechUser? user)
    {
        if (chat.IsBroadcaster || audience.HasFlag(ChatAudience.Everyone)) return true;
        return (audience.HasFlag(ChatAudience.Moderators) && chat.IsModerator)
               || (audience.HasFlag(ChatAudience.Vips) && chat.IsVip)
               || (audience.HasFlag(ChatAudience.Subscribers) && chat.IsSubscriber)
               || (audience.HasFlag(ChatAudience.Regulars) && user?.Regular == true);
    }

    /// <summary>Handles the message when it is a "!tts" command; returns false for any other message.</summary>
    public async Task<bool> TryHandleAsync(ChatContext chat, SpeechUser? user, CancellationToken ct)
    {
        var prefix = Settings.Prefix.Trim().Length > 0 ? Settings.Prefix.Trim() : "!tts";
        var rest = Rest(chat.Message.Trim(), prefix, allowEmpty: true);
        if (rest is null) return false;

        var args = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var name = args.Length == 0 ? "commands" : args[0];
        if (Synonyms.TryGetValue(name, out var canonical)) name = canonical;
        if (!Commands.TryGetValue(name, out var command))
        {
            await ReplyAsync(chat, $"@{chat.User} unknown command. Try {prefix} commands", ct).ConfigureAwait(false);
            return true;
        }
        name = name.ToLowerInvariant();
        if (!Allows(PermissionOf(name, command), chat, user))
        {
            await ReplyNotAllowedAsync(chat, ct).ConfigureAwait(false);
            return true;
        }
        if (command.ViewerCommand && !chat.IsModeratorOrBroadcaster && OnCooldown(chat, name)) return true;

        string? reply;
        try
        {
            reply = Execute(name, args.Skip(1).ToArray(), chat, user, prefix);
        }
        catch (UsageException)
        {
            reply = $"@{chat.User} usage: {prefix} {command.Usage}";
        }
        if (reply is not null) await ReplyAsync(chat, reply, ct).ConfigureAwait(false);
        return true;
    }

    public Task ReplyNotAllowedAsync(ChatContext chat, CancellationToken ct)
    {
        var text = Settings.NotAllowedText;
        return string.IsNullOrWhiteSpace(text)
            ? Task.CompletedTask
            : ReplyAsync(chat, VariableResolver.Resolve(text, new Dictionary<string, object?> { ["user"] = chat.User }), ct);
    }

    private sealed class UsageException : Exception;

    private ChatAudience PermissionOf(string name, Command command)
    {
        foreach (var (key, value) in Settings.Permissions)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return command.DefaultPermission;
    }

    private bool OnCooldown(ChatContext chat, string name)
    {
        var seconds = Settings.ViewerCooldownSeconds;
        foreach (var (key, value) in Settings.Cooldowns)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) seconds = value;
        if (seconds <= 0) return false;
        var key2 = $"{chat.Platform}|{(chat.UserId.Length > 0 ? chat.UserId : chat.User)}|{name}";
        var now = time.GetUtcNow();
        lock (_lastUse)
        {
            if (_lastUse.TryGetValue(key2, out var last) && now - last < TimeSpan.FromSeconds(seconds)) return true;
            _lastUse[key2] = now;
            return false;
        }
    }

    private string? Execute(string name, string[] args, ChatContext chat, SpeechUser? user, string prefix)
    {
        var settings = speech.Settings;
        var at = $"@{chat.User}";
        switch (name)
        {
            case "status":
                return $"{at} TTS is {(speech.Enabled ? "on" : "off")}{(speech.Paused ? " (paused)" : "")}, "
                       + $"{speech.QueueLength} queued, mode {settings.SpeakChat.Mode.ToString().ToLowerInvariant()}, "
                       + $"events {(speech.EventsEnabled ? "on" : "off")}.";
            case "about":
                return $"{at} Cuebot text-to-speech. Try {prefix} commands";
            case "commands":
                return $"{at} {prefix} " + string.Join(", ", Commands
                    .Where(c => Allows(PermissionOf(c.Key, c.Value), chat, user))
                    .Select(c => c.Key));
            case "voices":
            {
                var aliases = ViewerAliases(chat.IsModeratorOrBroadcaster);
                var engines = speech.Engines.Count(e => e.IsAvailable);
                return aliases.Count == 0
                    ? $"{at} no voices are set up ({engines} engines available)."
                    : $"{at} voices: {string.Join(", ", aliases)} ({engines} engines available)";
            }
            case "voice":
                return SetOwnVoice(args, chat, user, at);
            case "speed":
            case "pitch":
                return SetOwnNumber(name, args, chat, at);
            case "nick":
                return SetOwnNickname(args, chat, at);
            case "me":
            {
                var on = ParseOnOff(args.FirstOrDefault());
                speech.Users.Update(chat.Platform, chat.UserId, chat.User, u => u.OptedOut = !on);
                return on ? $"{at} your messages will be read aloud again." : $"{at} your messages will no longer be read aloud.";
            }
            case "pause":
                speech.Paused = true;
                return $"{at} TTS paused.";
            case "resume":
                speech.Paused = false;
                return $"{at} TTS resumed.";
            case "clear":
            {
                var count = speech.QueueLength;
                speech.ClearQueue();
                return $"{at} cleared {count} queued message(s).";
            }
            case "skip":
            {
                if (args.Length == 0)
                {
                    speech.SkipCurrent();
                    return null;
                }
                var target = args[0].TrimStart('@');
                var stopped = speech.StopUser(chat.Platform, null, target);
                return $"{at} skipped {stopped} message(s) from {target}.";
            }
            case "on":
                speech.Enabled = true;
                return $"{at} TTS enabled.";
            case "off":
                speech.Enabled = false;
                return $"{at} TTS disabled.";
            case "mode":
            {
                var mode = args.FirstOrDefault()?.ToLowerInvariant() switch
                {
                    "all" => SpeakMode.All,
                    "command" or "commands" => SpeakMode.Command,
                    _ => throw new UsageException(),
                };
                if (mode == SpeakMode.Command && settings.Commands.SpeakCommands.Count == 0 && settings.Commands.CustomCommands.Count == 0)
                    return $"{at} command mode needs at least one speaking command.";
                settings.SpeakChat.Mode = mode;
                speech.SaveSettings();
                return $"{at} TTS mode is now {mode.ToString().ToLowerInvariant()}.";
            }
            case "ignore":
            case "reg":
            {
                if (args.Length < 2) throw new UsageException();
                var add = args[0].ToLowerInvariant() switch
                {
                    "add" => true,
                    "del" or "delete" or "remove" => false,
                    _ => throw new UsageException(),
                };
                var target = args[1].TrimStart('@');
                speech.Users.Update(chat.Platform, "", target, u =>
                {
                    if (name == "ignore") u.Ignored = add;
                    else u.Regular = add;
                });
                if (name == "ignore" && add) speech.StopUser(chat.Platform, null, target);
                var what = name == "ignore" ? "ignored" : "a regular";
                return add ? $"{at} {target} is now {what}." : $"{at} {target} is no longer {what}.";
            }
            case "ignored":
            {
                var ignored = speech.Users.All.Where(u => u.Ignored && SamePlatform(u, chat)).Select(u => u.UserName).ToArray();
                return ignored.Length == 0 ? $"{at} nobody is ignored." : $"{at} ignored: {string.Join(", ", ignored)}";
            }
            case "events":
                speech.EventsEnabled = ParseOnOff(args.FirstOrDefault());
                return $"{at} event speech {(speech.EventsEnabled ? "on" : "off")}.";
            case "random":
            {
                if (args.Length < 2 || !args[0].Equals("reset", StringComparison.OrdinalIgnoreCase)) throw new UsageException();
                var target = args[1].TrimStart('@');
                speech.Users.Update(chat.Platform, "", target, u => u.RandomVoice = "");
                return $"{at} {target} will get a new random voice.";
            }
            case "set":
                return Set(args, chat, at);
            case "assign":
                return Assign(args, chat, at);
            case "profile":
                return Profile(args, at);
            case "voicegate":
            {
                var profile = args.Length == 0 ? null : string.Join(' ', args);
                if (profile is null)
                    return $"{at} VoiceGate profiles: {string.Join(", ", settings.VoiceGate.Profiles.Select(p => p.Name))}"
                           + $" (active: {(settings.VoiceGate.ActiveProfile.Length > 0 ? settings.VoiceGate.ActiveProfile : "none")}).";
                return speech.ActivateVoiceGateProfile(profile)
                    ? $"{at} VoiceGate profile: {(settings.VoiceGate.ActiveProfile.Length > 0 ? settings.VoiceGate.ActiveProfile : "none")}."
                    : $"{at} no VoiceGate profile named {profile}.";
            }
            default:
                throw new UsageException();
        }
    }

    private IReadOnlyList<string> ViewerAliases(bool all)
    {
        var aliases = speech.Settings.Aliases.Select(a => a.Name).Where(n => n.Length > 0);
        var allowed = speech.Settings.Commands.ViewerAliases;
        if (!all && allowed.Count > 0) aliases = aliases.Where(a => allowed.Any(v => string.Equals(v, a, StringComparison.OrdinalIgnoreCase)));
        return aliases.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string SetOwnVoice(string[] args, ChatContext chat, SpeechUser? user, string at)
    {
        if (args.Length > 0 && args[0].Equals("set", StringComparison.OrdinalIgnoreCase)) args = args[1..];
        if (args.Length == 0)
            return user is { Alias.Length: > 0 } ? $"{at} your voice is {user.Alias}." : $"{at} you have no voice set.";
        var wanted = string.Join(' ', args);
        if (wanted.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            speech.Users.Update(chat.Platform, chat.UserId, chat.User, u => u.Alias = "");
            return $"{at} your voice was reset.";
        }
        var alias = ViewerAliases(chat.IsModeratorOrBroadcaster)
            .FirstOrDefault(a => string.Equals(a, wanted, StringComparison.OrdinalIgnoreCase));
        if (alias is null) return $"{at} unknown voice {wanted}.";
        speech.SetUserVoice(chat.Platform, chat.UserId, chat.User, alias);
        return $"{at} your voice is now {alias}.";
    }

    private string SetOwnNumber(string name, string[] args, ChatContext chat, string at)
    {
        if (args.Length == 0) throw new UsageException();
        int? value = args[0].Equals("reset", StringComparison.OrdinalIgnoreCase)
            ? null
            : int.TryParse(args[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n is >= -10 and <= 10
                ? n
                : throw new UsageException();
        speech.Users.Update(chat.Platform, chat.UserId, chat.User, u =>
        {
            if (name == "speed") u.Rate = value;
            else u.Pitch = value;
        });
        return value is null ? $"{at} your {name} was reset." : $"{at} your {name} is now {value}.";
    }

    private string SetOwnNickname(string[] args, ChatContext chat, string at)
    {
        if (!speech.Settings.Commands.AllowViewerNicknames && !chat.IsModeratorOrBroadcaster)
            return $"{at} nicknames are set by moderators.";
        if (args.Length == 0) throw new UsageException();
        var nick = string.Join(' ', args);
        if (nick.Equals("reset", StringComparison.OrdinalIgnoreCase)) nick = "";
        if (CheckNickname(nick) is { } problem) return $"{at} {problem}";
        speech.Users.Update(chat.Platform, chat.UserId, chat.User, u => u.Nickname = nick);
        return nick.Length == 0 ? $"{at} your nickname was removed." : $"{at} your nickname is now {nick}.";
    }

    private string? CheckNickname(string nick)
    {
        if (nick.Length > MaxNicknameLength) return $"nicknames can be at most {MaxNicknameLength} characters.";
        var profile = speech.Settings.Filters;
        return nick.Length > 0 && TextFilter.ApplyBadWords(nick, profile.BadWords, BadWordAction.Skip) is null
            ? "that nickname is not allowed."
            : null;
    }

    private string Set(string[] args, ChatContext chat, string at)
    {
        if (args.Length < 2) throw new UsageException();
        switch (args[0].ToLowerInvariant())
        {
            case "nickname":
            case "nick":
            {
                var target = args[1].TrimStart('@');
                var nick = string.Join(' ', args.Skip(2));
                if (CheckNickname(nick) is { } problem) return $"{at} {problem}";
                speech.Users.Update(chat.Platform, "", target, u => u.Nickname = nick);
                return nick.Length == 0 ? $"{at} removed {target}'s nickname." : $"{at} {target} is now spoken as {nick}.";
            }
            case "sticky":
                speech.StickyRandom = ParseOnOff(args[1]);
                return $"{at} sticky random voices {(speech.StickyRandom ? "on" : "off")}.";
            case "voice":
            {
                if (args.Length < 3) throw new UsageException();
                return AssignAlias(args[1].TrimStart('@'), string.Join(' ', args.Skip(2)), chat, at);
            }
            default:
                throw new UsageException();
        }
    }

    private string Assign(string[] args, ChatContext chat, string at)
    {
        if (args.Length < 2) throw new UsageException();
        if (!args[0].Equals("last", StringComparison.OrdinalIgnoreCase))
            return AssignAlias(args[0].TrimStart('@'), string.Join(' ', args.Skip(1)), chat, at);

        var target = args[1].TrimStart('@');
        if (speech.AssignLastVoice(chat.Platform, target) is not { } alias) return $"{at} {target} has not spoken yet.";
        var user = speech.Users.Find(chat.Platform, null, target);
        return $"{at} {(user is { UserName.Length: > 0 } ? user.UserName : target)} now always speaks with {alias.Name}.";
    }

    private string AssignAlias(string target, string aliasName, ChatContext chat, string at)
    {
        if (aliasName.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            speech.SetUserVoice(chat.Platform, "", target, "");
            return $"{at} {target}'s voice was reset.";
        }
        var alias = VoiceResolver.FindAlias(aliasName, speech.Settings.Aliases);
        if (alias is null) return $"{at} unknown voice {aliasName}.";
        speech.SetUserVoice(chat.Platform, "", target, alias.Name);
        return $"{at} {target} now speaks with {alias.Name}.";
    }

    private string Profile(string[] args, string at)
    {
        var settings = speech.Settings;
        if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            return $"{at} ignore profiles: {string.Join(", ", settings.VoiceIgnoreProfiles.Select(p => p.Name))}"
                   + $" (active: {(settings.ActiveVoiceIgnoreProfile.Length > 0 ? settings.ActiveVoiceIgnoreProfile : "none")}).";
        if (args[0].Equals("activate", StringComparison.OrdinalIgnoreCase)) args = args[1..];
        var name = string.Join(' ', args);
        return speech.ActivateVoiceIgnoreProfile(name)
            ? $"{at} ignore profile: {(settings.ActiveVoiceIgnoreProfile.Length > 0 ? settings.ActiveVoiceIgnoreProfile : "none")}."
            : $"{at} no ignore profile named {name}.";
    }

    private static bool SamePlatform(SpeechUser user, ChatContext chat) =>
        user.Platform.Length == 0 || string.Equals(user.Platform, chat.Platform, StringComparison.OrdinalIgnoreCase);

    private static bool ParseOnOff(string? value) => value?.ToLowerInvariant() switch
    {
        "on" or "enable" or "true" or "yes" => true,
        "off" or "disable" or "false" or "no" => false,
        _ => throw new UsageException(),
    };

    /// <summary>The text after <paramref name="prefix"/> when the message starts with it as a whole word.</summary>
    private static string? Rest(string message, string prefix, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = message[prefix.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0])) return null;
        rest = rest.Trim();
        return rest.Length == 0 && !allowEmpty ? null : rest;
    }

    private async Task ReplyAsync(ChatContext chat, string text, CancellationToken ct)
    {
        // Whispered commands get no chat feedback (as in Speaker.bot): the reply would land in public chat.
        if (Settings.SilenceOutput || chat.IsWhisper || speech.Chat is not { } chatReplier) return;
        try
        {
            await chatReplier.ReplyAsync(chat.Platform, text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            speech.Log.Warn("Speech", $"Could not reply in {chat.Platform} chat: {ex.Message}");
        }
    }
}
