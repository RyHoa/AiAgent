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

        private const int MaxSendAttempts = 3;
        private const float SendCheckMs = 5000;

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
            int before = await page.Locator(selectors.CompletedMessage).CountAsync();

            // Click Send rather than pressing Enter: Enter is ignored if the page hasn't finished loading its scripts.
            // The page can also ignore a click while it is still settling after the previous reply, so check that the
            // prompt box emptied (i.e. the message really went out) and click again if it didn't.
            await page.FillAsync(selectors.PromptBox, text);
            for (int attempt = 1; ; attempt++)
            {
                await page.ClickAsync(selectors.SendButton, new() { Timeout = TimeoutMs });
                try
                {
                    await page.WaitForFunctionAsync(
                        "selector => { const box = document.querySelector(selector); return !box || (box.value ?? box.innerText ?? '').trim() === ''; }",
                        selectors.PromptBox,
                        new() { Timeout = SendCheckMs });
                    break;
                }
                catch (TimeoutException) when (attempt < MaxSendAttempts)
                {
                    Console.WriteLine($"[browser] Message was not sent, clicking Send again ({attempt + 1}/{MaxSendAttempts})...");
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"The message was not sent after {MaxSendAttempts} clicks on the Send button.");
                }
            }

            // Wait until one more reply has been marked complete.
            await page.WaitForFunctionAsync(
                "([selector, count]) => document.querySelectorAll(selector).length > count",
                new object[] { selectors.CompletedMessage, before },
                new() { Timeout = TimeoutMs });

            return await ReadLastReplyAsync();
        }

        /// <summary>
        /// Reads the last reply on the page once its text has stopped changing. Also called again when a reply
        /// looked incomplete (e.g. only "ChatGPT said:" before its JSON block had rendered).
        /// </summary>
        public async Task<string> ReadLastReplyAsync()
        {
            var page = _page ?? throw new InvalidOperationException("Call StartAsync before ReadLastReplyAsync.");
            var messages = page.Locator(_settings.Selectors.AssistantMessage);

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
