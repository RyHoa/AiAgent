using AiAgent.Tools;
using Microsoft.Extensions.Configuration;

namespace AiAgent
{
    internal class Program
    {
        private const int MaxJsonRetries = 3;
        private const int RereadDelayMs = 2000;
        // Matches the persona's RESPONSE FORMAT rule (one JSON object, last in the reply), so the AI never sees two different rules.
        private const string RetryPrompt =
            "Send your reply again with exactly one valid JSON object in the required shape as the last thing in it. " +
            "Inside JSON strings, write line breaks as \\n, quotes as \\\" and backslashes as \\\\.";
        private const string TaskReminder =
            "(Reminder: request local commands by replying with the JSON object using \"action\": \"tool\". " +
            "My program runs them for you; they are not your built-in tools.)";
        private static bool LogRawReplies;

        static async Task Main(string[] args)
        {
            var settings = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(Path.Combine("Config", "appsettings.json"), optional: false)
                // Machine-specific overrides (e.g. your real WorkspacePath). Git-ignored, so they never get committed.
                .AddJsonFile(Path.Combine("Config", "appsettings.local.json"), optional: true)
                .Build()
                .Get<AppSettings>() ?? new AppSettings();
            LogRawReplies = settings.Agent.LogRawReplies;

            if (!Directory.Exists(settings.Agent.WorkspacePath))
            {
                Console.WriteLine($"Workspace folder not found: '{settings.Agent.WorkspacePath}'. Set Agent:WorkspacePath in Config/appsettings.local.json (or Config/appsettings.json).");
                return;
            }

            string persona = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "persona.md"))
                .Replace("{{WORKSPACE}}", settings.Agent.WorkspacePath)
                .Replace("{{ALLOWED_COMMANDS}}", settings.Agent.AllowedCommands.Length > 0
                    ? string.Join(", ", settings.Agent.AllowedCommands)
                    : "(none; run_command is disabled)");

            await using var browser = new AiChatBrowser(settings.Browser);
            Console.WriteLine($"Opening {settings.Browser.AiChatUrl} in Edge...");
            await browser.StartAsync();

            // Send the persona once at startup; every task after that continues the same conversation.
            Console.WriteLine("Sending persona prompt...");
            var ack = await SendAndParseAsync(browser, persona);
            Console.WriteLine(ack is null
                ? "[warning] The AI did not acknowledge the persona with valid JSON; continuing anyway."
                : $"[ready] {ack.Message}");

            while (true)
            {
                Console.Write("\nTask (blank to exit): ");
                string? task = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(task))
                    break;

                await RunTaskAsync(browser, task, settings.Agent);
            }
        }

        private static async Task RunTaskAsync(AiChatBrowser browser, string task, AgentSettings settings)
        {
            string message = $"TASK: {task}\n\n{TaskReminder}";
            // Repeated after every tool result: without it the AI tends to treat the last result as the whole task.
            string taskAnchor =
                $"(Current task: {task}\nContinue with the next step. Only reply with action \"complete\" " +
                "when the WHOLE task is done; for code, that means it has been run successfully with run_command.)";

            for (int i = 1; i <= settings.MaxIterations; i++)
            {
                var response = await SendAndParseAsync(browser, message);
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
                        string result = ToolDispatcher.Execute(response.Tool, response.Arguments, settings);
                        Console.WriteLine($"[result] {Preview(result)}");
                        message = $"{result}\n\n{taskAnchor}";
                        break;
                    case "complete":
                        Console.WriteLine($"\n[complete] {response.Message}");
                        return;
                    case "error":
                        Console.WriteLine($"\n[error] {response.Message}");
                        return;
                    default:
                        message = $"Unknown action '{response.Action}'. Use \"tool\", \"complete\" or \"error\". {RetryPrompt}";
                        break;
                }
            }

            Console.WriteLine($"[stopped] Reached MaxIterations ({settings.MaxIterations}).");
        }

        private static async Task<AgentResponse?> SendAndParseAsync(AiChatBrowser browser, string message)
        {
            string reply = await browser.SendAsync(message);
            for (int attempt = 0; ; attempt++)
            {
                if (LogRawReplies)
                    Console.WriteLine($"[raw reply]\n{reply}\n[/raw reply]");
                if (AgentResponse.TryParse(reply, out var response, out string? parseError))
                    return response;

                // The page may have been read before the reply finished rendering (e.g. only "ChatGPT said:" without
                // its JSON block yet), so read it once more before spending a retry on the AI.
                Console.WriteLine($"[browser] Reply had no valid JSON ({parseError}), reading the page again...");
                await Task.Delay(RereadDelayMs);
                reply = await browser.ReadLastReplyAsync();
                if (LogRawReplies)
                    Console.WriteLine($"[raw reply (read again)]\n{reply}\n[/raw reply]");
                if (AgentResponse.TryParse(reply, out response, out parseError))
                    return response;

                if (attempt == MaxJsonRetries)
                    return null;

                Console.WriteLine($"[retry] Reply had no valid JSON ({parseError}), asking again.");
                reply = await browser.SendAsync($"Your last reply did not contain a valid JSON object (parser error: {parseError}). {RetryPrompt}");
            }
        }

        private static string Preview(string text) =>
            text.Length <= 200 ? text : text[..200] + "...";
    }
}
