using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

#if GNH_MAUI
using GnollHackM;
#else
using GnollHackX.Pages.Game;
using Xamarin.Essentials;
#endif

namespace GnollHackX
{
    /* What the player lets the local AI Overseer do in the game: nothing, press keys after the player confirms, or press
       keys on its own */
    public enum GHLocalAiControlMode
    {
        Off = 0,
        Ask,
        Auto
    }

    /* The press_keys tool of the local AI Overseer. The keys go to the game page one at a time as keyboard keys
       (GamePage.HandleKeyPress), so menus, questions and text inputs work as with the keyboard. After each key the tool
       waits until the game waits for input again, and at the end it returns the new messages and what the game shows.
       At the command prompt it refuses the commands that save, quit, pray or open extended commands, and it stops if the
       character's HP falls below half. */
    public static class GHLocalOverseerControl
    {
        public const string ToolName = "press_keys";
        public const int MaxCallsPerQuestion = 60;
        private const int SettleTimeoutMilliseconds = 20000;
        private const int SettlePollMilliseconds = 100;
        private const int MaxNewMessages = 40;

        private enum AiKeyKind
        {
            Char,
            Special,
            Meta,
            Ctrl,
            Direction
        }

        /* Direction names and the client's direction codes, which the core turns into the move keys of the current
           number_pad setting (convert_gnhch) */
        private static readonly string[] DirectionNames = { "sw", "s", "se", "w", "", "e", "nw", "n", "ne" };

        private sealed class AiKey
        {
            public AiKeyKind Kind;
            public int Value;
            public GHSpecialKey Special;
            public string Display;
        }

        /* Keys as text: characters are pressed as they are; {Esc}, {Enter}, {Space}, {M-x} (Alt/Meta), {C-x} (Ctrl) and the
           directions {N} ... {NW} are special. Returns null and sets error if the text has a bad {...}. */
        private static List<AiKey> ParseKeys(string keys, out string error)
        {
            error = null;
            List<AiKey> res = new List<AiKey>();
            int i = 0;
            while (i < keys.Length)
            {
                char c = keys[i];
                if (c != '{')
                {
                    res.Add(new AiKey { Kind = AiKeyKind.Char, Value = c, Display = c.ToString() });
                    i++;
                    continue;
                }
                int end = keys.IndexOf('}', i + 1);
                if (end < 0)
                {
                    error = "A { has no closing }.";
                    return null;
                }
                string name = keys.Substring(i + 1, end - i - 1).Trim();
                string display = "{" + name + "}";
                string lower = name.ToLowerInvariant();
                int direction = lower.Length > 0 ? Array.IndexOf(DirectionNames, lower) : -1;
                if (direction >= 0)
                    res.Add(new AiKey { Kind = AiKeyKind.Direction, Value = -1 - direction, Display = display });
                else if (lower == "esc" || lower == "escape")
                    res.Add(new AiKey { Kind = AiKeyKind.Special, Special = GHSpecialKey.Escape, Display = display });
                else if (lower == "enter" || lower == "return")
                    res.Add(new AiKey { Kind = AiKeyKind.Special, Special = GHSpecialKey.Enter, Display = display });
                else if (lower == "space")
                    res.Add(new AiKey { Kind = AiKeyKind.Special, Special = GHSpecialKey.Space, Display = display });
                else if (name.Length == 3 && (lower.StartsWith("m-") || lower.StartsWith("a-")))
                    res.Add(new AiKey { Kind = AiKeyKind.Meta, Value = name[2], Display = display });
                else if (name.Length == 3 && lower.StartsWith("c-"))
                    res.Add(new AiKey { Kind = AiKeyKind.Ctrl, Value = char.ToLowerInvariant(name[2]), Display = display });
                else
                {
                    error = "Unknown key " + display + ". Use {Esc}, {Enter}, {Space}, {M-x}, {C-x} or a direction {N} {NE} {E} {SE} {S} {SW} {W} {NW}.";
                    return null;
                }
                i = end + 1;
            }
            return res;
        }

        /* Null if the key may be pressed at the command prompt, else why not */
        private static string RefusedCommand(AiKey key)
        {
            if (key.Kind == AiKeyKind.Char && key.Value == '#')
                return "Extended commands (#) are not allowed. Use the command's own key, such as {M-l} for #loot.";
            if (key.Kind == AiKeyKind.Char && key.Value >= '0' && key.Value <= '9')
                return "Digits are not allowed as a command: depending on the number_pad option they move the character or start " +
                    "a count. Move with {N} {NE} {E} {SE} {S} {SW} {W} {NW}, and to do a command several times press its key " +
                    "several times, such as sss for three searches.";
            if (key.Kind == AiKeyKind.Meta)
            {
                switch (char.ToLowerInvariant((char)key.Value))
                {
                    case 's':
                        return "Saving the game ({M-s}) is not allowed; the player saves the game.";
                    case 'q':
                        return "Quitting the game ({M-q}) is not allowed.";
                    case 'p':
                        return "Praying ({M-p}) is not allowed; the player decides when to pray.";
                }
            }
            return null;
        }

        private static string KeysText(List<AiKey> keys, int count)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < count && i < keys.Count; i++)
                sb.Append(keys[i].Display);
            return sb.ToString();
        }

        private static bool TryGetStatusInt(GHGame game, NhStatusFields field, out int value)
        {
            string text = null;
            lock (game.StatusFieldLock)
            {
                text = game.StatusFields[(int)field].Text;
            }
            return int.TryParse(text?.Trim(), out value);
        }

        private static string DescribeStatus(GHGame game)
        {
            int hp, hpmax, turn;
            StringBuilder sb = new StringBuilder("Status:");
            if (TryGetStatusInt(game, NhStatusFields.BL_HP, out hp) && TryGetStatusInt(game, NhStatusFields.BL_HPMAX, out hpmax))
                sb.Append(" HP ").Append(hp).Append('/').Append(hpmax);
            if (TryGetStatusInt(game, NhStatusFields.BL_TIME, out turn))
                sb.Append(", turn ").Append(turn);
            return sb.ToString();
        }

        /* Waits until the game waits for input and the game page has shown what the game asked for */
        private static async Task<bool> WaitUntilSettledAsync(GHGame game, CancellationToken cancellationToken)
        {
            int stableState = -1;
            int stableCount = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < SettleTimeoutMilliseconds)
            {
                int state = game.KeyWaitState;
                bool waiting = GHGame.GetKeyWaitKind(state) != GHKeyWaitKind.None && game.RequestQueue.IsEmpty && game.ResponseQueue.IsEmpty;
                if (waiting && state == stableState)
                {
                    if (++stableCount >= 2)
                        return true;
                }
                else
                {
                    stableState = waiting ? state : -1;
                    stableCount = 0;
                }
                await Task.Delay(SettlePollMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        /* Presses one key on the UI thread; returns null, or why it was not pressed */
        private static Task<string> PressKeyAsync(AiKey key)
        {
            return MainThread.InvokeOnMainThreadAsync(() =>
            {
                GamePage page = GHApp.CurrentGamePage;
                if (page == null)
                    return "The game page is not open.";
                string block = page.AiControlBlockReason();
                if (block != null)
                    return block;
                if (page.AiControlIsCommandPrompt)
                {
                    string refused = RefusedCommand(key);
                    if (refused != null)
                        return refused;
                }

                bool handled;
                if (key.Kind == AiKeyKind.Char && page.AiControlIsTextInput)
                {
                    page.AiControlTypeText((char)key.Value);
                    handled = true;
                }
                else if (key.Kind == AiKeyKind.Special)
                    handled = page.HandleSpecialKeyPress(key.Special, false, false, false);
                else if (key.Kind == AiKeyKind.Direction)
                    handled = page.AiControlPressDirection(key.Value);
                else
                    handled = page.HandleKeyPress(key.Value, key.Kind == AiKeyKind.Ctrl, key.Kind == AiKeyKind.Meta);
                return handled ? null : "The game did not accept the key " + key.Display + " here.";
            });
        }

        private static Task<string> DescribeScreenAsync()
        {
            return MainThread.InvokeOnMainThreadAsync(() =>
            {
                GamePage page = GHApp.CurrentGamePage;
                return page != null ? page.DescribeScreenForAi() : "The game page is not open.\n";
            });
        }

        /* Runs press_keys. confirmAsync asks the player in Ask mode (keys, reason); reportStep shows a line in the chat. */
        public static async Task<string> ExecuteAsync(JObject arguments, GHLocalAiControlMode mode, Func<string, string, Task<bool>> confirmAsync,
            Action<string> reportStep, CancellationToken cancellationToken)
        {
            if (mode == GHLocalAiControlMode.Off)
                return "Control is Off: the player has not allowed you to press keys. Give advice instead, and tell the player that " +
                    "they can set Control to Ask or Auto in the Overseer window if they want you to do it.";

            string keysText = arguments?["keys"]?.ToString() ?? "";
            string reason = arguments?["reason"]?.ToString() ?? "";
            if (keysText.Length == 0)
                throw new ArgumentException("keys required");
            string error;
            List<AiKey> keys = ParseKeys(keysText, out error);
            if (keys == null)
                return "Nothing was pressed. " + error;

            GHGame game = GHApp.CurrentGHGame;
            if (game == null)
                return "Nothing was pressed: there is no game in progress.";
            if (game.PlayingReplay)
                return "Nothing was pressed: a replay is playing.";

            if (mode == GHLocalAiControlMode.Ask)
            {
                bool allowed = confirmAsync != null && await confirmAsync(KeysText(keys, keys.Count), reason).ConfigureAwait(false);
                if (!allowed)
                {
                    reportStep?.Invoke("Not pressed: " + KeysText(keys, keys.Count));
                    return "The player declined, so nothing was pressed. Do not press these keys; ask the player what to do instead.";
                }
            }

            int messageMark = game.AddedMessageCount;
            int startHp;
            int hpmax = 0;
            bool hasHp = TryGetStatusInt(game, NhStatusFields.BL_HP, out startHp) && TryGetStatusInt(game, NhStatusFields.BL_HPMAX, out hpmax);

            string stopReason = null;
            int pressed = 0;
            for (; pressed < keys.Count; pressed++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await WaitUntilSettledAsync(game, cancellationToken).ConfigureAwait(false))
                {
                    stopReason = "The game stayed busy too long.";
                    break;
                }
                int hp;
                if (hasHp && TryGetStatusInt(game, NhStatusFields.BL_HP, out hp) && hp < startHp && hp * 2 < hpmax)
                {
                    stopReason = "HP fell below half (" + hp + "/" + hpmax + ").";
                    break;
                }
                stopReason = await PressKeyAsync(keys[pressed]).ConfigureAwait(false);
                if (stopReason != null)
                    break;
            }
            await WaitUntilSettledAsync(game, cancellationToken).ConfigureAwait(false);

            string pressedText = KeysText(keys, pressed);
            reportStep?.Invoke((pressed > 0 ? "Pressed " + pressedText : "Pressed nothing") + (stopReason != null ? ". Stopped: " + stopReason : ""));

            StringBuilder sb = new StringBuilder();
            sb.Append("Pressed: ").Append(pressed > 0 ? pressedText : "nothing").Append('\n');
            if (stopReason != null)
                sb.Append("Stopped before ").Append(keys[pressed].Display).Append(": ").Append(stopReason).Append('\n');
            List<string> messages = game.GetMessagesSince(messageMark, MaxNewMessages);
            if (messages.Count == 0)
                sb.Append("No new messages.\n");
            else
            {
                sb.Append("New messages:\n");
                foreach (string message in messages)
                    sb.Append("- ").Append(message).Append('\n');
            }
            sb.Append("Screen now:\n").Append(await DescribeScreenAsync().ConfigureAwait(false));
            sb.Append(DescribeStatus(game));
            return sb.ToString();
        }
    }
}
