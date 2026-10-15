namespace AiAgent.Tools
{
    /// <summary>Keeps every tool inside the workspace folder: resolves paths and rejects anything outside it.</summary>
    internal static class WorkspaceGuard
    {
        internal static string WorkspaceRoot(string workspace) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar;

        /// <summary>Resolves a workspace-relative path and rejects anything outside the workspace.</summary>
        internal static string ResolvePath(string workspace, string path)
        {
            string root = WorkspaceRoot(workspace);
            string full = Path.GetFullPath(Path.Combine(root, path));
            string withSeparator = Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;
            if (!withSeparator.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"'{path}' is outside the workspace.");
            return full;
        }

        /// <summary>
        /// Resolves a path to a folder inside the workspace, rejecting the workspace folder itself
        /// so folder tools can never delete, rename or replace the working directory.
        /// </summary>
        internal static string ResolveSubfolder(string workspace, string path)
        {
            string full = ResolvePath(workspace, path);
            if (string.Equals(Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar, WorkspaceRoot(workspace), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The workspace folder itself cannot be changed; use a folder inside it.");
            return full;
        }

        internal static string Relative(string root, string fullPath) =>
            Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }
}
