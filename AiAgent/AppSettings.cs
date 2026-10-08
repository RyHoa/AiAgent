namespace AiAgent
{
    internal class AppSettings
    {
        public AgentSettings Agent { get; set; } = new();
        public BrowserSettings Browser { get; set; } = new();
    }

    internal class AgentSettings
    {
        public string WorkspacePath { get; set; } = "";
        public int MaxIterations { get; set; } = 25;
        public int MaxReadChars { get; set; } = 8000;
        public bool LogRawReplies { get; set; }
        public bool RequireApproval { get; set; } = true;
    }

    internal class BrowserSettings
    {
        public string AiChatUrl { get; set; } = "";
        public bool Headless { get; set; }
        public int ResponseTimeoutSeconds { get; set; } = 120;
        public SelectorSettings Selectors { get; set; } = new();
    }

    internal class SelectorSettings
    {
        public string PromptBox { get; set; } = "";
        public string SendButton { get; set; } = "";
        public string AssistantMessage { get; set; } = "";
        public string CompletedMessage { get; set; } = "";
    }
}
