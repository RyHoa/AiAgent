using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiAgent
{
    /// <summary>
    /// The history of one chat as a JSON Lines file: one event per line, written to disk immediately, so a crash
    /// loses at most the step in progress. Reopening a chat appends to the same file.
    /// </summary>
    internal class SessionLog : IDisposable
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly StreamWriter _writer;
        private readonly object _lock = new object();

        public string FilePath { get; }

        private SessionLog(string filePath)
        {
            FilePath = filePath;
            // FileShare.Read: the file can be opened in an editor while the app is running.
            var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _writer.AutoFlush = true;
        }

        /// <summary>Starts a new chat file named after the current time, e.g. 2026-10-27_140215.jsonl.</summary>
        public static SessionLog CreateNew(string folder, AgentSettings settings, string aiChatUrl)
        {
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string path = Path.Combine(folder, stamp + ".jsonl");
            int copy = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(folder, $"{stamp}_{copy}.jsonl");
                copy++;
            }

            var log = new SessionLog(path);
            var fields = new Dictionary<string, object?>();
            fields["workspace"] = settings.WorkspacePath;
            fields["chatUrl"] = aiChatUrl;
            log.Write("session_start", fields);
            return log;
        }

        /// <summary>Reopens an existing chat file; everything from now on is appended after a "resumed" event.</summary>
        public static SessionLog OpenExisting(string filePath)
        {
            var log = new SessionLog(filePath);
            log.Write("resumed", null);
            return log;
        }

        /// <param name="continued">True when this continues an unfinished task from before the app stopped.</param>
        public void TaskStarted(string task, bool continued)
        {
            var fields = new Dictionary<string, object?>();
            fields["task"] = task;
            if (continued)
                fields["continued"] = true;
            Write("task_start", fields);
        }

        /// <param name="marker">null for normal task traffic, "persona" or "replay" for messages that must never be replayed.</param>
        public void Sent(string text, string? marker)
        {
            Write("sent", TextFields(text, marker));
        }

        /// <param name="marker">null for normal task traffic, "persona" or "replay" for messages that must never be replayed.</param>
        public void Replied(string text, string? marker)
        {
            Write("reply", TextFields(text, marker));
        }

        public void ToolCall(string? tool, string? thought, JsonElement arguments)
        {
            var fields = new Dictionary<string, object?>();
            fields["tool"] = tool;
            if (!string.IsNullOrWhiteSpace(thought))
                fields["thought"] = thought;
            fields["arguments"] = arguments.ValueKind == JsonValueKind.Undefined ? null : arguments.Clone();
            Write("tool_call", fields);
        }

        /// <summary>Stores the tool_result JSON as nested JSON (not an escaped string), so the log stays readable.</summary>
        public void ToolResult(string resultJson)
        {
            var fields = new Dictionary<string, object?>();
            try
            {
                using JsonDocument document = JsonDocument.Parse(resultJson);
                fields["result"] = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                fields["result"] = resultJson;
            }
            Write("tool_result", fields);
        }

        /// <param name="status">"complete", "error" or "stopped".</param>
        public void TaskEnded(string status, string? message)
        {
            var fields = new Dictionary<string, object?>();
            fields["status"] = status;
            fields["message"] = message;
            Write("task_end", fields);
        }

        public void SessionEnded()
        {
            Write("session_end", null);
        }

        public void Crashed(string message)
        {
            var fields = new Dictionary<string, object?>();
            fields["message"] = message;
            Write("crash", fields);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _writer.Dispose();
            }
        }

        private static Dictionary<string, object?> TextFields(string text, string? marker)
        {
            var fields = new Dictionary<string, object?>();
            fields["text"] = text;
            if (marker is not null)
                fields[marker] = true;
            return fields;
        }

        private void Write(string type, Dictionary<string, object?>? fields)
        {
            var record = new Dictionary<string, object?>();
            record["time"] = DateTimeOffset.Now.ToString("o");
            record["type"] = type;
            if (fields is not null)
            {
                foreach (var field in fields)
                    record[field.Key] = field.Value;
            }

            string line = JsonSerializer.Serialize(record, Options);
            lock (_lock)
            {
                _writer.WriteLine(line);
            }
        }
    }
}
