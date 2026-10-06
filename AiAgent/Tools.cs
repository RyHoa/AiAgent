using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiAgent
{
    /// <summary>The tools the AI can call. Every tool is restricted to the workspace folder.</summary>
    internal static class Tools
    {
        private const int MaxSearchResults = 50;
        private const long MaxSearchFileBytes = 1_000_000;
        private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules" };

        /// <summary>Runs a tool and returns the tool_result JSON to send back to the AI.</summary>
        public static string Execute(string? tool, JsonElement args, AgentSettings settings)
        {
            try
            {
                string output = tool switch
                {
                    "list" => List(settings.WorkspacePath, GetString(args, "path") ?? "."),
                    "read_file" => ReadFile(settings.WorkspacePath, RequireString(args, "path"), GetInt(args, "offset"), settings.MaxReadChars),
                    "search_files" => SearchFiles(settings.WorkspacePath, RequireString(args, "query")),
                    "ask_user" => AskUser(RequireString(args, "question")),
                    _ => throw new ArgumentException($"Unknown tool '{tool}'. Available tools: list, read_file, search_files, ask_user."),
                };
                return Result(tool, true, output);
            }
            catch (Exception ex)
            {
                return Result(tool, false, ex.Message);
            }
        }

        // Relaxed escaping keeps quotes and < > readable for the AI instead of " etc.
        private static readonly JsonSerializerOptions ResultOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static string Result(string? tool, bool ok, string output) =>
            JsonSerializer.Serialize(new { tool_result = new { tool, ok, output } }, ResultOptions);

        private static string List(string workspace, string path)
        {
            string root = WorkspaceRoot(workspace);
            string dir = ResolvePath(workspace, path);
            var entries = Directory.EnumerateDirectories(dir).Select(d => Relative(root, d) + "/")
                .Concat(Directory.EnumerateFiles(dir).Select(f => Relative(root, f)))
                .Order(StringComparer.OrdinalIgnoreCase);
            string result = string.Join('\n', entries);
            return result.Length > 0 ? result : "(empty folder)";
        }

        private static string ReadFile(string workspace, string path, int offset, int maxChars)
        {
            string text = File.ReadAllText(ResolvePath(workspace, path));
            offset = Math.Clamp(offset, 0, text.Length);
            int end = Math.Min(text.Length, offset + maxChars);
            string chunk = text[offset..end];
            if (end < text.Length)
                chunk += $"\n[truncated: showing chars {offset}-{end} of {text.Length}; call read_file with offset {end} for more]";
            return chunk;
        }

        private static string SearchFiles(string workspace, string query)
        {
            string root = WorkspaceRoot(workspace);
            var results = new StringBuilder();
            int count = 0;

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Relative(root, file);
                if (relative.Split('/').Any(SkippedFolders.Contains) || new FileInfo(file).Length > MaxSearchFileBytes)
                    continue;

                int lineNumber = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (line.Contains('\0'))
                        break; // binary file
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        continue;

                    results.AppendLine($"{relative}:{lineNumber}: {line.Trim()}");
                    if (++count >= MaxSearchResults)
                        return results.Append($"[stopped after {MaxSearchResults} matches]").ToString();
                }
            }

            return count > 0 ? results.ToString() : "No matches.";
        }

        private static string AskUser(string question)
        {
            Console.WriteLine($"\n[AI asks] {question}");
            Console.Write("> ");
            return Console.ReadLine() ?? "";
        }

        private static string WorkspaceRoot(string workspace) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar;

        /// <summary>Resolves a workspace-relative path and rejects anything outside the workspace.</summary>
        private static string ResolvePath(string workspace, string path)
        {
            string root = WorkspaceRoot(workspace);
            string full = Path.GetFullPath(Path.Combine(root, path));
            string withSeparator = Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;
            if (!withSeparator.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"'{path}' is outside the workspace.");
            return full;
        }

        private static string Relative(string root, string fullPath) =>
            Path.GetRelativePath(root, fullPath).Replace('\\', '/');

        private static string? GetString(JsonElement args, string name) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string RequireString(JsonElement args, string name) =>
            GetString(args, name) ?? throw new ArgumentException($"Missing string argument '{name}'.");

        private static int GetInt(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;
            return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
        }
    }
}
