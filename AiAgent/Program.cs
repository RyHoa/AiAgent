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

            string persona = AgentPrompts.LoadPersona(settings.Agent);

            await using var browser = new AiChatBrowser(settings.Browser);
            Console.WriteLine($"Opening {settings.Browser.AiChatUrl} in Edge...");
            await browser.StartAsync();

            var session = new AgentSession(browser, settings.Agent);
            await session.SendPersonaAsync(persona);

            while (true)
            {
                Console.Write("\nTask (blank to exit): ");
                string? task = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(task))
                    break;

                await session.RunTaskAsync(task);
            }
        }
    }
}
