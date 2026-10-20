using AiAgent.Tools;

namespace AiAgent
{
    /// <summary>
    /// One conversation with the AI: sends the persona, runs tasks (AI reply → tool → result → next reply),
    /// and handles replies without valid JSON (re-read the page, then ask the AI again).
    /// </summary>
    internal class AgentSession
    {
        private const int MaxJsonRetries = 3;
        private const int RereadDelayMs = 2000;

        private readonly AiChatBrowser _browser;
        private readonly AgentSettings _settings;

        public AgentSession(AiChatBrowser browser, AgentSettings settings)
        {
            _browser = browser;
            _settings = settings;
        }

        /// <summary>Sends the persona once at startup; every task after that continues the same conversation.</summary>
        public async Task SendPersonaAsync(string persona)
        {
            Console.WriteLine("Sending persona prompt...");
            var ack = await SendAndParseAsync(persona);
            Console.WriteLine(ack is null
                ? "[warning] The AI did not acknowledge the persona with valid JSON; continuing anyway."
                : $"[ready] {ack.Message}");
        }

        public async Task RunTaskAsync(string task)
        {
            string message = AgentPrompts.TaskMessage(task);

            for (int i = 1; i <= _settings.MaxIterations; i++)
            {
                var response = await SendAndParseAsync(message);
                if (response is null)
                {
                    Console.WriteLine($"[stopped] No valid JSON after {MaxJsonRetries} retries.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(response.Thought))
                    Console.WriteLine($"[thought] {response.Thought}");

                switch (response.Action.ToLowerInvariant())
                {
                    case "tool":
                        Console.WriteLine($"[tool] {response.Tool} {response.Arguments}");
                        string result = ToolDispatcher.Execute(response.Tool, response.Arguments, _settings);
                        Console.WriteLine($"[result] {Preview(result)}");
                        message = AgentPrompts.ToolResultMessage(result, task);
                        break;
                    case "complete":
                        Console.WriteLine($"\n[complete] {response.Message}");
                        return;
                    case "error":
                        Console.WriteLine($"\n[error] {response.Message}");
                        return;
                    default:
                        message = AgentPrompts.UnknownActionMessage(response.Action);
                        break;
                }
            }

            Console.WriteLine($"[stopped] Reached MaxIterations ({_settings.MaxIterations}).");
        }

        private async Task<AgentResponse?> SendAndParseAsync(string message)
        {
            string reply = await _browser.SendAsync(message);
            for (int attempt = 0; ; attempt++)
            {
                if (_settings.LogRawReplies)
                    Console.WriteLine($"[raw reply]\n{reply}\n[/raw reply]");
                if (AgentResponse.TryParse(reply, out var response, out string? parseError))
                    return response;

                // The page may have been read before the reply finished rendering (e.g. only "ChatGPT said:" without
                // its JSON block yet), so read it once more before spending a retry on the AI.
                Console.WriteLine($"[browser] Reply had no valid JSON ({parseError}), reading the page again...");
                await Task.Delay(RereadDelayMs);
                reply = await _browser.ReadLastReplyAsync();
                if (_settings.LogRawReplies)
                    Console.WriteLine($"[raw reply (read again)]\n{reply}\n[/raw reply]");
                if (AgentResponse.TryParse(reply, out response, out parseError))
                    return response;

                if (attempt == MaxJsonRetries)
                    return null;

                Console.WriteLine($"[retry] Reply had no valid JSON ({parseError}), asking again.");
                reply = await _browser.SendAsync(AgentPrompts.ParseRetryMessage(parseError));
            }
        }

        private static string Preview(string text) =>
            text.Length <= 200 ? text : text[..200] + "...";
    }
}
