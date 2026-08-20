namespace RhinoWorktreeLauncher;

/// <summary>
/// One build output file another program holds open, as MSBuild reported it. This is the
/// ordinary consequence of a Rhino from an earlier launch still running with the plug-in
/// loaded, so it is a named failure class rather than an unclassified build error.
/// </summary>
internal sealed record LockedBuildOutput(string? Path, string? ProjectPath);

/// <summary>
/// Reads a locked build output out of MSBuild's own output while the build streams.
///
/// Detection keys on MSBuild's error identifiers, <c>MSB3021</c> and <c>MSB3027</c>, which
/// are stable and are not translated, rather than on the surrounding prose, which is. The
/// held file's full path is read from the single-quoted path the copy diagnostics carry;
/// MSBuild does not always emit it, and it does not always name the process holding the
/// file either, so neither is required for the class to be recognised.
///
/// Nothing is buffered: a build's output is unbounded, and only the first locked file is
/// worth naming, because every later one is the same lock reported again.
/// </summary>
internal sealed class LockedBuildOutputWatch
{
    private const string CopyRetriesExhausted = "MSB3027";
    private const string CopyFailed = "MSB3021";
    private const string CopyRetrying = "MSB3026";

    private string? _heldPath;
    private bool _blocked;
    private string? _projectPath;

    public void Observe(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        // The retry warning names the same file as the error that follows it, and it
        // arrives first, so the path is usually known before the build gives up.
        if (!line.Contains(CopyRetrying, StringComparison.Ordinal) &&
            !line.Contains(CopyRetriesExhausted, StringComparison.Ordinal) &&
            !line.Contains(CopyFailed, StringComparison.Ordinal))
        {
            return;
        }

        _heldPath ??= RootedPathInSingleQuotes(line) ?? DestinationBesideProject(line);
        if (!IsError(line, CopyRetriesExhausted) && !IsError(line, CopyFailed))
            return;
        if (_blocked)
            return;
        _blocked = true;
        _projectPath = TrailingProjectPath(line);
    }

    public void ObserveAll(string text)
    {
        using StringReader reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            Observe(line);
    }

    public LockedBuildOutput? Locked => _blocked ? new LockedBuildOutput(_heldPath, _projectPath) : null;

    // MSBuild reports a warning and an error under the same identifier for the same
    // condition, and only the error means the build gave up.
    private static bool IsError(string line, string identifier) =>
        line.Contains("error " + identifier, StringComparison.OrdinalIgnoreCase);

    // "The process cannot access the file 'C:\...\Sample.rhp' because ...": the one full
    // path in the diagnostic. The quoted destinations beside it are relative to the project.
    private static string? RootedPathInSingleQuotes(string line)
    {
        int start = line.IndexOf('\'');
        if (start < 0)
            return null;
        int end = line.IndexOf('\'', start + 1);
        if (end < 0)
            return null;
        string candidate = line.Substring(start + 1, end - start - 1);
        return IsRooted(candidate) ? System.IO.Path.GetFullPath(candidate) : null;
    }

    // The fallback when MSBuild named no full path: the copy destination, which is the
    // second quoted token, resolved against the project the diagnostic is attributed to.
    private static string? DestinationBesideProject(string line)
    {
        string? project = TrailingProjectPath(line);
        if (project is null)
            return null;
        string[] quoted = QuotedTokens(line);
        if (quoted.Length < 2)
            return null;
        string destination = quoted[1];
        return System.IO.Path.GetFullPath(IsRooted(destination)
            ? destination
            : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, destination));
    }

    // MSBuild attributes every diagnostic to the project that raised it, in trailing
    // brackets. A project path is the one thing on the line that is always absolute.
    private static string? TrailingProjectPath(string line)
    {
        string trimmed = line.TrimEnd();
        if (!trimmed.EndsWith(']'))
            return null;
        int start = trimmed.LastIndexOf('[');
        if (start < 0)
            return null;
        string candidate = trimmed.Substring(start + 1, trimmed.Length - start - 2);
        return IsRooted(candidate) ? candidate : null;
    }

    private static string[] QuotedTokens(string line)
    {
        List<string> tokens = new List<string>();
        int index = 0;
        while (true)
        {
            int start = line.IndexOf('"', index);
            if (start < 0)
                break;
            int end = line.IndexOf('"', start + 1);
            if (end < 0)
                break;
            tokens.Add(line.Substring(start + 1, end - start - 1));
            index = end + 1;
        }
        return tokens.ToArray();
    }

    private static bool IsRooted(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
            return false;
        try
        {
            return System.IO.Path.IsPathRooted(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
