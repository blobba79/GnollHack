#if GNH_MAUI
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GnollHackX;

namespace GnollHackM
{
    /* The chat window of Gnoll Overseer on the player's own AI (GHLocalOverseerClient). Built in code; shown in its own
       window where GHGuiWindows supports it, else as a modal page. */
    public sealed class GHLocalOverseerPage : ContentPage
    {
        private static readonly Color BackgroundColorValue = Color.FromRgb(0x14, 0x10, 0x0C);
        private static readonly Color PlayerBubbleColor = Color.FromRgb(0x3A, 0x2A, 0x18);
        private static readonly Color OverseerBubbleColor = Color.FromRgb(0x24, 0x1C, 0x14);
        private static readonly Color AccentColor = Color.FromRgb(0xFF, 0xCC, 0x44);
        private static readonly Color DimTextColor = Color.FromRgb(0xA8, 0xA0, 0x90);
        private const string FontName = "Underwood"; /* Has no ellipsis character, so "..." is used */

        private readonly GHLocalOverseerClient _client;
        private readonly string _initialSnapshot;
        private readonly bool _inOwnWindow;

        private readonly VerticalStackLayout _messages = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(12) };
        private readonly ScrollView _scroll = new ScrollView();
        private readonly Entry _input = new Entry();
        private readonly Button _sendButton = new Button();
        private readonly Button _stopButton = new Button();
        private readonly Button _thinkButton = new Button();
        private readonly Button _controlButton = new Button();
        private readonly Button _updateButton = new Button();
        private readonly Button _clearButton = new Button();
        private readonly Button _closeButton = new Button();
        private readonly Label _status = new Label();

        private bool _think;
        private bool _busy;
        private bool _started;
        private CancellationTokenSource _cancellation;

        /* The answer being streamed */
        private Label _answerLabel;
        private Label _reasoningLabel;
        private Label _reasoningToggle;
        private readonly StringBuilder _answerText = new StringBuilder();
        private readonly StringBuilder _reasoningText = new StringBuilder();

        public GHLocalOverseerPage(string snapshotText, bool inOwnWindow)
        {
            _client = new GHLocalOverseerClient(GHApp.LocalAiAddress, GHApp.LocalAiKey, GHApp.LocalAiModel);
            _client.ContentDelta += Client_ContentDelta;
            _client.ReasoningDelta += Client_ReasoningDelta;
            _client.ToolCalled += Client_ToolCalled;
            _client.ControlStep += Client_ControlStep;
            _client.ConfirmKeysAsync = ConfirmKeysAsync;
            _initialSnapshot = snapshotText;
            _inOwnWindow = inOwnWindow;

            Title = "Gnoll Overseer (local AI)";
            BackgroundColor = BackgroundColorValue;

            Label title = new Label
            {
                Text = "Gnoll Overseer",
                FontFamily = "Diablo",
                FontSize = 22,
                TextColor = AccentColor,
                VerticalOptions = LayoutOptions.Center,
            };
            _status.FontFamily = FontName;
            _status.FontSize = 13;
            _status.TextColor = DimTextColor;
            _status.VerticalOptions = LayoutOptions.Center;
            _status.Text = GHApp.LocalAiModel + " at " + GHApp.LocalAiAddress;

            SetupButton(_thinkButton, "Think: Off", ThinkButton_Clicked);
            SetupButton(_controlButton, "Control: Off", ControlButton_Clicked);
            SetupButton(_updateButton, "Update game state", UpdateButton_Clicked);
            SetupButton(_clearButton, "Clear", ClearButton_Clicked);
            SetupButton(_closeButton, "Close", CloseButton_Clicked);
            SetupButton(_sendButton, "Send", SendButton_Clicked);
            SetupButton(_stopButton, "Stop", StopButton_Clicked);
            _stopButton.IsVisible = false;

            HorizontalStackLayout toolbar = new HorizontalStackLayout { Spacing = 8 };
            toolbar.Children.Add(_thinkButton);
            toolbar.Children.Add(_controlButton);
            toolbar.Children.Add(_updateButton);
            toolbar.Children.Add(_clearButton);
            toolbar.Children.Add(_closeButton);

            Grid header = new Grid
            {
                Padding = new Thickness(12, 8),
                ColumnSpacing = 12,
                ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            };
            header.Add(title, 0, 0);
            header.Add(_status, 1, 0);
            header.Add(toolbar, 2, 0);

            _scroll.Content = _messages;

            _input.FontFamily = FontName;
            _input.FontSize = 16;
            _input.TextColor = Colors.White;
            _input.PlaceholderColor = DimTextColor;
            _input.Placeholder = "Ask the Overseer...";
            _input.BackgroundColor = Color.FromRgb(0x2A, 0x22, 0x1A);
            _input.Completed += Input_Completed;

            Grid inputRow = new Grid
            {
                Padding = new Thickness(12, 8),
                ColumnSpacing = 8,
                ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) },
            };
            inputRow.Add(_input, 0, 0);
            inputRow.Add(_sendButton, 1, 0);
            inputRow.Add(_stopButton, 2, 0);

            Grid root = new Grid
            {
                RowDefinitions = new RowDefinitionCollection { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            };
            root.Add(header, 0, 0);
            root.Add(_scroll, 0, 1);
            root.Add(inputRow, 0, 2);
            Content = root;
        }

        private static void SetupButton(Button button, string text, EventHandler clicked)
        {
            button.Text = text;
            button.FontFamily = FontName;
            button.FontSize = 14;
            button.TextColor = Colors.White;
            button.BackgroundColor = Color.FromRgb(0x5A, 0x24, 0x14);
            button.Padding = new Thickness(12, 4);
            button.Clicked += clicked;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (_started)
                return;
            _started = true;
            _ = StartAsync();
        }

        private async Task StartAsync()
        {
            SetBusy(true);
            try
            {
                string problem = await _client.CheckServerAsync(CancellationToken.None);
                if (problem != null)
                {
                    AddNote(problem + " Start your local AI server (for example START-QWEN-FINAL.bat) and open the Overseer again.", true);
                    _status.Text = "Not connected";
                    return;
                }
                _status.Text = GHApp.LocalAiModel + " at " + GHApp.LocalAiAddress;
                if (!_client.UsesInstructionsFile && !string.IsNullOrWhiteSpace(GHApp.LocalAiInstructionsPath))
                    AddNote("The instructions file could not be read, so the built-in instructions are used: " + GHApp.LocalAiInstructionsPath, true);
                if (GHLocalOverseerTools.FindSourceFolder() == null)
                    AddNote("The GnollHack source code was not found, so the Overseer cannot verify rules in it. Set Local AI Source Folder in Settings.", true);
                if (!string.IsNullOrWhiteSpace(_initialSnapshot))
                {
                    _client.AddGameState(_initialSnapshot);
                    AddNote("Reading the game state... This can take about half a minute the first time.", false);
                    try
                    {
                        await Task.Run(() => _client.WarmUpAsync(CancellationToken.None));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                    AddNote("Ready. Ask anything about your game.", false);
                }
                else
                {
                    AddNote("Ready. No game state was sent; the Overseer can fetch it with its tools when needed.", false);
                }
            }
            catch (Exception ex)
            {
                AddNote("Could not start the Overseer: " + ex.Message, true);
            }
            finally
            {
                SetBusy(false);
                _input.Focus();
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _sendButton.IsVisible = !busy;
            _stopButton.IsVisible = busy && _cancellation != null;
            _updateButton.IsEnabled = !busy;
            _clearButton.IsEnabled = !busy;
        }

        /*
         * Messages
         */

        private void ScrollToEnd()
        {
            Dispatcher.Dispatch(async () =>
            {
                try
                {
                    await _scroll.ScrollToAsync(0, Math.Max(0, _messages.Height), false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            });
        }

        private Border CreateBubble(View content, bool isPlayer)
        {
            return new Border
            {
                Content = content,
                BackgroundColor = isPlayer ? PlayerBubbleColor : OverseerBubbleColor,
                Stroke = isPlayer ? Color.FromRgb(0x7A, 0x5A, 0x30) : Color.FromRgb(0x4A, 0x3A, 0x28),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(12, 8),
                HorizontalOptions = isPlayer ? LayoutOptions.End : LayoutOptions.Start,
                MaximumWidthRequest = 900,
            };
        }

        private Label CreateTextLabel(string text, Color color, double size)
        {
            return new Label
            {
                Text = text,
                FontFamily = FontName,
                FontSize = size,
                TextColor = color,
                LineBreakMode = LineBreakMode.WordWrap,
            };
        }

        private void AddPlayerMessage(string text)
        {
            _messages.Children.Add(CreateBubble(CreateTextLabel(text, Colors.White, 16), true));
            ScrollToEnd();
        }

        private void AddNote(string text, bool isError)
        {
            Label label = CreateTextLabel(text, isError ? Color.FromRgb(0xFF, 0x80, 0x60) : DimTextColor, 14);
            label.FontAttributes = FontAttributes.Italic;
            label.HorizontalOptions = LayoutOptions.Center;
            label.HorizontalTextAlignment = TextAlignment.Center;
            _messages.Children.Add(label);
            ScrollToEnd();
        }

        /* Starts an empty Overseer answer with a collapsed thinking section */
        private void StartAnswer()
        {
            _answerText.Clear();
            _reasoningText.Clear();
            VerticalStackLayout content = new VerticalStackLayout { Spacing = 6 };

            _reasoningToggle = CreateTextLabel("▸ Thinking...", DimTextColor, 13);
            _reasoningToggle.IsVisible = false;
            _reasoningLabel = CreateTextLabel("", DimTextColor, 13);
            _reasoningLabel.FontAttributes = FontAttributes.Italic;
            _reasoningLabel.IsVisible = false;
            Label toggle = _reasoningToggle;
            Label reasoning = _reasoningLabel;
            TapGestureRecognizer tap = new TapGestureRecognizer();
            tap.Tapped += (s, e) =>
            {
                reasoning.IsVisible = !reasoning.IsVisible;
                toggle.Text = (reasoning.IsVisible ? "▾ " : "▸ ") + "Thinking";
            };
            _reasoningToggle.GestureRecognizers.Add(tap);

            _answerLabel = CreateTextLabel("...", Colors.White, 16);
            content.Children.Add(_reasoningToggle);
            content.Children.Add(_reasoningLabel);
            content.Children.Add(_answerLabel);
            _messages.Children.Add(CreateBubble(content, false));
            ScrollToEnd();
        }

        /* Shows **bold** text from the answer's markdown; other markdown stays as text */
        private static FormattedString FormatAnswer(string text)
        {
            FormattedString formatted = new FormattedString();
            string[] parts = (text ?? "").Split(new string[] { "**" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0)
                    continue;
                formatted.Spans.Add(new Span
                {
                    Text = parts[i].Replace("\n* ", "\n• ").Replace("\n- ", "\n• "),
                    FontFamily = FontName,
                    FontAttributes = (i % 2) == 1 ? FontAttributes.Bold : FontAttributes.None,
                });
            }
            return formatted;
        }

        private void Client_ContentDelta(string text)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_answerLabel == null)
                    return;
                _answerText.Append(text);
                _answerLabel.Text = _answerText.ToString();
                if (_reasoningLabel != null && _reasoningLabel.IsVisible == false && _reasoningToggle != null && _reasoningText.Length > 0)
                    _reasoningToggle.Text = "▸ Thinking";
                ScrollToEnd();
            });
        }

        private void Client_ReasoningDelta(string text)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_reasoningLabel == null)
                    return;
                _reasoningText.Append(text);
                _reasoningLabel.Text = _reasoningText.ToString();
                _reasoningToggle.IsVisible = true;
                ScrollToEnd();
            });
        }

        private static string DescribeTool(string name)
        {
            switch (name)
            {
                case "refresh_snapshot":
                    return "the current game state";
                case "get_full_message_history":
                    return "the message history";
                case "get_player_library":
                    return "your library";
                case "get_oracle_consultations":
                    return "your Oracle consultations";
                case "get_player_xlog":
                    return "your finished games";
                case "get_player_dumplogs":
                    return "your dump logs";
                case "search_gnollhack_code":
                    return "the GnollHack source code";
                case "read_gnollhack_file":
                    return "a GnollHack source file";
                case "lookup_encyclopedia":
                    return "the encyclopedia";
                default:
                    return name;
            }
        }

        private void Client_ToolCalled(string name)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                FinishAnswer(true);
                if (name != GHLocalOverseerControl.ToolName) /* Client_ControlStep shows what it did */
                    AddNote("Looked up " + DescribeTool(name) + ".", false);
                StartAnswer();
            });
        }

        private void Client_ControlStep(string text)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                FinishAnswer(true);
                AddNote(text, false);
                StartAnswer();
            });
        }

        /* Ask mode: shows the keys with Run and No buttons and waits for the player; Stop counts as No */
        private Task<bool> ConfirmKeysAsync(string keys, string reason)
        {
            TaskCompletionSource<bool> decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken token = _cancellation?.Token ?? CancellationToken.None;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                FinishAnswer(true);
                VerticalStackLayout content = new VerticalStackLayout { Spacing = 8 };
                content.Children.Add(CreateTextLabel("The Overseer wants to press: " + keys, AccentColor, 16));
                if (!string.IsNullOrWhiteSpace(reason))
                    content.Children.Add(CreateTextLabel(reason, Colors.White, 15));
                Button runButton = new Button();
                Button noButton = new Button();
                HorizontalStackLayout buttons = new HorizontalStackLayout { Spacing = 8 };
                buttons.Children.Add(runButton);
                buttons.Children.Add(noButton);
                content.Children.Add(buttons);
                SetupButton(runButton, "Run", (s, e) => decision.TrySetResult(true));
                SetupButton(noButton, "No", (s, e) => decision.TrySetResult(false));
                _messages.Children.Add(CreateBubble(content, false));
                ScrollToEnd();
                token.Register(() => decision.TrySetResult(false));
                /* The answer goes on after Client_ControlStep, which starts a new answer */
                _ = decision.Task.ContinueWith(t => MainThread.BeginInvokeOnMainThread(() =>
                {
                    runButton.IsEnabled = false;
                    noButton.IsEnabled = false;
                }));
            });
            return decision.Task;
        }

        /* Ends the streamed answer; an empty one is removed */
        private void FinishAnswer(bool removeIfEmpty)
        {
            if (_answerLabel == null)
                return;
            string text = _answerText.ToString().Trim();
            if (text.Length == 0)
            {
                if (removeIfEmpty && _reasoningText.Length == 0 && _messages.Children.Count > 0)
                    _messages.Children.RemoveAt(_messages.Children.Count - 1);
                else
                    _answerLabel.Text = "";
            }
            else
            {
                _answerLabel.Text = null;
                _answerLabel.FormattedText = FormatAnswer(text);
            }
            _answerLabel = null;
            _reasoningLabel = null;
            _reasoningToggle = null;
        }

        /*
         * Actions
         */

        private void Input_Completed(object sender, EventArgs e)
        {
            SendButton_Clicked(sender, e);
        }

        private async void SendButton_Clicked(object sender, EventArgs e)
        {
            string text = _input.Text?.Trim();
            if (_busy || string.IsNullOrEmpty(text))
                return;
            _input.Text = "";
            AddPlayerMessage(text);
            StartAnswer();
            _cancellation = new CancellationTokenSource();
            SetBusy(true);
            bool think = _think;
            CancellationToken token = _cancellation.Token;
            try
            {
                await Task.Run(() => _client.SendAsync(text, think, token));
                FinishAnswer(false);
            }
            catch (OperationCanceledException)
            {
                FinishAnswer(false);
                AddNote("Stopped.", false);
            }
            catch (ObjectDisposedException)
            {
                FinishAnswer(false);
                AddNote("Stopped.", false);
            }
            catch (Exception ex)
            {
                FinishAnswer(true);
                AddNote("The local AI did not answer: " + ex.Message, true);
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                SetBusy(false);
                _input.Focus();
            }
        }

        private void StopButton_Clicked(object sender, EventArgs e)
        {
            try
            {
                _cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ThinkButton_Clicked(object sender, EventArgs e)
        {
            _think = !_think;
            _thinkButton.Text = _think ? "Think: On" : "Think: Off";
        }

        private void ControlButton_Clicked(object sender, EventArgs e)
        {
            GHLocalAiControlMode mode;
            switch (_client.ControlMode)
            {
                case GHLocalAiControlMode.Off:
                    mode = GHLocalAiControlMode.Ask;
                    break;
                case GHLocalAiControlMode.Ask:
                    mode = GHLocalAiControlMode.Auto;
                    break;
                default:
                    mode = GHLocalAiControlMode.Off;
                    break;
            }
            _client.ControlMode = mode;
            _controlButton.Text = "Control: " + mode;
            if (mode == GHLocalAiControlMode.Ask)
                AddNote("Control: Ask. The Overseer may press keys in the game after you click Run.", false);
            else if (mode == GHLocalAiControlMode.Auto)
                AddNote("Control: Auto. The Overseer presses keys in the game on its own; click Stop to stop it.", false);
            else
                AddNote("Control: Off. The Overseer only gives advice.", false);
        }

        private async void UpdateButton_Clicked(object sender, EventArgs e)
        {
            if (_busy)
                return;
            if (GHApp.CurrentGHGame == null)
            {
                AddNote("There is no game in progress.", true);
                return;
            }
            SetBusy(true);
            try
            {
                AiSnapshotResult snapshot = await GHApp.GenerateAiSnapshotTextAsync();
                string text = OverseerPage.TruncateSnapshotForLlm(snapshot != null ? snapshot.Text : null);
                if (string.IsNullOrWhiteSpace(text))
                {
                    AddNote("The game state could not be read.", true);
                    return;
                }
                _client.AddGameState(text);
                AddNote("The game state was updated.", false);
            }
            catch (Exception ex)
            {
                AddNote("The game state could not be read: " + ex.Message, true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ClearButton_Clicked(object sender, EventArgs e)
        {
            if (_busy)
                return;
            _client.Reset();
            _messages.Children.Clear();
            AddNote("The conversation was cleared. Use Update game state to give the Overseer the current game.", false);
        }

        private async void CloseButton_Clicked(object sender, EventArgs e)
        {
            StopButton_Clicked(sender, e);
            try
            {
                if (_inOwnWindow)
                    GHGuiWindows.CloseWindowOf(this);
                else
                    await GHApp.PopModalPageAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}
#endif
