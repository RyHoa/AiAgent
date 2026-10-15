namespace AiAgent.Tools
{
    /// <summary>The ask_user tool: lets the AI ask the human a question in the console.</summary>
    internal static class UserTools
    {
        internal static ToolOutput AskUser(string question)
        {
            Console.WriteLine($"\n[AI asks] {question}");
            Console.Write("> ");
            return ToolOutput.Success(Console.ReadLine() ?? "");
        }
    }
}
