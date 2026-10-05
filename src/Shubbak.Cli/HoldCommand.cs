using Shubbak.Companion;
using Shubbak.Config;
using Shubbak.Ipc;

namespace Shubbak.Cli;

/// <summary>
/// What <c>shubbak context ... --hold</c> asked for, read from the arguments.
/// </summary>
/// <param name="Command">The command to send, spelled for the pipe, with <c>--lease</c> on and <c>--hold</c> off.</param>
/// <param name="Context">The context's name, for what is said on the console.</param>
public sealed record HoldArguments(string Command, string Context)
{
    /// <summary>The flag that means "stay", which the window manager never sees.</summary>
    public const string Flag = "--hold";

    /// <summary>
    /// Reads a <c>context</c> command line for <c>--hold</c>, or says why it cannot be
    /// held.
    /// </summary>
    /// <param name="args">The arguments as typed, <c>context</c> first.</param>
    /// <param name="problem">Why not, when the answer is null and the flag was given.</param>
    /// <returns>What to hold, or null when <c>--hold</c> was not asked for or cannot be honoured.</returns>
    /// <remarks>
    /// <c>--lease</c> is added when it was left out, since holding is what a lease is
    /// for; <c>--hold</c> itself is taken out, since it is this program's word and the
    /// parser would refuse it. Everything else - <c>--set</c>, <c>--ttl</c>, the name -
    /// is passed on as written and judged by the window manager's parser, so a mistake
    /// is reported in the parser's words rather than a second set.
    /// </remarks>
    public static HoldArguments? Parse(IReadOnlyList<string> args, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        problem = null;

        if (args.Count == 0 || !string.Equals(args[0], "context", StringComparison.OrdinalIgnoreCase)) return null;
        if (!args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase))) return null;

        List<string> kept = [.. args.Where(a => !string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase))];

        if (kept.Any(a => string.Equals(a, "--auto", StringComparison.OrdinalIgnoreCase)))
        {
            problem = "--hold keeps a pin held, and --auto takes the pin off; one or the other.";
            return null;
        }

        if (!kept.Any(a => string.Equals(a, "--lease", StringComparison.OrdinalIgnoreCase))) kept.Add("--lease");

        // Spelled for the pipe the way every forwarded command is: the shell has thrown
        // its quotes away, and a name with a space in it must arrive as one token.
        string command = string.Join(' ', kept.Select(a => CommandParser.CanQuote(a) ? CommandParser.Quote(a) : a));

        if (!CommandParser.TryParse(command, default, out Core.Commands.WmCommand? parsed, out Diagnostic? error))
        {
            problem = error!.Hint is { Length: > 0 } hint ? $"{error.Message} {hint}" : error.Message;
            return null;
        }

        if (parsed is not Core.Commands.ContextCommand context)
        {
            problem = "--hold is for the context command.";
            return null;
        }

        return new HoldArguments(command, context.Context);
    }
}

/// <summary>
/// <c>shubbak context --set meeting --hold</c>: pins a context with a lease and stays,
/// so that the lease holds for as long as this process runs.
/// </summary>
/// <remarks>
/// <para>
/// A lease dies with the connection that made it, and the command line's connection
/// closes the moment the reply arrives - so <c>--lease</c> from a script did nothing
/// visible and was refused with a hint to "hold a pipe connection open from your own
/// process". That is the whole of a provider, and it was left as an exercise: work
/// out the pipe's name from the SID, write one line of JSON, read one back, and keep
/// the stream open. This is that process, so any script is a provider:
/// </para>
/// <code>
/// $hold = Start-Process shubbak -ArgumentList 'context --set in-meeting --hold' -PassThru
/// ...
/// Stop-Process $hold
/// </code>
/// <para>
/// Ending the process is letting go - the pin dies with the connection, which is what
/// a lease is for - and so is Ctrl+C. Nothing is read from standard input, so a
/// script that starts this with its input redirected from nothing does not see it
/// leave at once.
/// </para>
/// <para>
/// The watcher's shape underneath: a subscription that reconnects for as long as the
/// process lives, and the pin sent again each time it connects, so a window manager
/// that restarts is holding the context again within a second of coming back - and a
/// reload, which drops every pin on a context the reloaded file no longer declares
/// and tells nobody, is followed by the pin being asserted again. One connection does
/// both: the pin is made on the connection the events arrive on, so the lease lives
/// exactly as long as the subscription, and a reconnect is the pin being made again.
/// <c>exit-all</c> is the one shutdown this leaves on: the user is done with Shubbak,
/// and a process waiting to pin a context on a window manager that is not coming back
/// is exactly what a leased pin exists to avoid.
/// </para>
/// </remarks>
public static class HoldCommand
{
    /// <summary>Runs the hold until <paramref name="stop"/> is cancelled or the window manager leaves for good.</summary>
    /// <param name="hold">What to hold.</param>
    /// <param name="output">Where to say what is happening; the console.</param>
    /// <param name="error">Where to say what went wrong.</param>
    /// <param name="pipeName">The window manager's pipe, or null for it; a test names its own.</param>
    /// <param name="stop">Ctrl+C, or a test.</param>
    /// <returns>0 when the hold ended because it was asked to; 1 when the pin was refused outright; 2 when no window manager could be reached at first.</returns>
    public static async Task<int> RunAsync(
        HoldArguments hold, TextWriter output, TextWriter error, string? pipeName, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string pipe = pipeName ?? IpcProtocol.PipeName;

        // Said here rather than waited out. A hold started with no window manager
        // running is far more likely a mistake than a plan, and a process that sat
        // silently waiting for one would be a puzzle rather than a message.
        if (!IpcClient.IsServerRunning(pipe))
        {
            await error.WriteLineAsync("shubbak: no window manager is running.").ConfigureAwait(false);
            await error.WriteLineAsync("hint: start it with shubbak-wm").ConfigureAwait(false);
            return 2;
        }

        var ended = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool heldOnce = false;

        await using var pump = new EventPump("config.reloaded," + IpcProtocol.ShutdownTopic)
        {
            ProgramName = "shubbak",
            PipeName = pipe,
            ConnectTimeout = TimeSpan.FromSeconds(2),

            Subscribed = async (connection, token) =>
            {
                IpcResponse response = await connection.Events.SendAsync("command", hold.Command, token).ConfigureAwait(false);

                if (response.Ok)
                {
                    await output.WriteLineAsync(heldOnce
                        ? $"holding \"{hold.Context}\" again; the window manager is back"
                        : $"holding \"{hold.Context}\"; Ctrl+C, or end this process, to let go").ConfigureAwait(false);

                    heldOnce = true;
                    return;
                }

                // Refused the first time is the answer: the context is not declared, or
                // the command was wrong, and neither will change by waiting. Refused on
                // a later connection is a window manager whose file changed under it,
                // which the next reload may change back; said, and waited out.
                if (!heldOnce)
                {
                    await error.WriteLineAsync($"shubbak: {response.Error}").ConfigureAwait(false);
                    ended.TrySetResult(1);
                    return;
                }

                await error.WriteLineAsync($"shubbak: \"{hold.Context}\" was refused when asked again: {response.Error}").ConfigureAwait(false);
            },

            Event = async (connection, notification, token) =>
            {
                switch (notification.Topic)
                {
                    case "config.reloaded":
                        // The window manager drops the pins of contexts the reloaded
                        // file no longer declares and says nothing; asserting again is
                        // one command, and replaces the pin with itself when it is still
                        // there. A reload the window manager refused touched no pin and
                        // needs nothing.
                        if (!ConfigReloadNotice.Parse(notification.Data).Accepted) break;

                        IpcResponse response = await connection.Events.SendAsync("command", hold.Command, token).ConfigureAwait(false);

                        if (!response.Ok)
                            await error.WriteLineAsync($"shubbak: after a reload, \"{hold.Context}\" was refused: {response.Error}").ConfigureAwait(false);

                        break;

                    case IpcProtocol.ShutdownTopic when ShutdownNotice.IsForEveryone(notification.Data):
                        await output.WriteLineAsync("the window manager is shutting everything down; letting go").ConfigureAwait(false);
                        ended.TrySetResult(0);
                        break;

                    case IpcProtocol.ShutdownTopic:
                        await output.WriteLineAsync("the window manager is restarting; the pin will be held again when it is back").ConfigureAwait(false);
                        break;
                }
            },
        };

        pump.Start();

        await using CancellationTokenRegistration letGo = stop.Register(() => ended.TrySetResult(0));

        return await ended.Task.ConfigureAwait(false);
    }
}
