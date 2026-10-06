using Microsoft.Extensions.Configuration;

namespace AiAgent
{
    internal class Program
    {
        private const int MaxJsonRetries = 3;
        private const string RetryPrompt = "Reply with only the JSON object in the required format.";
        private const string TaskReminder =
            "(Reminder: request local commands by replying with the JSON object using \"action\": \"tool\". " +
            "My program runs them for you; they are not your built-in tools.)";
        private static bool LogRawReplies;

        static async Task Main(string[] args)
        {
            var settings = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(Path.Combine("Config", "appsettings.json"), optional: false)
                .Build()
                .Get<AppSettings>() ?? new AppSettings();
            LogRawReplies = settings.Agent.LogRawReplies;

            if (!Directory.Exists(settings.Agent.WorkspacePath))
            {
                Console.WriteLine($"Workspace folder not found: '{settings.Agent.WorkspacePath}'. Set Agent:WorkspacePath in Config/appsettings.json.");
                return;
            }

            string persona = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "persona.md"))
                .Replace("{{WORKSPACE}}", settings.Agent.WorkspacePath);

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

                await RunTaskAsync(browser, $"TASK: {task}\n\n{TaskReminder}", settings.Agent);
            }
        }

        private static async Task RunTaskAsync(AiChatBrowser browser, string message, AgentSettings settings)
        {
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
                        message = Tools.Execute(response.Tool, response.Arguments, settings);
                        Console.WriteLine($"[result] {Preview(message)}");
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
                if (AgentResponse.TryParse(reply, out var response))
                    return response;
                if (attempt == MaxJsonRetries)
                    return null;

                Console.WriteLine("[retry] Reply had no valid JSON, asking again.");
                reply = await browser.SendAsync(RetryPrompt);
            }
        }

        private static string Preview(string text) =>
            text.Length <= 200 ? text : text[..200] + "...";
    }
}
