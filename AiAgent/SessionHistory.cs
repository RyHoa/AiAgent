using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiAgent
{
    /// <summary>One saved chat, as shown in the startup menu.</summary>
    internal class SessionSummary
    {
        public string FilePath { get; set; } = "";
        public DateTime LastUsed { get; set; }
        public string Title { get; set; } = "";
        public int TaskCount { get; set; }
        /// <summary>The task that was running when the app stopped, if any.</summary>
        public string? UnfinishedTask { get; set; }
        /// <summary>True when the last run of this chat ended with a normal exit (blank task).</summary>
        public bool EndedNormally { get; set; }
    }

    /// <summary>The previous conversation as text, split into parts small enough for the chat box.</summary>
    internal class ReplayResult
    {
        public List<string> Parts { get; set; } = new List<string>();
        public string? UnfinishedTask { get; set; }
    }

    /// <summary>Finds, reads, replays and deletes saved chats (see SessionLog for the file format).</summary>
    internal static class SessionHistory
    {
        private const int TitleLength = 50;
        private const string ShortenedNote = "… (shortened; re-read the file if you need it)";
        // Tool arguments that hold whole file contents; shortened in the replay like large tool outputs.
        private static readonly HashSet<string> LargeArguments = new HashSet<string> { "content", "new_text", "old_text" };
        private static readonly JsonSerializerOptions CompactJson = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// The folder holding this workspace's chats: Sessions/&lt;workspace name&gt;-&lt;hash&gt; inside the AiAgent project
        /// folder (git-ignored), or under Agent:SessionsPath when that is set.
        /// </summary>
        public static string GetSessionsFolder(AgentSettings settings)
        {
            string root;
            if (string.IsNullOrWhiteSpace(settings.SessionsPath))
                root = Path.Combine(FindProjectFolder(), "Sessions");
            else if (Path.IsPathRooted(settings.SessionsPath))
                root = settings.SessionsPath;
            else
                root = Path.Combine(FindProjectFolder(), settings.SessionsPath);

            string workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.WorkspacePath));
            string name = SafeFolderName(Path.GetFileName(workspace));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspace.ToLowerInvariant()));
            string shortHash = Convert.ToHexString(hash)[..8].ToLowerInvariant();
            return Path.Combine(root, $"{name}-{shortHash}");
        }

        /// <summary>
        /// The AiAgent project folder (the one containing AiAgent.csproj), found by walking up from the running exe in
        /// bin/. Sessions are kept there so a clean or rebuild doesn't delete them. Falls back to the exe's folder.
        /// </summary>
        public static string FindProjectFolder()
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder is not null)
            {
                if (File.Exists(Path.Combine(folder.FullName, "AiAgent.csproj")))
                    return folder.FullName;
                folder = folder.Parent;
            }
            return AppContext.BaseDirectory;
        }

        /// <summary>All saved chats in the folder, most recently used first.</summary>
        public static List<SessionSummary> FindSessions(string folder)
        {
            var sessions = new List<SessionSummary>();
            if (!Directory.Exists(folder))
                return sessions;

            foreach (string path in Directory.EnumerateFiles(folder, "*.jsonl"))
                sessions.Add(Summarize(path, ReadEvents(path)));

            sessions.Sort(CompareByLastUsedDescending);
            return sessions;
        }

        /// <summary>
        /// Reads a chat file. Lines that aren't valid JSON are skipped: if the app crashed while writing, the last
        /// line may be cut off. Opened with FileShare.ReadWrite so a chat open in another copy of the app can be read.
        /// </summary>
        public static List<JsonElement> ReadEvents(string path)
        {
            var events = new List<JsonElement>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Trim().Length == 0)
                    continue;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    events.Add(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // A partly written line from a crash.
                }
            }
            return events;
        }

        public static SessionSummary Summarize(string path, List<JsonElement> events)
        {
            var summary = new SessionSummary();
            summary.FilePath = path;
            summary.LastUsed = File.GetLastWriteTime(path);

            string? firstTask = null;
            string? openTask = null;
            bool ended = false;
            foreach (JsonElement e in events)
            {
                string type = Text(e, "type") ?? "";
                if (type == "task_start")
                {
                    string task = Text(e, "task") ?? "";
                    firstTask ??= task;
                    if (!IsTrue(e, "continued"))
                        summary.TaskCount++;
                    openTask = task;
                    ended = false;
                }
                else if (type == "task_end")
                {
                    openTask = null;
                }
                else if (type == "resumed" || type == "session_start")
                {
                    // A task still open here was interrupted, but a later run may have moved on to other tasks.
                    openTask = null;
                    ended = false;
                }
                else if (type == "session_end")
                {
                    ended = true;
                }
            }

            summary.Title = firstTask is null ? "(no tasks yet)" : Shorten(firstTask.Replace("\r", " ").Replace("\n", " "), TitleLength);
            summary.UnfinishedTask = openTask;
            summary.EndedNormally = ended && openTask is null;
            return summary;
        }

        /// <summary>
        /// Turns a chat's events into a readable transcript of the whole conversation, split into parts of at most
        /// Agent:ResumeChunkChars. Only tasks, tool calls, results and outcomes are used: the raw sent/reply traffic
        /// (including the persona and earlier replays) is skipped, so reopening a chat never nests old replays.
        /// Large tool outputs and file contents are shortened to Agent:ResumeMaxResultChars.
        /// </summary>
        public static ReplayResult BuildReplay(List<JsonElement> events, AgentSettings settings)
        {
            var blocks = new List<string>();
            int taskNumber = 0;
            string? openTask = null;

            foreach (JsonElement e in events)
            {
                string type = Text(e, "type") ?? "";
                if (type == "task_start")
                {
                    if (openTask is not null)
                        blocks.Add("(UNFINISHED: the app stopped here)");
                    openTask = Text(e, "task") ?? "";
                    if (IsTrue(e, "continued"))
                    {
                        blocks.Add($"[Task {taskNumber}, continued after a restart] User: {openTask}");
                    }
                    else
                    {
                        taskNumber++;
                        blocks.Add($"[Task {taskNumber}] User: {openTask}");
                    }
                }
                else if (type == "tool_call")
                {
                    string thought = Text(e, "thought") is { } t ? $" (thought: {t})" : "";
                    string arguments = e.TryGetProperty("arguments", out JsonElement args) ? FormatArguments(args, settings.ResumeMaxResultChars) : "{}";
                    blocks.Add($"AI{thought} -> {Text(e, "tool")} {arguments}");
                }
                else if (type == "tool_result")
                {
                    blocks.Add(FormatResult(e, settings.ResumeMaxResultChars));
                }
                else if (type == "task_end")
                {
                    string status = Text(e, "status") ?? "";
                    string message = Text(e, "message") ?? "";
                    if (status == "complete")
                        blocks.Add($"AI -> complete: {message}");
                    else if (status == "error")
                        blocks.Add($"AI -> error: {message}");
                    else
                        blocks.Add($"(Task stopped: {message})");
                    openTask = null;
                }
                else if (type == "resumed" && openTask is not null)
                {
                    blocks.Add("(UNFINISHED: the app stopped here)");
                    openTask = null;
                }
            }

            var result = new ReplayResult();
            if (openTask is not null)
            {
                blocks.Add("(UNFINISHED: the app stopped here)");
                result.UnfinishedTask = openTask;
            }
            result.Parts = SplitIntoParts(blocks, settings.ResumeChunkChars);
            return result;
        }

        /// <summary>
        /// Deletes one chat file. Only *.jsonl files directly inside the given sessions folder can be deleted.
        /// Throws IOException when the chat is open in another running copy of the app.
        /// </summary>
        public static void DeleteSession(string sessionsFolder, string filePath)
        {
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsFolder));
            string file = Path.GetFullPath(filePath);
            string? parent = Path.GetDirectoryName(file);
            bool insideFolder = parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent), folder, StringComparison.OrdinalIgnoreCase);
            if (!insideFolder || !file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"'{filePath}' is not a chat file in {sessionsFolder}.");

            // Opening with FileShare.None fails if another copy of the app has this chat open.
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
            File.Delete(file);
        }

        private static string FormatArguments(JsonElement arguments, int maxChars)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
                return arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText();

            var shown = new Dictionary<string, object?>();
            foreach (JsonProperty property in arguments.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && LargeArguments.Contains(property.Name))
                    shown[property.Name] = Shorten(property.Value.GetString() ?? "", maxChars, ShortenedNote);
                else
                    shown[property.Name] = property.Value.Clone();
            }
            return JsonSerializer.Serialize(shown, CompactJson);
        }

        private static string FormatResult(JsonElement e, int maxChars)
        {
            if (!e.TryGetProperty("result", out JsonElement result))
                return "Result: (missing)";

            JsonElement toolResult = result;
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("tool_result", out JsonElement inner))
                toolResult = inner;
            if (toolResult.ValueKind != JsonValueKind.Object)
                return "Result: " + Shorten(toolResult.ToString(), maxChars, ShortenedNote);

            bool ok = toolResult.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
            string output = Text(toolResult, "output") ?? "";
            return $"Result ({(ok ? "ok" : "failed")}): {Shorten(output, maxChars, ShortenedNote)}";
        }

        /// <summary>Packs the transcript lines into parts of at most maxChars; a single over-long line is cut into pieces.</summary>
        private static List<string> SplitIntoParts(List<string> blocks, int maxChars)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            foreach (string block in blocks)
            {
                string remaining = block;
                while (remaining.Length > 0)
                {
                    int room = maxChars - current.Length - (current.Length > 0 ? 1 : 0);
                    if (remaining.Length <= room)
                    {
                        if (current.Length > 0)
                            current.Append('\n');
                        current.Append(remaining);
                        remaining = "";
                    }
                    else if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(remaining[..maxChars]);
                        remaining = remaining[maxChars..];
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                }
            }
            if (current.Length > 0)
                parts.Add(current.ToString());
            return parts;
        }

        private static int CompareByLastUsedDescending(SessionSummary a, SessionSummary b)
        {
            return b.LastUsed.CompareTo(a.LastUsed);
        }

        private static string? Text(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
            return null;
        }

        private static bool IsTrue(JsonElement e, string name)
        {
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
        }

        private static string Shorten(string text, int maxChars)
        {
            return Shorten(text, maxChars, "…");
        }

        private static string Shorten(string text, int maxChars, string note)
        {
            string normalized = text.Replace("\r\n", "\n");
            if (normalized.Length <= maxChars)
                return normalized;
            return normalized[..maxChars] + note;
        }

        private static string SafeFolderName(string name)
        {
            var safe = new StringBuilder();
            foreach (char c in name)
                safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return safe.Length > 0 ? safe.ToString() : "workspace";
        }
    }
}
