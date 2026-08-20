using System.Text;

namespace RhinoWorktreeLauncher;

/// <summary>
/// One build output file another program holds open, as MSBuild reported it. This is the
/// ordinary consequence of a Rhino from an earlier launch still running with the plug-in
/// loaded, so it is a named failure class rather than an unclassified build error.
/// </summary>
internal sealed record LockedBuildOutput(string? Path, string? ProjectPath);

/// <summary>
/// Reads a failing build's output while it streams, so a failure can be described without
/// being quoted whole.
///
/// It answers three things. Whether the build was blocked by a locked output file, keyed on
/// MSBuild's <c>MSB3021</c> and <c>MSB3027</c> identifiers rather than on the surrounding
/// prose, which is translated. How many distinct errors there were and what the first one
/// said, which is the message a surface can show at a glance. And a bounded transcript,
/// which is what a surface offers behind a disclosure.
///
/// Nothing unbounded is buffered: a build's output has no size limit, the transcript keeps
/// only its tail, and the launch log holds the unabridged record either way.
/// </summary>
internal sealed class BuildOutputWatch
{
    private const string CopyRetriesExhausted = "MSB3027";
    private const string CopyFailed = "MSB3021";
    private const string CopyRetrying = "MSB3026";
    // Enough to carry MSBuild's error summary and the lines that led to it. Beyond this the
    // launch log is the record.
    private const int TranscriptLines = 200;
    private const int DistinctErrorsCounted = 200;

    private readonly Queue<string> _transcript = new Queue<string>();
    private readonly HashSet<string> _errors = new HashSet<string>(StringComparer.Ordinal);
    private bool _transcriptTruncated;
    private string? _firstError;
    private string? _heldPath;
    private bool _blocked;
    private string? _projectPath;

    public void Observe(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        Remember(line);
        RecordError(line);
        RecordLock(line);
    }

    public void ObserveAll(string text)
    {
        using StringReader reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            Observe(line);
    }

    public LockedBuildOutput? Locked => _blocked ? new LockedBuildOutput(_heldPath, _projectPath) : null;

    /// <summary>
    /// What the build said, as a surface offers it behind a disclosure. Null when the build
    /// said nothing, so a caller can tell an empty detail from a missing one.
    /// </summary>
    public string? Transcript
    {
        get
        {
            if (_transcript.Count == 0)
                return null;
            StringBuilder transcript = new StringBuilder();
            if (_transcriptTruncated)
            {
                transcript.AppendLine(
                    $"Showing the last {TranscriptLines} lines. The launch log holds the whole build output.");
            }
            return transcript.AppendJoin(Environment.NewLine, _transcript).ToString();
        }
    }

    /// <summary>
    /// The message for a build that failed for a reason with no name of its own: how many
    /// distinct errors, and what the first one said. MSBuild repeats every error in its
    /// closing summary, so the errors are counted by content rather than by occurrence. A
    /// build whose errors this could not recognise says only that it failed, and the
    /// transcript carries the rest.
    /// </summary>
    public string DescribeFailure()
    {
        if (_firstError is null)
            return "The build failed.";
        string count = _errors.Count == 1
            ? "The build failed with 1 error."
            : $"The build failed with {_errors.Count} errors.";
        return count + Environment.NewLine + _firstError;
    }

    private void Remember(string line)
    {
        _transcript.Enqueue(line.TrimEnd());
        while (_transcript.Count > TranscriptLines)
        {
            _ = _transcript.Dequeue();
            _transcriptTruncated = true;
        }
    }

    // MSBuild's canonical diagnostic is "origin: error CODE: text [project]". The word is
    // translated under a localised SDK, which costs the count and the first line but never
    // the transcript.
    private void RecordError(string line)
    {
        if (line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) < 0)
            return;
        string error = WithoutProjectAttribution(line).Trim();
        if (_errors.Count < DistinctErrorsCounted)
            _ = _errors.Add(error);
        _firstError ??= error;
    }

    private void RecordLock(string line)
    {
        if (!line.Contains(CopyRetrying, StringComparison.Ordinal) &&
            !line.Contains(CopyRetriesExhausted, StringComparison.Ordinal) &&
            !line.Contains(CopyFailed, StringComparison.Ordinal))
        {
            return;
        }

        // The retry warning names the same file as the error that follows it, and it
        // arrives first, so the path is usually known before the build gives up.
        _heldPath ??= RootedPathInSingleQuotes(line) ?? DestinationBesideProject(line);
        if (!IsError(line, CopyRetriesExhausted) && !IsError(line, CopyFailed))
            return;
        if (_blocked)
            return;
        _blocked = true;
        _projectPath = TrailingProjectPath(line);
    }

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

    private static string WithoutProjectAttribution(string line)
    {
        string trimmed = line.TrimEnd();
        return TrailingProjectPath(trimmed) is null
            ? trimmed
            : trimmed.Substring(0, trimmed.LastIndexOf('[')).TrimEnd();
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
