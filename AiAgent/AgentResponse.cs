using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AiAgent
{
    /// <summary>The JSON envelope the AI must end every reply with.</summary>
    internal sealed record AgentResponse(string? Thought, string Action, string? Tool, JsonElement Arguments, string? Message)
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        public static bool TryParse(string reply, [NotNullWhen(true)] out AgentResponse? result)
        {
            foreach (string candidate in Candidates(reply))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<AgentResponse>(candidate, Options);
                    if (!string.IsNullOrWhiteSpace(parsed?.Action))
                    {
                        result = parsed;
                        return true;
                    }
                }
                catch (JsonException) { }
            }

            result = null;
            return false;
        }

        private static IEnumerable<string> Candidates(string reply)
        {
            // 1. A raw ```json fence (present if the page returns markdown).
            int fence = reply.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (fence >= 0)
            {
                int start = fence + "```json".Length;
                int end = reply.IndexOf("```", start, StringComparison.Ordinal);
                if (end > start)
                    yield return reply[start..end];
            }

            // 2. Rendered code blocks lose their fences (and gain "json" / "Copy code" labels),
            //    so try each '{' up to the last '}' until one parses.
            int close = reply.LastIndexOf('}');
            for (int open = reply.IndexOf('{'); open >= 0 && open < close; open = reply.IndexOf('{', open + 1))
                yield return reply[open..(close + 1)];
        }
    }
}
