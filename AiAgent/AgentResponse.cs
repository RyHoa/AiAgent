using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace AiAgent
{
    /// <summary>The JSON envelope the AI must end every reply with.</summary>
    internal sealed record AgentResponse(string? Thought, string Action, string? Tool, JsonElement Arguments, string? Message)
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Finds the JSON object in the reply. When none parses, <paramref name="error"/> says why, so the AI can be
        /// told exactly what to fix (e.g. an unescaped line break inside a string).
        /// </summary>
        public static bool TryParse(string reply, [NotNullWhen(true)] out AgentResponse? result, out string? error)
        {
            string? firstError = null;
            string? objectError = null; // error from a candidate that starts like a JSON object: {"...
            foreach (string candidate in Candidates(reply))
            {
                string? candidateError;
                try
                {
                    var parsed = DeserializeFirstValue(candidate);
                    if (!string.IsNullOrWhiteSpace(parsed?.Action))
                    {
                        result = parsed;
                        error = null;
                        return true;
                    }
                    candidateError = "the JSON object has no \"action\" field";
                }
                catch (JsonException ex)
                {
                    candidateError = ex.Message;
                }

                firstError ??= candidateError;
                if (objectError is null && LooksLikeObject(candidate))
                    objectError = candidateError;
            }

            result = null;
            error = objectError ?? firstError ?? "no JSON object was found in the reply";
            return false;
        }

        /// <summary>
        /// Reads the first complete JSON value and ignores anything after it. The AI sometimes closes the object one
        /// brace too early and then adds more, e.g. ...}},"message":""} after a long file content. The first value is
        /// still a complete, valid request, so it is used instead of rejecting the whole reply.
        /// </summary>
        private static AgentResponse? DeserializeFirstValue(string candidate)
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(candidate));
            return JsonSerializer.Deserialize<AgentResponse>(ref reader, Options);
        }

        private static bool LooksLikeObject(string candidate)
        {
            string trimmed = candidate.TrimStart();
            if (trimmed.StartsWith('{'))
                trimmed = trimmed[1..].TrimStart();
            return trimmed.StartsWith('"');
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
