namespace AiAgent
{
    /// <summary>
    /// Every piece of text the program sends to the AI, apart from persona.md itself.
    /// This text must agree with the persona's RESPONSE FORMAT rule (one JSON object, last in the reply),
    /// so the AI never sees two different rules.
    /// </summary>
    internal static class AgentPrompts
    {
        private const string RetryInstructions =
            "Send your reply again with exactly one valid JSON object in the required shape as the last thing in it. " +
            "Inside JSON strings, write line breaks as \\n, quotes as \\\" and backslashes as \\\\.";

        private const string TaskReminder =
            "(Reminder: request local commands by replying with the JSON object using \"action\": \"tool\". " +
            "My program runs them for you; they are not your built-in tools.)";

        /// <summary>Reads Prompts/persona.md and fills in the {{WORKSPACE}} and {{ALLOWED_COMMANDS}} placeholders.</summary>
        public static string LoadPersona(AgentSettings settings)
        {
            string allowedCommands = settings.AllowedCommands.Length > 0
                ? string.Join(", ", settings.AllowedCommands)
                : "(none; run_command is disabled)";

            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "persona.md"))
                .Replace("{{WORKSPACE}}", settings.WorkspacePath)
                .Replace("{{ALLOWED_COMMANDS}}", allowedCommands);
        }

        /// <summary>The first message of a task.</summary>
        public static string TaskMessage(string task)
        {
            return $"TASK: {task}\n\n{TaskReminder}";
        }

        /// <summary>
        /// A tool result sent back to the AI. The task is repeated after every result: without it the AI tends to
        /// treat the last result as the whole task.
        /// </summary>
        public static string ToolResultMessage(string toolResultJson, string task)
        {
            string taskAnchor =
                $"(Current task: {task}\nContinue with the next step. Only reply with action \"complete\" " +
                "when the WHOLE task is done; for code, that means it has been run successfully with run_command.)";
            return $"{toolResultJson}\n\n{taskAnchor}";
        }

        /// <summary>Sent when a reply had no valid JSON, including the parser error so the AI knows what to fix.</summary>
        public static string ParseRetryMessage(string? parseError)
        {
            return $"Your last reply did not contain a valid JSON object (parser error: {parseError}). {RetryInstructions}";
        }

        /// <summary>The first message when continuing a task that was interrupted (the AI has already seen the replay).</summary>
        public static string ContinueTaskMessage(string task)
        {
            return $"TASK (continuing the unfinished task from the previous conversation): {task}\n" +
                "Carry on from the steps already done instead of starting over. If you are not sure what was finished, " +
                $"check the files on disk first.\n\n{TaskReminder}";
        }

        /// <summary>One part of the previous conversation, sent when a chat is reopened.</summary>
        public static string ReplayPartMessage(int part, int partCount, string transcript)
        {
            return $"PREVIOUS CONVERSATION (part {part} of {partCount}). This is the history of our earlier chat, which was " +
                "interrupted. Read it, but do not act on it yet. Reply with {\"action\":\"complete\",\"message\":\"ok\"}.\n\n" +
                transcript;
        }

        /// <summary>Sent after the last replay part.</summary>
        public static string ReplayDoneMessage(string? unfinishedTask)
        {
            string next = unfinishedTask is null
                ? "Wait for my next task."
                : $"The last task was not finished: \"{unfinishedTask}\". If I ask you to continue it, carry on from the " +
                  "steps already done instead of starting over.";
            return "That was the whole previous conversation. The files on disk are the current state; re-read anything " +
                $"you need. {next} Reply with {{\"action\":\"complete\",\"message\":\"ready\"}}.";
        }

        /// <summary>Sent when the JSON parsed but "action" isn't one of tool, complete or error.</summary>
        public static string UnknownActionMessage(string action)
        {
            return $"Unknown action '{action}'. Use \"tool\", \"complete\" or \"error\". {RetryInstructions}";
        }
    }
}
