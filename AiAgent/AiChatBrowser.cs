using Microsoft.Playwright;

namespace AiAgent
{
    /// <summary>Drives a web AI chat page in Edge: types a prompt, waits for the reply, returns its text.</summary>
    internal sealed class AiChatBrowser : IAsyncDisposable
    {
        private readonly BrowserSettings _settings;
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private IPage? _page;

        public AiChatBrowser(BrowserSettings settings)
        {
            _settings = settings;
        }

        private float TimeoutMs => _settings.ResponseTimeoutSeconds * 1000f;

        public async Task StartAsync()
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = _settings.Headless });
            _page = await _browser.NewPageAsync();
            await _page.GotoAsync(_settings.AiChatUrl);
            Console.WriteLine($"Waiting for prompt box '{_settings.Selectors.PromptBox}'...");
            await _page.WaitForSelectorAsync(_settings.Selectors.PromptBox, new() { Timeout = TimeoutMs });
        }

        public async Task<string> SendAsync(string text)
        {
            var page = _page ?? throw new InvalidOperationException("Call StartAsync before SendAsync.");
            var selectors = _settings.Selectors;
            var messages = page.Locator(selectors.AssistantMessage);
            int before = await page.Locator(selectors.CompletedMessage).CountAsync();

            // Click Send rather than pressing Enter: Enter is ignored if the page hasn't finished loading its scripts.
            await page.FillAsync(selectors.PromptBox, text);
            await page.ClickAsync(selectors.SendButton, new() { Timeout = TimeoutMs });

            // Wait until one more reply has been marked complete.
            await page.WaitForFunctionAsync(
                "([selector, count]) => document.querySelectorAll(selector).length > count",
                new object[] { selectors.CompletedMessage, before },
                new() { Timeout = TimeoutMs });

            // Return once the last message's text has stopped changing.
            var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            string previous = "";
            while (true)
            {
                string current = await messages.Last.InnerTextAsync();
                if (current.Length > 0 && current == previous)
                    return current;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("The AI reply did not finish within the response timeout.");
                previous = current;
                await Task.Delay(1000);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_browser is not null)
                await _browser.CloseAsync();
            _playwright?.Dispose();
        }
    }
}
