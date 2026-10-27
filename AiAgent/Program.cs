using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AiAgent
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            AppSettings settings = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(Path.Combine("Config", "appsettings.json"), optional: false)
                // Machine-specific overrides (e.g. your real WorkspacePath). Git-ignored, so they never get committed.
                .AddJsonFile(Path.Combine("Config", "appsettings.local.json"), optional: true)
                .Build()
                .Get<AppSettings>() ?? new AppSettings();

            if (!Directory.Exists(settings.Agent.WorkspacePath))
            {
                Console.WriteLine($"Workspace folder not found: '{settings.Agent.WorkspacePath}'. Set Agent:WorkspacePath in Config/appsettings.local.json (or Config/appsettings.json).");
                return;
            }

            // Pick a previous chat (or a new one) before opening the browser.
            string sessionsFolder = SessionHistory.GetSessionsFolder(settings.Agent);
            SessionSummary? previousChat = ChatMenu.Choose(sessionsFolder);
            // Read the previous chat before reopening its file, so the replay doesn't see this run's "resumed" event.
            List<JsonElement>? previousEvents = previousChat is null ? null : SessionHistory.ReadEvents(previousChat.FilePath);

            string persona = AgentPrompts.LoadPersona(settings.Agent);

            await using var browser = new AiChatBrowser(settings.Browser);
            Console.WriteLine($"Opening {settings.Browser.AiChatUrl} in Edge...");
            await browser.StartAsync();

            using SessionLog log = previousChat is null
                ? SessionLog.CreateNew(sessionsFolder, settings.Agent, settings.Browser.AiChatUrl)
                : SessionLog.OpenExisting(previousChat.FilePath);
            Console.WriteLine($"[chat] Saving this chat to {log.FilePath}");

            try
            {
                var session = new AgentSession(browser, settings.Agent, log);
                await session.SendPersonaAsync(persona);

                if (previousEvents is not null)
                {
                    string? unfinishedTask = await session.ResumeAsync(previousEvents);
                    if (unfinishedTask is not null)
                    {
                        Console.Write($"\nContinue the unfinished task \"{unfinishedTask}\"? (y/n): ");
                        string answer = (Console.ReadLine() ?? "").Trim();
                        if (answer.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                            await session.RunTaskAsync(unfinishedTask, continuing: true);
                    }
                }

                while (true)
                {
                    Console.Write("\nTask (blank to exit): ");
                    string? task = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(task))
                        break;

                    await session.RunTaskAsync(task);
                }

                log.SessionEnded();
            }
            catch (Exception ex)
            {
                // Record the crash in the chat file, then let it fail exactly as before.
                log.Crashed($"{ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }
}
