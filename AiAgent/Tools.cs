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
        private const int ApprovalPreviewLines = 20;
        private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules" };
        private static readonly HashSet<string> ChangingTools = new() { "create_file", "write_file", "edit_file", "delete_file", "move_file", "create_folder" };
        private const string ToolNames = "list, read_file, search_files, ask_user, create_file, write_file, edit_file, delete_file, move_file, create_folder";

        /// <summary>Runs a tool and returns the tool_result JSON to send back to the AI.</summary>
        public static string Execute(string? tool, JsonElement args, AgentSettings settings)
        {
            try
            {
                if (tool is not null && ChangingTools.Contains(tool) && settings.RequireApproval && !Approve(tool, args))
                    return Result(tool, false, "The user denied this action. Ask what they want instead, or try a different approach.");

                string ws = settings.WorkspacePath;
                string output = tool switch
                {
                    "list" => List(ws, GetString(args, "path") ?? "."),
                    "read_file" => ReadFile(ws, RequireString(args, "path"), GetInt(args, "offset"), settings.MaxReadChars),
                    "search_files" => SearchFiles(ws, RequireString(args, "query")),
                    "ask_user" => AskUser(RequireString(args, "question")),
                    "create_file" => CreateFile(ws, RequireString(args, "path"), RequireString(args, "content")),
                    "write_file" => WriteFile(ws, RequireString(args, "path"), RequireString(args, "content")),
                    "edit_file" => EditFile(ws, RequireString(args, "path"), RequireString(args, "old_text"), RequireString(args, "new_text")),
                    "delete_file" => DeleteFile(ws, RequireString(args, "path")),
                    "move_file" => MoveFile(ws, RequireString(args, "path"), RequireString(args, "new_path")),
                    "create_folder" => CreateFolder(ws, RequireString(args, "path")),
                    _ => throw new ArgumentException($"Unknown tool '{tool}'. Available tools: {ToolNames}."),
                };
                return Result(tool, true, output);
            }
            catch (Exception ex)
            {
                return Result(tool, false, ex.Message);
            }
        }

        // Relaxed escaping keeps quotes and < > readable for the AI instead of unicode escapes.
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

        private static string CreateFile(string workspace, string path, string content)
        {
            string full = ResolvePath(workspace, path);
            if (File.Exists(full))
                throw new IOException($"'{path}' already exists. Use write_file to replace it or edit_file to change part of it.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return $"Created {path} ({content.Length} chars).";
        }

        private static string WriteFile(string workspace, string path, string content)
        {
            string full = ResolvePath(workspace, path);
            bool existed = File.Exists(full);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return $"{(existed ? "Replaced" : "Created")} {path} ({content.Length} chars).";
        }

        /// <summary>Replaces one exact occurrence of old_text. Matching ignores CRLF vs LF differences.</summary>
        private static string EditFile(string workspace, string path, string oldText, string newText)
        {
            string full = ResolvePath(workspace, path);
            if (oldText.Length == 0)
                throw new ArgumentException("old_text must not be empty.");

            string original = File.ReadAllText(full);
            bool crlf = original.Contains("\r\n");
            string text = original.Replace("\r\n", "\n");
            string find = oldText.Replace("\r\n", "\n");

            int index = text.IndexOf(find, StringComparison.Ordinal);
            if (index < 0)
                throw new ArgumentException($"old_text was not found in '{path}'. Read the file again and copy the text exactly.");
            if (text.IndexOf(find, index + 1, StringComparison.Ordinal) >= 0)
                throw new ArgumentException($"old_text appears more than once in '{path}'. Include more surrounding lines so it is unique.");

            string updated = text[..index] + newText.Replace("\r\n", "\n") + text[(index + find.Length)..];
            File.WriteAllText(full, crlf ? updated.Replace("\n", "\r\n") : updated);
            return $"Edited {path}.";
        }

        private static string DeleteFile(string workspace, string path)
        {
            string full = ResolvePath(workspace, path);
            if (!File.Exists(full))
                throw new FileNotFoundException($"'{path}' is not a file (folders cannot be deleted).");
            File.Delete(full);
            return $"Deleted {path}.";
        }

        private static string MoveFile(string workspace, string path, string newPath)
        {
            string from = ResolvePath(workspace, path);
            string to = ResolvePath(workspace, newPath);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            return $"Moved {path} to {newPath}.";
        }

        private static string CreateFolder(string workspace, string path)
        {
            Directory.CreateDirectory(ResolvePath(workspace, path));
            return $"Folder {path} is ready.";
        }

        /// <summary>Shows what a file-changing tool is about to do and asks the user to allow it.</summary>
        private static bool Approve(string tool, JsonElement args)
        {
            Console.WriteLine($"\n[approval] The AI wants to run {tool}:");
            foreach (string name in new[] { "path", "new_path", "old_text", "new_text", "content" })
            {
                string? value = GetString(args, name);
                if (value is null)
                    continue;
                var lines = value.Replace("\r\n", "\n").Split('\n');
                Console.WriteLine($"  {name}:");
                foreach (string line in lines.Take(ApprovalPreviewLines))
                    Console.WriteLine($"    | {line}");
                if (lines.Length > ApprovalPreviewLines)
                    Console.WriteLine($"    | ... ({lines.Length - ApprovalPreviewLines} more lines)");
            }
            Console.Write("Allow? (y/n): ");
            return Console.ReadLine()?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
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
