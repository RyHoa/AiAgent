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

        /// <summary>
        /// Reads Prompts/persona.md and fills in the {{WORKSPACE}}, {{ALLOWED_COMMANDS}} and {{EXTRA_INSTRUCTIONS}}
        /// placeholders. Extra instructions come from the files listed in Agent:ExtraPromptFiles (also in Prompts/).
        /// </summary>
        public static string LoadPersona(AgentSettings settings)
        {
            string allowedCommands = settings.AllowedCommands.Length > 0
                ? string.Join(", ", settings.AllowedCommands)
                : "(none; run_command is disabled)";

            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "persona.md"))
                .Replace("{{WORKSPACE}}", settings.WorkspacePath)
                .Replace("{{ALLOWED_COMMANDS}}", allowedCommands)
                .Replace("{{EXTRA_INSTRUCTIONS}}", LoadExtraInstructions(settings.ExtraPromptFiles));
        }

        /// <summary>The contents of each extra prompt file, separated by blank lines; empty when none are configured.</summary>
        private static string LoadExtraInstructions(string[] fileNames)
        {
            if (fileNames.Length == 0)
                return "";

            var sections = new List<string>();
            foreach (string fileName in fileNames)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Prompts", fileName);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Extra prompt file '{fileName}' (from Agent:ExtraPromptFiles) was not found in the Prompts folder.", path);
                sections.Add(File.ReadAllText(path).Trim());
            }
            return "\n" + string.Join("\n\n", sections) + "\n";
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

        /// <summary>Sent when the JSON parsed but "action" isn't one of tool, complete or error.</summary>
        public static string UnknownActionMessage(string action)
        {
            return $"Unknown action '{action}'. Use \"tool\", \"complete\" or \"error\". {RetryInstructions}";
        }
    }
}
