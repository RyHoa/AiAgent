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
        public string[] AllowedCommands { get; set; } = [];
        public int CommandTimeoutSeconds { get; set; } = 120;
        public int MaxCommandOutputChars { get; set; } = 4000;
        /// <summary>Where chats are saved; empty means AiAgent/Sessions in the project folder (git-ignored).</summary>
        public string SessionsPath { get; set; } = "";
        /// <summary>Largest replay message when reopening a chat; the chat box has a size limit.</summary>
        public int ResumeChunkChars { get; set; } = 6000;
        /// <summary>Tool outputs and file contents longer than this are shortened in the replay.</summary>
        public int ResumeMaxResultChars { get; set; } = 1500;
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
