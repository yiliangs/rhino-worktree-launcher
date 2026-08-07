using System.Text.Json;

namespace RhinoWorktreeLauncher;

public sealed class ProjectCatalog
{
    private const int LockAttempts = 20;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly string _catalogPath;

    public ProjectCatalog(string catalogPath) => _catalogPath = Path.GetFullPath(catalogPath);

    public async Task<IReadOnlyList<ProjectSnapshot>> LoadAsync(CancellationToken cancellationToken)
    {
        CatalogFile file = await ReadFileAsync(cancellationToken);
        return file.Projects
            .Select((record, index) => LoadSnapshot(record, index))
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<ProjectRegistration>> LoadRegistrationsAsync(
        CancellationToken cancellationToken) =>
        (await ReadFileAsync(cancellationToken)).Projects
            .Where(record => record.IsSchemaV2)
            .Select(record => record.ToRegistration())
            .ToArray();

    public async Task<ProjectRegistration> RegisterAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        string repositoryRoot = Path.GetFullPath(repositoryPath);
        ProjectManifest manifest = ProjectManifest.Load(repositoryRoot);
        string driverPath = manifest.ResolveDriverPath(repositoryRoot);
        if (!File.Exists(driverPath))
            throw new FileNotFoundException($"Project driver was not found at '{driverPath}'.", driverPath);
        string gitCommonDirectory = (await ProcessRunner.RunAsync(
            "git",
            repositoryRoot,
            new[] { "-C", repositoryRoot, "rev-parse", "--path-format=absolute", "--git-common-dir" },
            cancellationToken)).Trim();
        string primaryCheckout = Path.GetFullPath(Path.GetDirectoryName(gitCommonDirectory)!);
        ProjectRegistration registration = new ProjectRegistration(
            manifest.ProjectId,
            Path.GetFullPath(gitCommonDirectory),
            primaryCheckout,
            Path.GetRelativePath(repositoryRoot, manifest.ManifestPath));

        await ModifyAsync(file =>
        {
            file.SchemaVersion = 2;
            file.Projects.RemoveAll(record =>
                string.Equals(record.ProjectId, registration.ProjectId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(record.GitCommonDirectory, registration.GitCommonDirectory, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(record.ManifestPath) && ContextResolver.SamePath(
                    record.ManifestPath,
                    manifest.ManifestPath)));
            file.Projects.Add(CatalogRegistrationRecord.From(registration));
        }, cancellationToken);
        return registration;
    }

    public Task RemoveAsync(string projectId, CancellationToken cancellationToken) =>
        ModifyAsync(
            file =>
            {
                file.SchemaVersion = 2;
                file.Projects.RemoveAll((record, index) => string.Equals(
                    record.ProjectId ?? LegacyProjectId(record, index),
                    projectId,
                    StringComparison.OrdinalIgnoreCase));
            },
            cancellationToken);

    private ProjectSnapshot LoadSnapshot(CatalogRegistrationRecord record, int index)
    {
        if (!record.IsSchemaV2)
        {
            string legacyPath = record.ManifestPath ?? "unknown manifest";
            ProjectRegistration legacy = new ProjectRegistration(
                LegacyProjectId(record, index),
                string.Empty,
                string.IsNullOrWhiteSpace(record.ManifestPath)
                    ? string.Empty
                    : Path.GetDirectoryName(Path.GetFullPath(record.ManifestPath))!,
                ProjectManifest.DefaultFileName);
            return Degraded(
                legacy,
                "catalog_registration_legacy",
                $"Legacy path-based registration '{legacyPath}' must be explicitly registered again with schema v2.");
        }

        ProjectRegistration registration = record.ToRegistration();
        string manifestPath = Path.Combine(
            registration.PrimaryCheckout,
            registration.ManifestRelativePath);
        try
        {
            ProjectManifest manifest = ProjectManifest.Load(manifestPath);
            string driverPath = manifest.ResolveDriverPath(registration.PrimaryCheckout);
            if (!File.Exists(driverPath))
            {
                return new ProjectSnapshot(
                    registration,
                    manifest,
                    ProjectAvailability.Degraded,
                    new[] { new Diagnostic("driver_missing", $"Project driver was not found at '{driverPath}'.") });
            }
            return new ProjectSnapshot(
                registration,
                manifest,
                ProjectAvailability.Available,
                Array.Empty<Diagnostic>());
        }
        catch (FileNotFoundException exception)
        {
            return Degraded(registration, "manifest_missing", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Degraded(registration, "manifest_unreadable", exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return Degraded(registration, "manifest_invalid", exception.Message);
        }
    }

    private static string LegacyProjectId(CatalogRegistrationRecord record, int index)
    {
        string? directoryName = string.IsNullOrWhiteSpace(record.ManifestPath)
            ? null
            : Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(record.ManifestPath)));
        return $"legacy-{(string.IsNullOrWhiteSpace(directoryName) ? index.ToString() : directoryName)}";
    }

    private static ProjectSnapshot Degraded(
        ProjectRegistration registration,
        string code,
        string message) => new ProjectSnapshot(
            registration,
            null,
            ProjectAvailability.Degraded,
            new[] { new Diagnostic(code, message) });

    private async Task ModifyAsync(Action<CatalogFile> modification, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        await using FileStream fileLock = await AcquireLockAsync(cancellationToken);
        CatalogFile current = await ReadFileAsync(cancellationToken);
        modification(current);

        string temporaryPath = $"{_catalogPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(current, JsonDefaults.Write),
                cancellationToken);
            File.Move(temporaryPath, _catalogPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<CatalogFile> ReadFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
            return new CatalogFile { SchemaVersion = 2 };

        string json = await File.ReadAllTextAsync(_catalogPath, cancellationToken);
        return JsonSerializer.Deserialize<CatalogFile>(json, JsonDefaults.Read) ??
            new CatalogFile { SchemaVersion = 2 };
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        string lockPath = _catalogPath + ".lock";
        for (int attempt = 0; attempt < LockAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < LockAttempts - 1)
            {
                await Task.Delay(LockRetryDelay, cancellationToken);
            }
        }
        throw new IOException($"Could not acquire the project catalog lock at '{lockPath}'.");
    }

    private sealed class CatalogFile
    {
        public int SchemaVersion { get; set; }
        public List<CatalogRegistrationRecord> Projects { get; init; } = new List<CatalogRegistrationRecord>();
    }

    private sealed class CatalogRegistrationRecord
    {
        public string? ProjectId { get; init; }
        public string? GitCommonDirectory { get; init; }
        public string? PrimaryCheckout { get; init; }
        public string? ManifestRelativePath { get; init; }
        public string? ManifestPath { get; init; }

        public bool IsSchemaV2 =>
            !string.IsNullOrWhiteSpace(ProjectId) &&
            !string.IsNullOrWhiteSpace(GitCommonDirectory) &&
            !string.IsNullOrWhiteSpace(PrimaryCheckout) &&
            !string.IsNullOrWhiteSpace(ManifestRelativePath);

        public ProjectRegistration ToRegistration() => new ProjectRegistration(
            ProjectId!,
            GitCommonDirectory!,
            PrimaryCheckout!,
            ManifestRelativePath!);

        public static CatalogRegistrationRecord From(ProjectRegistration registration) => new CatalogRegistrationRecord
        {
            ProjectId = registration.ProjectId,
            GitCommonDirectory = registration.GitCommonDirectory,
            PrimaryCheckout = registration.PrimaryCheckout,
            ManifestRelativePath = registration.ManifestRelativePath
        };
    }
}

internal static class ListExtensions
{
    public static int RemoveAll<T>(this List<T> items, Func<T, int, bool> predicate)
    {
        int removed = 0;
        for (int index = items.Count - 1; index >= 0; index--)
        {
            if (!predicate(items[index], index))
                continue;
            items.RemoveAt(index);
            removed++;
        }
        return removed;
    }
}
