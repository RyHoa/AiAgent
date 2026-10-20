using System.Diagnostics;
using System.Text;

namespace AiAgent.Tools
{
    /// <summary>The run_command tool: runs an allow-listed command such as dotnet build or npm run build in the workspace.</summary>
    internal static class CommandTools
    {
        private static readonly HashSet<string> ShellOperators = new() { "&&", "||", "|", "&", ";", ">", ">>", "<", "2>", "2>&1" };
        private static readonly string[] ExecutableExtensions = { ".exe", ".cmd", ".bat" };
        private static readonly char[] BatchMetacharacters = { '&', '|', '<', '>', '^', '%', '!', '"', '(', ')', '\r', '\n' };

        /// <summary>
        /// Runs an allow-listed command (no shell) in the workspace and returns its exit code and output.
        /// Output keeps the start and the end, where build errors and test failures usually are.
        /// </summary>
        internal static ToolOutput RunCommand(AgentSettings settings, string command, string workingDir)
        {
            string ws = settings.WorkspacePath;
            var tokens = SplitCommand(command);
            if (tokens.Count == 0)
                throw new ArgumentException("command is empty.");

            if (!settings.AllowedCommands.Any(allowed => StartsWithTokens(tokens, SplitCommand(allowed))))
                throw new UnauthorizedAccessException(
                    $"'{command}' is not allowed. Commands must start with one of: {string.Join(", ", settings.AllowedCommands)}. " +
                    "Shell features like &&, | and > are not supported; run one command per call.");

            if (tokens.FirstOrDefault(ShellOperators.Contains) is { } op)
                throw new ArgumentException($"Shell operator '{op}' is not supported; run one command per call.");
            foreach (string token in tokens.Skip(1))
                CheckPathArgument(ws, token);

            // MSBuild's error for this mistake ("MSB4025 ... Data at the root level is invalid") doesn't tell the AI what went wrong.
            int projectFlag = tokens.FindIndex(t => t.Equals("--project", StringComparison.OrdinalIgnoreCase) || t.Equals("-p", StringComparison.OrdinalIgnoreCase));
            if (projectFlag >= 0 && projectFlag + 1 < tokens.Count && tokens[projectFlag + 1].EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"--project needs a .csproj file, not a .cs file. To run a single file use: dotnet run {tokens[projectFlag + 1]}");

            // npm and npx are .cmd scripts on Windows, which Process.Start can't find by bare name. Windows runs .cmd/.bat
            // files through cmd.exe, which interprets characters like & and | even inside arguments, so those are refused.
            string executable = ResolveExecutable(tokens[0]);
            if (IsBatchFile(executable))
            {
                foreach (string token in tokens.Skip(1))
                {
                    if (token.IndexOfAny(BatchMetacharacters) >= 0)
                        throw new ArgumentException(
                            $"Argument '{token}' contains a character that is not allowed for '{tokens[0]}' " +
                            "(& | < > ^ % ! \" ( ) or a line break). Remove it and run the command again.");
                }
            }

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = WorkspaceGuard.ResolvePath(ws, workingDir),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string token in tokens.Skip(1))
                start.ArgumentList.Add(token);

            // Quieter output, and no long-lived build servers holding the output pipes open after the command ends.
            start.Environment["DOTNET_NOLOGO"] = "1";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            start.Environment["UseSharedCompilation"] = "false";

            var output = new StringBuilder();
            void Append(object _, DataReceivedEventArgs e)
            {
                if (e.Data is null)
                    return;
                lock (output)
                    output.AppendLine(e.Data);
            }

            Console.WriteLine($"[running] {command}");
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{tokens[0]}'.");
            process.OutputDataReceived += Append;
            process.ErrorDataReceived += Append;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close(); // programs can't wait for keyboard input

            bool timedOut = !process.WaitForExit(settings.CommandTimeoutSeconds * 1000);
            if (timedOut)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5000); // let the last output lines arrive

            string text;
            lock (output)
                text = output.ToString().Trim();
            // Show workspace paths as relative paths, e.g. "src/App.cs(3,5): error ...", to save characters.
            text = text.Replace(WorkspaceGuard.WorkspaceRoot(ws), "", StringComparison.OrdinalIgnoreCase);
            text = Shorten(text, settings.MaxCommandOutputChars);

            string status = timedOut
                ? $"Timed out after {settings.CommandTimeoutSeconds}s and was stopped."
                : $"Exit code: {process.ExitCode}";
            return new ToolOutput(!timedOut && process.ExitCode == 0, text.Length > 0 ? $"{status}\n{text}" : status);
        }

        /// <summary>
        /// Finds the program for a bare command name like "dotnet" or "npm" by searching the PATH folders in order,
        /// trying .exe, then .cmd, then .bat in each folder. Names with a folder or an extension are used as given.
        /// </summary>
        private static string ResolveExecutable(string name)
        {
            if (Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\') || Path.HasExtension(name))
                return name;

            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string folder = entry.Trim().Trim('"');
                if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    continue;
                foreach (string extension in ExecutableExtensions)
                {
                    string candidate = Path.Combine(folder, name + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            throw new FileNotFoundException($"'{name}' was not found on PATH. Is it installed?");
        }

        private static bool IsBatchFile(string executable)
        {
            string extension = Path.GetExtension(executable);
            return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        }

        private static string Shorten(string text, int maxChars)
        {
            if (text.Length <= maxChars)
                return text;
            int head = maxChars / 4;
            int tail = maxChars - head;
            return $"{text[..head]}\n... [{text.Length - maxChars} chars omitted] ...\n{text[^tail..]}";
        }

        /// <summary>Rejects absolute paths and ".." in command arguments that point outside the workspace.</summary>
        private static void CheckPathArgument(string workspace, string token)
        {
            // For options like --output=path, check the value part.
            string value = token.StartsWith('-')
                ? (token.Contains('=') ? token[(token.IndexOf('=') + 1)..] : "")
                : token;
            if (value.Length > 0 && (Path.IsPathRooted(value) || value.Contains("..")))
                WorkspaceGuard.ResolvePath(workspace, value);
        }

        /// <summary>Splits a command line on spaces, keeping "quoted parts" together.</summary>
        private static List<string> SplitCommand(string command)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, hasToken = false;
            foreach (char c in command)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    hasToken = true;
                }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (hasToken)
                        tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                else
                {
                    current.Append(c);
                    hasToken = true;
                }
            }
            if (hasToken)
                tokens.Add(current.ToString());
            return tokens;
        }

        private static bool StartsWithTokens(List<string> tokens, List<string> prefix) =>
            prefix.Count > 0 && prefix.Count <= tokens.Count &&
            prefix.Select((p, i) => string.Equals(p, tokens[i], StringComparison.OrdinalIgnoreCase)).All(match => match);
    }
}
