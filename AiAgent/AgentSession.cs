using System.Text.Json;
using AiAgent.Tools;

namespace AiAgent
{
    /// <summary>
    /// One conversation with the AI: sends the persona, runs tasks (AI reply → tool → result → next reply),
    /// and handles replies without valid JSON (re-read the page, then ask the AI again).
    /// Everything is recorded in the chat's SessionLog, so a previous chat can be reopened with its full context.
    /// </summary>
    internal class AgentSession
    {
        private const int MaxJsonRetries = 3;
        private const int RereadDelayMs = 2000;
        // Markers for traffic that must never be replayed when a chat is reopened (see SessionHistory.BuildReplay).
        private const string PersonaMarker = "persona";
        private const string ReplayMarker = "replay";

        private readonly AiChatBrowser _browser;
        private readonly AgentSettings _settings;
        private readonly SessionLog _log;

        public AgentSession(AiChatBrowser browser, AgentSettings settings, SessionLog log)
        {
            _browser = browser;
            _settings = settings;
            _log = log;
        }

        /// <summary>Sends the persona once at startup; every task after that continues the same conversation.</summary>
        public async Task SendPersonaAsync(string persona)
        {
            Console.WriteLine("Sending persona prompt...");
            var ack = await SendAndParseAsync(persona, PersonaMarker);
            Console.WriteLine(ack is null
                ? "[warning] The AI did not acknowledge the persona with valid JSON; continuing anyway."
                : $"[ready] {ack.Message}");
        }

        /// <summary>
        /// Rebuilds the AI's context in this new chat by replaying a previous conversation, in parts small enough for
        /// the chat box. Returns the task that was unfinished when that conversation stopped, if any.
        /// </summary>
        public async Task<string?> ResumeAsync(List<JsonElement> previousEvents)
        {
            ReplayResult replay = SessionHistory.BuildReplay(previousEvents, _settings);
            if (replay.Parts.Count == 0)
            {
                Console.WriteLine("[resume] That chat had no tasks yet, so there is nothing to replay.");
                return null;
            }

            for (int i = 0; i < replay.Parts.Count; i++)
            {
                Console.WriteLine($"[resume] Sending the previous conversation, part {i + 1} of {replay.Parts.Count}...");
                var ack = await SendAndParseAsync(AgentPrompts.ReplayPartMessage(i + 1, replay.Parts.Count, replay.Parts[i]), ReplayMarker);
                if (ack is null)
                    Console.WriteLine("[warning] The AI did not acknowledge this part with valid JSON; continuing anyway.");
            }

            var done = await SendAndParseAsync(AgentPrompts.ReplayDoneMessage(replay.UnfinishedTask), ReplayMarker);
            Console.WriteLine(done is null
                ? "[warning] The AI did not confirm the replay with valid JSON; continuing anyway."
                : "[resume] The AI has the previous conversation.");
            return replay.UnfinishedTask;
        }

        /// <param name="continuing">True when continuing a task that was unfinished in a previous run of this chat.</param>
        public async Task RunTaskAsync(string task, bool continuing = false)
        {
            _log.TaskStarted(task, continuing);
            string message = continuing ? AgentPrompts.ContinueTaskMessage(task) : AgentPrompts.TaskMessage(task);

            for (int i = 1; i <= _settings.MaxIterations; i++)
            {
                var response = await SendAndParseAsync(message, null);
                if (response is null)
                {
                    Console.WriteLine($"[stopped] No valid JSON after {MaxJsonRetries} retries.");
                    _log.TaskEnded("stopped", $"No valid JSON after {MaxJsonRetries} retries.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(response.Thought))
                    Console.WriteLine($"[thought] {response.Thought}");

                switch (response.Action.ToLowerInvariant())
                {
                    case "tool":
                        Console.WriteLine($"[tool] {response.Tool} {response.Arguments}");
                        _log.ToolCall(response.Tool, response.Thought, response.Arguments);
                        string result = ToolDispatcher.Execute(response.Tool, response.Arguments, _settings);
                        _log.ToolResult(result);
                        Console.WriteLine($"[result] {Preview(result)}");
                        message = AgentPrompts.ToolResultMessage(result, task);
                        break;
                    case "complete":
                        Console.WriteLine($"\n[complete] {response.Message}");
                        _log.TaskEnded("complete", response.Message);
                        return;
                    case "error":
                        Console.WriteLine($"\n[error] {response.Message}");
                        _log.TaskEnded("error", response.Message);
                        return;
                    default:
                        message = AgentPrompts.UnknownActionMessage(response.Action);
                        break;
                }
            }

            Console.WriteLine($"[stopped] Reached MaxIterations ({_settings.MaxIterations}).");
            _log.TaskEnded("stopped", $"Reached MaxIterations ({_settings.MaxIterations}).");
        }

        /// <param name="marker">null for task traffic; "persona" or "replay" for messages that must never be replayed.</param>
        private async Task<AgentResponse?> SendAndParseAsync(string message, string? marker)
        {
            _log.Sent(message, marker);
            string reply = await _browser.SendAsync(message);
            for (int attempt = 0; ; attempt++)
            {
                _log.Replied(reply, marker);
                if (_settings.LogRawReplies)
                    Console.WriteLine($"[raw reply]\n{reply}\n[/raw reply]");
                if (AgentResponse.TryParse(reply, out var response, out string? parseError))
                    return response;

                // The page may have been read before the reply finished rendering (e.g. only "ChatGPT said:" without
                // its JSON block yet), so read it once more before spending a retry on the AI.
                Console.WriteLine($"[browser] Reply had no valid JSON ({parseError}), reading the page again...");
                await Task.Delay(RereadDelayMs);
                reply = await _browser.ReadLastReplyAsync();
                _log.Replied(reply, marker);
                if (_settings.LogRawReplies)
                    Console.WriteLine($"[raw reply (read again)]\n{reply}\n[/raw reply]");
                if (AgentResponse.TryParse(reply, out response, out parseError))
                    return response;

                if (attempt == MaxJsonRetries)
                    return null;

                Console.WriteLine($"[retry] Reply had no valid JSON ({parseError}), asking again.");
                string retry = AgentPrompts.ParseRetryMessage(parseError);
                _log.Sent(retry, marker);
                reply = await _browser.SendAsync(retry);
            }
        }

        private static string Preview(string text) =>
            text.Length <= 200 ? text : text[..200] + "...";
    }
}
