namespace AiAgent
{
    /// <summary>
    /// The startup menu: open any previous chat for this workspace, start a new chat, or delete a chat.
    /// </summary>
    internal static class ChatMenu
    {
        /// <summary>Shows the menu until the user opens a chat or starts a new one. Returns null for a new chat.</summary>
        public static SessionSummary? Choose(string sessionsFolder)
        {
            while (true)
            {
                List<SessionSummary> chats = SessionHistory.FindSessions(sessionsFolder);
                if (chats.Count == 0)
                    return null;

                Console.WriteLine("\nChats for this workspace:");
                for (int i = 0; i < chats.Count; i++)
                    Console.WriteLine($"  {i + 1}) {Describe(chats[i])}");
                Console.Write("\nEnter a number to open that chat, N (or Enter) for a new chat, or D and a number to delete a chat (e.g. D2): ");

                string input = (Console.ReadLine() ?? "").Trim();
                if (input.Length == 0 || input.Equals("N", StringComparison.OrdinalIgnoreCase))
                    return null;

                if (input.StartsWith("D", StringComparison.OrdinalIgnoreCase))
                {
                    SessionSummary? toDelete = Pick(chats, input[1..].Trim());
                    if (toDelete is null)
                        Console.WriteLine($"'{input}' is not a chat in the list. Use D and a number, e.g. D2.");
                    else
                        ConfirmAndDelete(sessionsFolder, toDelete);
                    continue;
                }

                SessionSummary? chosen = Pick(chats, input);
                if (chosen is not null)
                    return chosen;
                Console.WriteLine($"'{input}' is not an option. Enter a number from the list, N, or D and a number.");
            }
        }

        private static string Describe(SessionSummary chat)
        {
            string tasks = chat.TaskCount == 1 ? "1 task" : $"{chat.TaskCount} tasks";
            string status;
            if (chat.UnfinishedTask is not null)
                status = "UNFINISHED";
            else if (chat.EndedNormally)
                status = "ended normally";
            else
                status = "closed without exiting";
            return $"{chat.LastUsed:dd MMM HH:mm}  \"{chat.Title}\"  {tasks}  {status}";
        }

        /// <summary>The chat for a 1-based number typed by the user, or null if the number isn't in the list.</summary>
        private static SessionSummary? Pick(List<SessionSummary> chats, string number)
        {
            if (int.TryParse(number, out int n) && n >= 1 && n <= chats.Count)
                return chats[n - 1];
            return null;
        }

        private static void ConfirmAndDelete(string sessionsFolder, SessionSummary chat)
        {
            Console.Write($"Delete the chat \"{chat.Title}\" permanently? (y/n): ");
            string answer = (Console.ReadLine() ?? "").Trim();
            if (!answer.StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Not deleted.");
                return;
            }

            try
            {
                SessionHistory.DeleteSession(sessionsFolder, chat.FilePath);
                Console.WriteLine("Chat deleted.");
            }
            catch (IOException)
            {
                Console.WriteLine("That chat is open in another running copy of the app, so it can't be deleted right now.");
            }
        }
    }
}
