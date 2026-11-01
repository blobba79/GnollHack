using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#if GNH_MAUI
using GnollHackM;
#else
using GnollHackX.Pages.Game;
#endif

namespace GnollHackX
{
    /* Gnoll Overseer on the player's own AI: talks to an OpenAI-compatible chat completions server (such as llama.cpp's
       llama-server) with streaming, optional thinking and tool calls. The tools are the official Overseer's client tools
       (OverseerPage.DispatchToolCallAsync). The conversation is kept in memory only. Events are raised on a background
       thread. */
    public sealed class GHLocalOverseerClient
    {
        private const int MaxToolRounds = 6;
        /* While the AI may press keys, a task such as testing every wand takes many rounds */
        private const int MaxToolRoundsWithControl = GHLocalOverseerControl.MaxCallsPerQuestion + 10;
        private const int MaxToolResultChars = 30000;
        private const int MaxTokensWithoutThinking = 2048;
        private const int MaxTokensWithThinking = 8192;

        private static readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private const string SystemPrompt =
            "You are Gnoll Overseer, a friendly and knowledgeable advisor for the roguelike game GnollHack, a variant of NetHack " +
            "with many changes and additions. You help the player understand their current situation and decide what to do. " +
            "Always answer in the same language as the player writes in, but keep everything that comes from the game in English " +
            "exactly as the game shows it (item, monster, spell, skill and feature names, commands and in-game messages), so that the " +
            "player recognises it on the screen. Be concrete and concise, and base your advice on the " +
            "game state you are given. When you need more information, use the tools: refresh_snapshot gets the current game " +
            "state, get_full_message_history gets the recent in-game messages, and search_gnollhack_code and read_gnollhack_file " +
            "let you verify rules in the GnollHack source code. If you are not sure about a GnollHack rule, say so instead of guessing. " +
            "When the player asks you to do something in the game and has set Control to Ask or Auto, use press_keys in small steps: " +
            "read what the game shows after each call, stop and ask the player when something unexpected or dangerous happens, and " +
            "say what you are about to do.";

        private readonly List<JObject> _messages = new List<JObject>();
        private readonly string _systemPrompt;

        /* The player's instructions file (GHApp.LocalAiInstructionsPath), or the built-in prompt */
        private static string LoadSystemPrompt()
        {
            string path = GHApp.LocalAiInstructionsPath;
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim()))
                {
                    string text = File.ReadAllText(path.Trim());
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
            catch (Exception)
            {
            }
            return SystemPrompt;
        }

        /* True if the system prompt comes from the player's instructions file */
        public bool UsesInstructionsFile { get { return !ReferenceEquals(_systemPrompt, SystemPrompt); } }
        private readonly object _messagesLock = new object();

        public string BaseAddress { get; }
        public string ApiKey { get; }
        public string Model { get; }

        /* Text of the answer as it streams in */
        public event Action<string> ContentDelta;
        /* The model's thinking as it streams in */
        public event Action<string> ReasoningDelta;
        /* A tool the model called, by name */
        public event Action<string> ToolCalled;
        /* What press_keys did in the game, as a line for the chat */
        public event Action<string> ControlStep;

        private int _controlMode = (int)GHLocalAiControlMode.Off;
        /* Whether the model may press keys in the game; the press_keys tool is always offered, so that changing this
           keeps the server's prompt cache valid, and refuses while Off */
        public GHLocalAiControlMode ControlMode
        {
            get { return (GHLocalAiControlMode)Interlocked.CompareExchange(ref _controlMode, 0, 0); }
            set { Interlocked.Exchange(ref _controlMode, (int)value); }
        }

        /* Asks the player whether to press the keys (keys, reason) in Ask mode */
        public Func<string, string, Task<bool>> ConfirmKeysAsync { get; set; }

        public GHLocalOverseerClient(string baseAddress, string apiKey, string model)
        {
            BaseAddress = (baseAddress ?? "").Trim().TrimEnd('/');
            ApiKey = apiKey ?? "";
            Model = string.IsNullOrWhiteSpace(model) ? "local" : model.Trim();
            _systemPrompt = LoadSystemPrompt();
            Reset();
        }

        private string ChatCompletionsUrl { get { return BaseAddress + "/v1/chat/completions"; } }
        private string ModelsUrl { get { return BaseAddress + "/v1/models"; } }

        public void Reset()
        {
            lock (_messagesLock)
            {
                _messages.Clear();
                _messages.Add(new JObject { ["role"] = "system", ["content"] = _systemPrompt });
            }
        }

        /* Adds the game state as context; earlier states stay so that the server's prompt cache stays valid */
        public void AddGameState(string snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot))
                return;
            lock (_messagesLock)
            {
                _messages.Add(new JObject { ["role"] = "user", ["content"] = "Current game state (sent automatically by the game client):\n\n" + snapshot });
                _messages.Add(new JObject { ["role"] = "assistant", ["content"] = "Understood. I have read the current game state." });
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            HttpRequestMessage request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrEmpty(ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            return request;
        }

        /* Returns null if the server answers, else a description of the problem */
        public async Task<string> CheckServerAsync(CancellationToken cancellationToken)
        {
            Uri uri;
            if (!Uri.TryCreate(ModelsUrl, UriKind.Absolute, out uri))
                return "The local AI address is not valid: " + BaseAddress;
            try
            {
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using (HttpRequestMessage request = CreateRequest(HttpMethod.Get, ModelsUrl))
                    using (HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                            return "The local AI refused the key. Check Local AI Key in Settings.";
                        if (!response.IsSuccessStatusCode)
                            return "The local AI answered with an error: " + (int)response.StatusCode + " " + response.ReasonPhrase;
                        return null;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return "The local AI at " + BaseAddress + " does not answer.";
            }
            catch (HttpRequestException)
            {
                return "The local AI at " + BaseAddress + " does not answer.";
            }
        }

        /* Lets the server read the conversation so far into its prompt cache, so that the first question is answered quickly */
        public async Task WarmUpAsync(CancellationToken cancellationToken)
        {
            JObject body = new JObject
            {
                ["model"] = Model,
                ["stream"] = false,
                ["cache_prompt"] = true,
                ["max_tokens"] = 1,
                ["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false },
                ["tools"] = CreateTools(),
            };
            lock (_messagesLock)
            {
                body["messages"] = new JArray(_messages.ConvertAll(m => (JToken)m.DeepClone()));
            }
            using (HttpRequestMessage request = CreateRequest(HttpMethod.Post, ChatCompletionsUrl))
            {
                request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    /* Only the cache matters; the answer is not used */
                }
            }
        }

        private static JArray CreateTools()
        {
            JArray tools = new JArray();
            tools.Add(CreateTool("search_gnollhack_code", "Searches the GnollHack source code (src, include and dat folders) for text, such as a function, " +
                "constant or in-game message. Returns file:line: text for each match. Use it to verify how a mechanic works in GnollHack.",
                new JObject
                {
                    ["query"] = new JObject { ["type"] = "string", ["description"] = "The text to search for (case-insensitive)." },
                    ["path_filter"] = new JObject { ["type"] = "string", ["description"] = "Only files whose path contains this, for example src/zap.c." },
                    ["max_results"] = new JObject { ["type"] = "integer", ["description"] = "The maximum number of matches (default 40, at most 60)." },
                }, "query"));
            tools.Add(CreateTool("read_gnollhack_file", "Reads lines of a GnollHack source file, for example around a match from search_gnollhack_code.",
                new JObject
                {
                    ["path"] = new JObject { ["type"] = "string", ["description"] = "The file, relative to the source folder, for example src/zap.c." },
                    ["start_line"] = new JObject { ["type"] = "integer", ["description"] = "The first line (default 1)." },
                    ["end_line"] = new JObject { ["type"] = "integer", ["description"] = "The last line (at most 400 lines are returned)." },
                }, "path"));
            tools.Add(CreateTool("lookup_encyclopedia", "Looks up the game's own encyclopedia entry for a monster, item or feature, as the in-game look-up shows it. " +
                "The entries are mostly descriptions and quotes, not rules.",
                new JObject { ["term"] = new JObject { ["type"] = "string", ["description"] = "The name to look up, in English, for example \"gnoll\" or \"wand of probing\"." } }, "term"));
            tools.Add(CreateTool(GHLocalOverseerControl.ToolName, "Presses keys in the game for the player, as if typed on the keyboard, and returns the new " +
                "messages and what the game shows now (a menu with its letters, a question, a text input or the command prompt). Works only " +
                "when the player has set Control to Ask or Auto. Use the game's own commands and shortcuts, press a few keys at a time, " +
                "and read the result before the next call. Characters are pressed as they are; write {Esc}, {Enter}, {Space}, " +
                "{M-x} for Alt+x, {C-x} for Ctrl+x, and {N} {NE} {E} {SE} {S} {SW} {W} {NW} for the directions (to move, or to answer " +
                "a direction question). To do a command several times, press its key that many times. In a text input, characters " +
                "are typed into it. Digits as a command, saving, quitting, praying and # extended commands are refused.",
                new JObject
                {
                    ["keys"] = new JObject { ["type"] = "string", ["description"] = "The keys, for example \"s\", \"{E}\", \"_>.\" or \"E\"." },
                    ["reason"] = new JObject { ["type"] = "string", ["description"] = "What the keys do, in the player's language, shown to the player." },
                }, "keys"));
            tools.Add(CreateTool("refresh_snapshot", "Gets the current state of the game: the character, attributes, status, inventory, " +
                "surroundings and recent messages. Use it when the game may have changed since the last state you saw.", new JObject()));
            tools.Add(CreateTool("get_full_message_history", "Gets the in-game message history of the current game, newest last.",
                new JObject
                {
                    ["search_term"] = new JObject { ["type"] = "string", ["description"] = "Only messages containing this text." },
                    ["last_n"] = new JObject { ["type"] = "integer", ["description"] = "The number of latest messages to return (default 250)." },
                }));
            tools.Add(CreateTool("get_player_library", "Lists the manuals and catalogues the player has found, or with item_id returns the full text of one.",
                new JObject { ["item_id"] = new JObject { ["type"] = "integer", ["description"] = "The id of the manual to read." } }));
            tools.Add(CreateTool("get_oracle_consultations", "Lists the player's consultations with the Oracle, or with item_id returns one in full.",
                new JObject { ["item_id"] = new JObject { ["type"] = "integer", ["description"] = "The id of the consultation to read." } }));
            tools.Add(CreateTool("get_player_xlog", "Gets the player's finished games (deaths, ascensions and scores), newest first.",
                new JObject
                {
                    ["limit"] = new JObject { ["type"] = "integer", ["description"] = "The number of games to return." },
                    ["offset"] = new JObject { ["type"] = "integer", ["description"] = "The number of newest games to skip." },
                }));
            tools.Add(CreateTool("get_player_dumplogs", "Lists the dump logs of the player's finished games, or with filename returns one.",
                new JObject
                {
                    ["filename"] = new JObject { ["type"] = "string", ["description"] = "The dump log to read." },
                    ["max_length"] = new JObject { ["type"] = "integer", ["description"] = "The maximum number of characters to return." },
                }));
            return tools;
        }

        private static JObject CreateTool(string name, string description, JObject properties, string required = null)
        {
            JObject tool = new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = new JObject { ["type"] = "object", ["properties"] = properties },
                },
            };
            if (required != null)
                tool["function"]["parameters"]["required"] = new JArray(required);
            return tool;
        }

        /* Sends the player's message and streams the answer, running the tools the model asks for */
        public async Task SendAsync(string userText, bool think, CancellationToken cancellationToken)
        {
            lock (_messagesLock)
            {
                _messages.Add(new JObject { ["role"] = "user", ["content"] = userText ?? "" });
            }

            GHLocalAiControlMode controlMode = ControlMode;
            int maxRounds = controlMode != GHLocalAiControlMode.Off ? MaxToolRoundsWithControl : MaxToolRounds;
            int pressCalls = 0;
            for (int round = 0; round <= maxRounds; round++)
            {
                StreamResult result = await StreamCompletionAsync(think, round < maxRounds, cancellationToken).ConfigureAwait(false);
                JObject assistant = new JObject { ["role"] = "assistant", ["content"] = result.Content };
                if (result.ToolCalls.Count > 0)
                    assistant["tool_calls"] = result.ToolCalls;
                lock (_messagesLock)
                {
                    _messages.Add(assistant);
                }
                if (result.ToolCalls.Count == 0)
                    return;

                foreach (JToken call in result.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string id = call["id"]?.ToString() ?? "";
                    string name = call["function"]?["name"]?.ToString() ?? "";
                    JObject arguments = null;
                    try
                    {
                        string argText = call["function"]?["arguments"]?.ToString();
                        arguments = string.IsNullOrWhiteSpace(argText) ? new JObject() : JObject.Parse(argText);
                    }
                    catch (JsonException)
                    {
                        arguments = new JObject();
                    }

                    ToolCalled?.Invoke(name);
                    string toolResult;
                    try
                    {
                        if (name == GHLocalOverseerControl.ToolName)
                        {
                            if (++pressCalls > GHLocalOverseerControl.MaxCallsPerQuestion)
                                toolResult = "Nothing was pressed: the limit of " + GHLocalOverseerControl.MaxCallsPerQuestion + " press_keys calls " +
                                    "per question was reached. Stop, and tell the player what was done and what is left.";
                            else
                                toolResult = await GHLocalOverseerControl.ExecuteAsync(arguments, ControlMode, ConfirmKeysAsync, ControlStep, cancellationToken).ConfigureAwait(false);
                        }
                        else if (GHLocalOverseerTools.IsLocalTool(name))
                            toolResult = GHLocalOverseerTools.Execute(name, arguments);
                        else
                            toolResult = await OverseerPage.DispatchToolCallAsync(name, arguments).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        toolResult = "Error: " + ex.Message;
                    }
                    if (toolResult == null)
                        toolResult = "";
                    if (toolResult.Length > MaxToolResultChars)
                        toolResult = toolResult.Substring(0, MaxToolResultChars) + "\n[truncated]";

                    lock (_messagesLock)
                    {
                        _messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = toolResult });
                    }
                }
            }
        }

        private sealed class StreamResult
        {
            public string Content = "";
            public JArray ToolCalls = new JArray();
        }

        private async Task<StreamResult> StreamCompletionAsync(bool think, bool allowTools, CancellationToken cancellationToken)
        {
            JObject body = new JObject
            {
                ["model"] = Model,
                ["stream"] = true,
                ["cache_prompt"] = true,
                ["max_tokens"] = think ? MaxTokensWithThinking : MaxTokensWithoutThinking,
                ["chat_template_kwargs"] = new JObject { ["enable_thinking"] = think },
            };
            lock (_messagesLock)
            {
                body["messages"] = new JArray(_messages.ConvertAll(m => (JToken)m.DeepClone()));
            }
            if (allowTools)
                body["tools"] = CreateTools();

            StreamResult result = new StreamResult();
            StringBuilder content = new StringBuilder();
            /* Tool calls stream in pieces, by index */
            SortedDictionary<int, JObject> calls = new SortedDictionary<int, JObject>();

            using (HttpRequestMessage request = CreateRequest(HttpMethod.Post, ChatCompletionsUrl))
            {
                request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new HttpRequestException("The local AI answered with an error: " + (int)response.StatusCode + " " + error);
                    }
                    using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    using (cancellationToken.Register(() => { try { stream.Dispose(); } catch { } }))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!line.StartsWith("data:"))
                                continue;
                            string data = line.Substring(5).Trim();
                            if (data == "[DONE]")
                                break;
                            JObject chunk;
                            try
                            {
                                chunk = JObject.Parse(data);
                            }
                            catch (JsonException)
                            {
                                continue;
                            }
                            JToken delta = chunk["choices"]?[0]?["delta"];
                            if (delta == null)
                                continue;

                            string reasoning = delta["reasoning_content"]?.Type == JTokenType.String ? delta["reasoning_content"].ToString() : null;
                            if (!string.IsNullOrEmpty(reasoning))
                                ReasoningDelta?.Invoke(reasoning);

                            string text = delta["content"]?.Type == JTokenType.String ? delta["content"].ToString() : null;
                            if (!string.IsNullOrEmpty(text))
                            {
                                content.Append(text);
                                ContentDelta?.Invoke(text);
                            }

                            JArray toolCalls = delta["tool_calls"] as JArray;
                            if (toolCalls != null)
                            {
                                foreach (JToken piece in toolCalls)
                                {
                                    int index = piece["index"]?.Value<int>() ?? 0;
                                    JObject call;
                                    if (!calls.TryGetValue(index, out call))
                                    {
                                        call = new JObject
                                        {
                                            ["id"] = "",
                                            ["type"] = "function",
                                            ["function"] = new JObject { ["name"] = "", ["arguments"] = "" },
                                        };
                                        calls[index] = call;
                                    }
                                    string id = piece["id"]?.ToString();
                                    if (!string.IsNullOrEmpty(id))
                                        call["id"] = id;
                                    string name = piece["function"]?["name"]?.ToString();
                                    if (!string.IsNullOrEmpty(name))
                                        call["function"]["name"] = call["function"]["name"].ToString() + name;
                                    string args = piece["function"]?["arguments"]?.ToString();
                                    if (!string.IsNullOrEmpty(args))
                                        call["function"]["arguments"] = call["function"]["arguments"].ToString() + args;
                                }
                            }
                        }
                    }
                }
            }

            result.Content = content.ToString();
            foreach (JObject call in calls.Values)
            {
                if (string.IsNullOrEmpty(call["id"].ToString()))
                    call["id"] = "call_" + Guid.NewGuid().ToString("N");
                result.ToolCalls.Add(call);
            }
            return result;
        }
    }
}
