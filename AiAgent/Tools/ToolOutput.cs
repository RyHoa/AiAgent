namespace AiAgent.Tools
{
    /// <summary>What every tool returns: whether it succeeded, and the text to send back to the AI.</summary>
    internal class ToolOutput
    {
        public bool Ok { get; }
        public string Output { get; }

        public ToolOutput(bool ok, string output)
        {
            Ok = ok;
            Output = output;
        }

        public static ToolOutput Success(string output)
        {
            return new ToolOutput(true, output);
        }

        public static ToolOutput Failure(string output)
        {
            return new ToolOutput(false, output);
        }
    }
}
