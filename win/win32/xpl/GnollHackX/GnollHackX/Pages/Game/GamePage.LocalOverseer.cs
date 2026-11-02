using System;
using System.Collections.Generic;
using System.Text;
using GnollHackX;

#if GNH_MAUI
namespace GnollHackM
#else
using GnollHackX.Controls;
using Xamarin.Forms;

namespace GnollHackX.Pages.Game
#endif
{
    /* Keys from the local AI Overseer (GHLocalOverseerControl). The keys go through HandleKeyPress and
       HandleSpecialKeyPress like keyboard keys; the text of the dialog on screen is described for the AI.
       All methods run on the UI thread. */
    public partial class GamePage
    {
        private const int AiControlMaxMenuLines = 80;
        private const int AiControlMaxTextLines = 60;

        /* Null if the keys of the AI can be handled now, else why not */
        internal string AiControlBlockReason()
        {
            if (PlayingReplay)
                return "A replay is playing.";
            if (LoadingGrid.IsVisible)
                return "The game is loading.";
            if (!GHApp.IsPageOnTopOfModalNavigationStack(this))
                return "Another page, such as the game menu, is open over the game.";
            if (IsRadialMenuOpen || MoreCommandsGrid.IsVisible)
                return "A command menu of the game is open.";
            return null;
        }

        /* A text input (getline) is open: characters go into its text field */
        internal bool AiControlIsTextInput { get { return GetLineGrid.IsVisible; } }

        /* The game waits for a command, not for an answer to a dialog or a position */
        internal bool AiControlIsCommandPrompt
        {
            get
            {
                return GHApp.CurrentGHGame != null && GHApp.CurrentGHGame.AwaitingCommand && !IsAiControlDialogVisible() && !IsGetPosCursor();
            }
        }

        private bool IsAiControlDialogVisible()
        {
            return MenuGrid.IsVisible || TextGrid.IsVisible || PopupGrid.IsVisible || GetLineGrid.IsVisible || YnGrid.IsVisible;
        }

        internal void AiControlTypeText(char c)
        {
            GetLineEntryText.Text = (GetLineEntryText.Text ?? "") + c;
        }

        /* A direction code -1 ... -9 (numpad layout), as the direction buttons send it; not while a dialog is open */
        internal bool AiControlPressDirection(int code)
        {
            if (IsAiControlDialogVisible())
                return false;
            GenericButton_Clicked(null, null, code);
            return true;
        }

        /* What the game shows and how to answer it, for the AI */
        internal string DescribeScreenForAi()
        {
            StringBuilder sb = new StringBuilder();
            if (MenuGrid.IsVisible)
            {
                SelectionMode how = MenuCanvas.SelectionHow;
                sb.Append("A menu is open: ").Append(MenuHeaderLabel.Text ?? "");
                if (!string.IsNullOrWhiteSpace(MenuSubtitleLabel.Text))
                    sb.Append(" (").Append(MenuSubtitleLabel.Text).Append(')');
                sb.Append('\n');
                if (MenuCountGrid.IsVisible)
                    sb.Append("A count is being entered for a menu item: type digits, then {Enter}, or {Esc}.\n");
                else if (how == SelectionMode.Multiple)
                    sb.Append("You can pick several: press the letters to select or unselect, then {Enter} to accept, or {Esc} to cancel. A number before a letter picks that many.\n");
                else if (how == SelectionMode.Single)
                    sb.Append("Pick one by pressing its letter, or {Esc} to cancel.\n");
                else
                    sb.Append("Nothing can be picked: press {Enter} or {Esc} to close it.\n");

                IList<GHMenuItem> items = MenuCanvas.MenuItems;
                int lines = 0;
                if (items != null)
                {
                    foreach (GHMenuItem item in items)
                    {
                        if (++lines > AiControlMaxMenuLines)
                        {
                            sb.Append("[more lines not shown]\n");
                            break;
                        }
                        if (item.Accelerator != '\0' && item.Accelerator != ' ')
                        {
                            sb.Append(item.Accelerator).Append(" - ").Append(item.Text);
                            if (item.Selected)
                                sb.Append(item.Count > 0 ? " [selected, count " + item.Count + "]" : " [selected]");
                        }
                        else
                        {
                            sb.Append("   ").Append(item.Text);
                        }
                        sb.Append('\n');
                    }
                }
            }
            else if (YnGrid.IsVisible)
            {
                sb.Append("The game asks: ").Append(YnTitleLabel.Text ?? "").Append(' ').Append(YnQuestionLabel.Text ?? "").Append('\n');
                sb.Append("Answer by pressing one of these keys, or {Esc} to cancel:");
                LabeledImageButton[] buttons = { ZeroButton, FirstButton, SecondButton, ThirdButton, FourthButton };
                foreach (LabeledImageButton button in buttons)
                {
                    if (button.IsVisible)
                        sb.Append(' ').Append(button.BtnLetter).Append(" (").Append(button.LblText).Append(')');
                }
                sb.Append('\n');
            }
            else if (GetLineGrid.IsVisible)
            {
                sb.Append("The game asks for text: ").Append(GetLineCaption.Text ?? "").Append('\n');
                sb.Append("Text so far: \"").Append(GetLineEntryText.Text ?? "").Append("\"\n");
                sb.Append("Type the text, then {Enter} to accept, or {Esc} to cancel.\n");
            }
            else if (PopupGrid.IsVisible)
            {
                sb.Append("A message window is open: ").Append(PopupTitleLabel.Text ?? "").Append('\n').Append(PopupLabel.Text ?? "").Append('\n');
                sb.Append("Press {Enter} to close it.\n");
            }
            else if (TextGrid.IsVisible)
            {
                sb.Append("A text window is open:\n");
                List<GHPublishedWindowRow> rows = TextCanvas.PutStrItems;
                int lines = 0;
                if (rows != null)
                {
                    foreach (GHPublishedWindowRow row in rows)
                    {
                        if (++lines > AiControlMaxTextLines)
                        {
                            sb.Append("[more lines not shown]\n");
                            break;
                        }
                        sb.Append(row.Text).Append('\n');
                    }
                }
                sb.Append("Press {Enter} to close it.\n");
            }
            else if (IsGetPosCursor())
            {
                sb.Append("The game asks you to choose a position on the map with the cursor. Move it one square with " +
                    "{N} {NE} {E} {SE} {S} {SW} {W} {NW}, or jump: < > to stairs, _ to an altar, m to monsters, o to objects, d to doors, " +
                    "@ to yourself. Then press . or , to choose the position, or {Esc} to cancel.\n");
            }
            else
            {
                GHGame game = GHApp.CurrentGHGame;
                GHKeyWaitKind kind = game != null ? game.KeyWaitKind : GHKeyWaitKind.None;
                if (game != null && game.AwaitingCommand)
                    sb.Append("The game waits for your next command.\n");
                else if (kind == GHKeyWaitKind.Direction)
                    sb.Append("The game asks for a direction: {N} {NE} {E} {SE} {S} {SW} {W} {NW}, < (up), > (down), . (here or yourself), or {Esc} to cancel.\n");
                else if (kind == GHKeyWaitKind.None)
                    sb.Append("The game is busy.\n");
                else
                    sb.Append("The game waits for a key; see the latest messages for what it asks.\n");
            }
            return sb.ToString();
        }
    }
}
