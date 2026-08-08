using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoWorktreeLauncher;

public sealed class ProjectCatalog
{
    private const int CurrentSchemaVersion = 4;
    private const int LockAttempts = 20;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly string _catalogPath;
    private readonly string _applicationRoot;

    public ProjectCatalog(string catalogPath)
    {
        _catalogPath = Path.GetFullPath(catalogPath);
        _applicationRoot = Path.GetDirectoryName(_catalogPath)!;
    }

    public async Task<IReadOnlyList<ProjectSnapshot>> LoadAsync(CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken);
        CatalogFile file = await ReadCurrentFileAsync(cancellationToken);
        return file.Projects
            .Select((record, index) => LoadSnapshot(record, index))
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<ProjectRegistration>> LoadRegistrationsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken);
        return (await ReadCurrentFileAsync(cancellationToken)).Projects
            .Where(record => record.IsComplete)
            .Select(record => record.ToRegistration(_applicationRoot))
            .ToArray();
    }

    public async Task<ProjectRegistration> RegisterAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        string selectedPath = Path.GetFullPath(repositoryPath);
        string repositoryRoot = Path.GetFullPath((await ProcessRunner.RunAsync(
            "git",
            selectedPath,
            new[] { "-C", selectedPath, "rev-parse", "--show-toplevel" },
            cancellationToken)).Trim());
        string gitCommonDirectory = Path.GetFullPath((await ProcessRunner.RunAsync(
            "git",
            repositoryRoot,
            new[] { "-C", repositoryRoot, "rev-parse", "--path-format=absolute", "--git-common-dir" },
            cancellationToken)).Trim());
        string primaryCheckout = Path.GetFullPath(Path.GetDirectoryName(gitCommonDirectory)!);

        await EnsureMigratedAsync(cancellationToken);
        ProjectRegistration? existing = (await ReadCurrentFileAsync(cancellationToken)).Projects
            .Where(record => record.IsComplete)
            .Select(record => record.ToRegistration(_applicationRoot))
            .FirstOrDefault(registration => ContextResolver.SamePath(
                registration.GitCommonDirectory,
                gitCommonDirectory));
        ProjectContract contract = existing?.Contract ?? ProjectContract.CreateDefault(primaryCheckout);
        contract.Validate(_applicationRoot);
        string driverPath = contract.ResolveDriverPath(_applicationRoot);
        await ProjectDriverScaffolder.CreateAsync(
            repositoryRoot,
            driverPath,
            contract.Driver.Entrypoint,
            contract.ProjectId,
            cancellationToken);

        ProjectRegistration registration = new ProjectRegistration(
            contract.ProjectId,
            contract.DisplayName,
            gitCommonDirectory,
            primaryCheckout,
            contract.Driver,
            contract.Launch,
            driverPath);
        await ModifyAsync(file =>
        {
            file.Projects.RemoveAll(record =>
                string.Equals(record.ProjectId, registration.ProjectId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(record.GitCommonDirectory) && ContextResolver.SamePath(
                    record.GitCommonDirectory,
                    registration.GitCommonDirectory)));
            file.Projects.Add(CatalogRegistrationRecord.From(registration));
        }, cancellationToken);
        return registration;
    }

    public Task RemoveAsync(string projectId, CancellationToken cancellationToken) => ModifyAsync(
        file => file.Projects.RemoveAll((record, index) => string.Equals(
            record.ProjectId ?? $"legacy-{index}",
            projectId,
            StringComparison.OrdinalIgnoreCase)),
        cancellationToken);

    private ProjectSnapshot LoadSnapshot(CatalogRegistrationRecord record, int index)
    {
        if (!record.IsComplete)
        {
            ProjectRegistration invalid = new ProjectRegistration(
                record.ProjectId ?? $"legacy-{index}",
                record.DisplayName ?? record.ProjectId ?? $"Legacy project {index + 1}",
                record.GitCommonDirectory ?? string.Empty,
                record.PrimaryCheckout ?? string.Empty,
                record.Driver ?? new DriverContract(),
                record.Launch ?? new LaunchContract(),
                string.Empty);
            return Degraded(invalid, "catalog_registration_invalid", "The migrated project registration is incomplete and must be added again.");
        }

        ProjectRegistration registration = record.ToRegistration(_applicationRoot);
        try
        {
            registration.Contract.Validate(_applicationRoot);
            if (!Directory.Exists(registration.PrimaryCheckout))
            {
                return Degraded(
                    registration,
                    "primary_checkout_missing",
                    $"Primary checkout was not found at '{registration.PrimaryCheckout}'.");
            }

            string driverPath = registration.ResolveDriverPath();
            IReadOnlyList<Diagnostic> diagnostics = File.Exists(driverPath)
                ? Array.Empty<Diagnostic>()
                : new[]
                {
                    new Diagnostic(
                        "application_driver_missing",
                        $"The app-owned project driver was not found at '{driverPath}'. Add the project again to restore it.",
                        DiagnosticSeverity.Warning)
                };
            return new ProjectSnapshot(registration, ProjectAvailability.Available, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return Degraded(registration, "project_configuration_unreadable", exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return Degraded(registration, "project_configuration_invalid", exception.Message);
        }
    }

    private static ProjectSnapshot Degraded(
        ProjectRegistration registration,
        string code,
        string message) => new ProjectSnapshot(
        registration,
        ProjectAvailability.Degraded,
        new[] { new Diagnostic(code, message) });

    private async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
            return;

        int schemaVersion = await ReadSchemaVersionAsync(cancellationToken);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported project catalog schema version {schemaVersion}; expected {CurrentSchemaVersion}.");
        }
        if (schemaVersion == CurrentSchemaVersion)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        await using FileStream fileLock = await AcquireLockAsync(cancellationToken);
        schemaVersion = await ReadSchemaVersionAsync(cancellationToken);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported project catalog schema version {schemaVersion}; expected {CurrentSchemaVersion}.");
        }
        if (schemaVersion == CurrentSchemaVersion)
            return;

        string json = await File.ReadAllTextAsync(_catalogPath, cancellationToken);
        CatalogFile previous = JsonSerializer.Deserialize<CatalogFile>(json, JsonDefaults.Read) ?? new CatalogFile();
        LegacyCatalogFile legacy = JsonSerializer.Deserialize<LegacyCatalogFile>(json, JsonDefaults.Read) ??
            new LegacyCatalogFile();
        CatalogFile migrated = new CatalogFile
        {
            SchemaVersion = CurrentSchemaVersion,
            Projects = schemaVersion == 3
                ? previous.Projects.Select(MigrateSchemaThree).ToList()
                : legacy.Projects.Select(MigrateLegacy).ToList()
        };
        foreach (CatalogRegistrationRecord record in migrated.Projects.Where(record => record.IsComplete))
        {
            CatalogRegistrationRecord? oldRecord = previous.Projects.FirstOrDefault(candidate =>
                string.Equals(candidate.ProjectId, record.ProjectId, StringComparison.OrdinalIgnoreCase));
            ProjectRegistration registration = record.ToRegistration(_applicationRoot);
            await ProjectDriverScaffolder.CreateAsync(
                registration.PrimaryCheckout,
                registration.DriverPath,
                oldRecord?.Driver?.Entrypoint ?? ProjectDriverScaffolder.LegacyDriverRelativePath,
                registration.ProjectId,
                cancellationToken);
        }
        string backupPath = Path.Combine(
            Path.GetDirectoryName(_catalogPath)!,
            $"projects.schema{schemaVersion}.backup.json");
        if (!File.Exists(backupPath))
            File.Copy(_catalogPath, backupPath);
        await WriteCurrentFileAsync(migrated, cancellationToken);
    }

    private CatalogRegistrationRecord MigrateSchemaThree(CatalogRegistrationRecord previous)
    {
        ProjectContract defaults = ProjectContract.CreateDefault(
            previous.PrimaryCheckout ?? Environment.CurrentDirectory,
            previous.ProjectId);
        return new CatalogRegistrationRecord
        {
            ProjectId = previous.ProjectId,
            DisplayName = previous.DisplayName ?? defaults.DisplayName,
            GitCommonDirectory = previous.GitCommonDirectory,
            PrimaryCheckout = previous.PrimaryCheckout,
            Driver = defaults.Driver,
            Launch = previous.Launch ?? defaults.Launch
        };
    }

    private CatalogRegistrationRecord MigrateLegacy(LegacyCatalogRegistrationRecord legacy)
    {
        string primaryCheckout = legacy.PrimaryCheckout ??
            (string.IsNullOrWhiteSpace(legacy.ManifestPath)
                ? string.Empty
                : Path.GetDirectoryName(Path.GetFullPath(legacy.ManifestPath))!);
        ProjectContract previous = TryLoadLegacyContract(legacy, primaryCheckout) ??
            ProjectContract.CreateDefault(
                string.IsNullOrWhiteSpace(primaryCheckout) ? Environment.CurrentDirectory : primaryCheckout,
                legacy.ProjectId);
        ProjectContract contract = new ProjectContract
        {
            ProjectId = previous.ProjectId,
            DisplayName = previous.DisplayName,
            Driver = ProjectContract.CreateDefault(primaryCheckout, previous.ProjectId).Driver,
            Launch = previous.Launch
        };
        return new CatalogRegistrationRecord
        {
            ProjectId = contract.ProjectId,
            DisplayName = contract.DisplayName,
            GitCommonDirectory = legacy.GitCommonDirectory,
            PrimaryCheckout = primaryCheckout,
            Driver = contract.Driver,
            Launch = contract.Launch
        };
    }

    private static ProjectContract? TryLoadLegacyContract(
        LegacyCatalogRegistrationRecord legacy,
        string primaryCheckout)
    {
        string? manifestPath = !string.IsNullOrWhiteSpace(legacy.ManifestPath)
            ? legacy.ManifestPath
            : string.IsNullOrWhiteSpace(primaryCheckout)
                ? null
                : Path.Combine(
                    primaryCheckout,
                    legacy.ManifestRelativePath ?? ".rhino-worktree-launcher.json");
        if (manifestPath is null || !File.Exists(manifestPath))
            return null;

        try
        {
            ProjectContract? contract = JsonSerializer.Deserialize<ProjectContract>(
                File.ReadAllText(manifestPath),
                JsonDefaults.Read);
            return contract;
        }
        catch
        {
            return null;
        }
    }

    private async Task ModifyAsync(Action<CatalogFile> modification, CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        await using FileStream fileLock = await AcquireLockAsync(cancellationToken);
        CatalogFile current = await ReadCurrentFileAsync(cancellationToken);
        current.SchemaVersion = CurrentSchemaVersion;
        modification(current);
        await WriteCurrentFileAsync(current, cancellationToken);
    }

    private async Task<CatalogFile> ReadCurrentFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
            return new CatalogFile { SchemaVersion = CurrentSchemaVersion };
        return JsonSerializer.Deserialize<CatalogFile>(
            await File.ReadAllTextAsync(_catalogPath, cancellationToken),
            JsonDefaults.Read) ?? new CatalogFile { SchemaVersion = CurrentSchemaVersion };
    }

    private async Task<int> ReadSchemaVersionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
            return CurrentSchemaVersion;
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(_catalogPath, cancellationToken));
        return document.RootElement.TryGetProperty("schemaVersion", out JsonElement schemaVersion) &&
            schemaVersion.TryGetInt32(out int value)
            ? value
            : 0;
    }

    private async Task WriteCurrentFileAsync(CatalogFile file, CancellationToken cancellationToken)
    {
        string temporaryPath = $"{_catalogPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(file, JsonDefaults.Write),
                cancellationToken);
            File.Move(temporaryPath, _catalogPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
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
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<CatalogRegistrationRecord> Projects { get; set; } = new List<CatalogRegistrationRecord>();
    }

    private sealed class CatalogRegistrationRecord
    {
        public string? ProjectId { get; init; }
        public string? DisplayName { get; init; }
        public string? GitCommonDirectory { get; init; }
        public string? PrimaryCheckout { get; init; }
        public DriverContract? Driver { get; init; }
        public LaunchContract? Launch { get; init; }

        [JsonIgnore]
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(ProjectId) &&
            !string.IsNullOrWhiteSpace(DisplayName) &&
            !string.IsNullOrWhiteSpace(GitCommonDirectory) &&
            !string.IsNullOrWhiteSpace(PrimaryCheckout) &&
            Driver is not null &&
            Launch is not null;

        public ProjectRegistration ToRegistration(string applicationRoot) => new ProjectRegistration(
            ProjectId!,
            DisplayName!,
            GitCommonDirectory!,
            PrimaryCheckout!,
            Driver!,
            Launch!,
            new ProjectContract
            {
                ProjectId = ProjectId!,
                DisplayName = DisplayName!,
                Driver = Driver!,
                Launch = Launch!
            }.ResolveDriverPath(applicationRoot));

        public static CatalogRegistrationRecord From(ProjectRegistration registration) => new CatalogRegistrationRecord
        {
            ProjectId = registration.ProjectId,
            DisplayName = registration.DisplayName,
            GitCommonDirectory = registration.GitCommonDirectory,
            PrimaryCheckout = registration.PrimaryCheckout,
            Driver = registration.Driver,
            Launch = registration.Launch
        };
    }

    private sealed class LegacyCatalogFile
    {
        public List<LegacyCatalogRegistrationRecord> Projects { get; init; } = new List<LegacyCatalogRegistrationRecord>();
    }

    private sealed class LegacyCatalogRegistrationRecord
    {
        public string? ProjectId { get; init; }
        public string? GitCommonDirectory { get; init; }
        public string? PrimaryCheckout { get; init; }
        public string? ManifestRelativePath { get; init; }
        public string? ManifestPath { get; init; }
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
