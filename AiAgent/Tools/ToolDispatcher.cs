using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiAgent.Tools
{
    /// <summary>Runs the tool the AI asked for and wraps the outcome as tool_result JSON. Every tool is restricted to the workspace folder.</summary>
    internal static class ToolDispatcher
    {
        private const int ApprovalPreviewLines = 20;
        private static readonly HashSet<string> ToolsRequiringApproval = new() { "create_file", "write_file", "edit_file", "delete_file", "move_file", "create_folder", "delete_folder", "move_folder", "run_command" };
        private const string AllToolNames = "list, read_file, search_files, ask_user, create_file, write_file, edit_file, delete_file, move_file, create_folder, delete_folder, move_folder, run_command";

        /// <summary>Runs a tool and returns the tool_result JSON to send back to the AI.</summary>
        public static string Execute(string? tool, JsonElement args, AgentSettings settings)
        {
            try
            {
                if (tool is not null && ToolsRequiringApproval.Contains(tool) && settings.RequireApproval && !AskForApproval(tool, args))
                    return Result(tool, false, "The user denied this action. Ask what they want instead, or try a different approach.");

                string ws = settings.WorkspacePath;
                ToolOutput result = tool switch
                {
                    "list" => FileTools.List(ws, GetString(args, "path") ?? "."),
                    "read_file" => FileTools.ReadFile(ws, RequireString(args, "path"), GetInt(args, "offset"), settings.MaxReadChars),
                    "search_files" => FileTools.SearchFiles(ws, RequireString(args, "query")),
                    "ask_user" => UserTools.AskUser(RequireString(args, "question")),
                    "create_file" => FileTools.CreateFile(ws, RequireString(args, "path"), RequireString(args, "content")),
                    "write_file" => FileTools.WriteFile(ws, RequireString(args, "path"), RequireString(args, "content")),
                    "edit_file" => FileTools.EditFile(ws, RequireString(args, "path"), RequireString(args, "old_text"), RequireString(args, "new_text")),
                    "delete_file" => FileTools.DeleteFile(ws, RequireString(args, "path")),
                    "move_file" => FileTools.MoveFile(ws, RequireString(args, "path"), RequireString(args, "new_path")),
                    "create_folder" => FileTools.CreateFolder(ws, RequireString(args, "path")),
                    "delete_folder" => FileTools.DeleteFolder(ws, RequireString(args, "path"), GetBool(args, "recursive")),
                    "move_folder" => FileTools.MoveFolder(ws, RequireString(args, "path"), RequireString(args, "new_path")),
                    "run_command" => CommandTools.RunCommand(settings, RequireString(args, "command"), GetString(args, "working_dir") ?? "."),
                    _ => throw new ArgumentException($"Unknown tool '{tool}'. Available tools: {AllToolNames}."),
                };
                return Result(tool, result.Ok, result.Output);
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

        /// <summary>Shows what a file-changing tool or command is about to do and asks the user to allow it.</summary>
        private static bool AskForApproval(string tool, JsonElement args)
        {
            Console.WriteLine($"\n[approval] The AI wants to run {tool}:");
            foreach (string name in new[] { "command", "working_dir", "path", "new_path", "old_text", "new_text", "content" })
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
            if (GetBool(args, "recursive"))
                Console.WriteLine("  recursive: true (deletes the folder AND everything inside it)");
            Console.Write("Allow? (y/n): ");
            return Console.ReadLine()?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
        }

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

        /// <summary>True only for JSON true or the string "true"; anything else (including missing) is false.</summary>
        private static bool GetBool(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
                return false;
            return value.ValueKind == JsonValueKind.True
                || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool flag) && flag);
        }
    }
}
