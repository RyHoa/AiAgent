using System.Text;

namespace AiAgent.Tools
{
    /// <summary>The file tools the AI can call: list, read, search, create, write, edit, delete, move and create folders.</summary>
    internal static class FileTools
    {
        private const int MaxSearchResults = 50;
        private const long MaxSearchFileBytes = 1_000_000;
        private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules", "dist", ".vite", "coverage" };

        internal static ToolOutput List(string workspace, string path)
        {
            string root = WorkspaceGuard.WorkspaceRoot(workspace);
            string dir = WorkspaceGuard.ResolvePath(workspace, path);
            var entries = Directory.EnumerateDirectories(dir).Select(d => WorkspaceGuard.Relative(root, d) + "/")
                .Concat(Directory.EnumerateFiles(dir).Select(f => WorkspaceGuard.Relative(root, f)))
                .Order(StringComparer.OrdinalIgnoreCase);
            string result = string.Join('\n', entries);
            return ToolOutput.Success(result.Length > 0 ? result : "(empty folder)");
        }

        internal static ToolOutput ReadFile(string workspace, string path, int offset, int maxChars)
        {
            string text = File.ReadAllText(WorkspaceGuard.ResolvePath(workspace, path));
            offset = Math.Clamp(offset, 0, text.Length);
            int end = Math.Min(text.Length, offset + maxChars);
            string chunk = text[offset..end];
            if (end < text.Length)
                chunk += $"\n[truncated: showing chars {offset}-{end} of {text.Length}; call read_file with offset {end} for more]";
            return ToolOutput.Success(chunk);
        }

        internal static ToolOutput SearchFiles(string workspace, string query)
        {
            string root = WorkspaceGuard.WorkspaceRoot(workspace);
            var results = new StringBuilder();
            int count = 0;

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = WorkspaceGuard.Relative(root, file);
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
                        return ToolOutput.Success(results.Append($"[stopped after {MaxSearchResults} matches]").ToString());
                }
            }

            return ToolOutput.Success(count > 0 ? results.ToString() : "No matches.");
        }

        internal static ToolOutput CreateFile(string workspace, string path, string content)
        {
            string full = WorkspaceGuard.ResolvePath(workspace, path);
            if (File.Exists(full))
                throw new IOException($"'{path}' already exists. Use write_file to replace it or edit_file to change part of it.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return ToolOutput.Success($"Created {path} ({content.Length} chars).");
        }

        internal static ToolOutput WriteFile(string workspace, string path, string content)
        {
            string full = WorkspaceGuard.ResolvePath(workspace, path);
            bool existed = File.Exists(full);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return ToolOutput.Success($"{(existed ? "Replaced" : "Created")} {path} ({content.Length} chars).");
        }

        /// <summary>Replaces one exact occurrence of old_text. Matching ignores CRLF vs LF differences.</summary>
        internal static ToolOutput EditFile(string workspace, string path, string oldText, string newText)
        {
            string full = WorkspaceGuard.ResolvePath(workspace, path);
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
            return ToolOutput.Success($"Edited {path}.");
        }

        internal static ToolOutput DeleteFile(string workspace, string path)
        {
            string full = WorkspaceGuard.ResolvePath(workspace, path);
            if (!File.Exists(full))
                throw new FileNotFoundException($"'{path}' is not a file. Use delete_folder for folders.");
            File.Delete(full);
            return ToolOutput.Success($"Deleted {path}.");
        }

        internal static ToolOutput MoveFile(string workspace, string path, string newPath)
        {
            string from = WorkspaceGuard.ResolvePath(workspace, path);
            string to = WorkspaceGuard.ResolvePath(workspace, newPath);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            return ToolOutput.Success($"Moved {path} to {newPath}.");
        }

        internal static ToolOutput CreateFolder(string workspace, string path)
        {
            Directory.CreateDirectory(WorkspaceGuard.ResolvePath(workspace, path));
            return ToolOutput.Success($"Folder {path} is ready.");
        }

        /// <summary>Deletes a folder inside the workspace. A folder that isn't empty needs recursive = true.</summary>
        internal static ToolOutput DeleteFolder(string workspace, string path, bool recursive)
        {
            string full = WorkspaceGuard.ResolveSubfolder(workspace, path);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException($"'{path}' is not a folder. Use delete_file for files.");
            if (!recursive && Directory.EnumerateFileSystemEntries(full).Any())
                throw new IOException($"'{path}' is not empty. Call delete_folder with \"recursive\": true to delete it and everything in it.");
            Directory.Delete(full, recursive);
            return ToolOutput.Success(recursive ? $"Deleted folder {path} and everything in it." : $"Deleted empty folder {path}.");
        }

        /// <summary>Renames or moves a folder inside the workspace (never the workspace folder itself).</summary>
        internal static ToolOutput MoveFolder(string workspace, string path, string newPath)
        {
            string from = WorkspaceGuard.ResolveSubfolder(workspace, path);
            string to = WorkspaceGuard.ResolveSubfolder(workspace, newPath);
            if (!Directory.Exists(from))
                throw new DirectoryNotFoundException($"'{path}' is not a folder. Use move_file for files.");
            if (Directory.Exists(to) || File.Exists(to))
                throw new IOException($"'{newPath}' already exists. Choose a new name or location.");
            string fromWithSeparator = Path.TrimEndingDirectorySeparator(from) + Path.DirectorySeparatorChar;
            if (to.StartsWith(fromWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Cannot move '{path}' into itself ('{newPath}').");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
            return ToolOutput.Success($"Moved folder {path} to {newPath}.");
        }
    }
}
