using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoWorktreeLauncher;

public sealed class ProjectCatalog
{
    private const int CurrentSchemaVersion = 5;
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
            .Select(record => record.ToRegistration())
            .ToArray();
    }

    public async Task<ProjectRegistration> RegisterAsync(
        string repositoryPath,
        ProjectAccessGrant access,
        string? importedDriverPath,
        CancellationToken cancellationToken)
    {
        string selectedPath = Path.GetFullPath(repositoryPath);
        string repositoryRoot = Path.GetFullPath((await RunGitReadOnlyAsync(
            selectedPath,
            new[] { "rev-parse", "--show-toplevel" },
            cancellationToken)).Trim());
        string gitCommonDirectory = Path.GetFullPath((await RunGitReadOnlyAsync(
            repositoryRoot,
            new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" },
            cancellationToken)).Trim());
        string primaryCheckout = Path.GetFullPath(Path.GetDirectoryName(gitCommonDirectory)!);

        await EnsureMigratedAsync(cancellationToken);
        ProjectRegistration? existing = (await ReadCurrentFileAsync(cancellationToken)).Projects
            .Where(record => record.IsComplete)
            .Select(record => record.ToRegistration())
            .FirstOrDefault(registration => ContextResolver.SamePath(
                registration.GitCommonDirectory,
                gitCommonDirectory));
        ProjectIdentity identity = existing is null
            ? ProjectIdentity.Create(primaryCheckout)
            : new ProjectIdentity(existing.ProjectId, existing.DisplayName);
        BuildProfile buildProfile = string.IsNullOrWhiteSpace(importedDriverPath)
            ? existing?.BuildProfile ?? BuildProfileDiscovery.Discover(repositoryRoot)
            : await ImportDriverAsync(identity.ProjectId, importedDriverPath, cancellationToken);
        ProjectRegistration registration = new ProjectRegistration(
            identity.ProjectId,
            identity.DisplayName,
            gitCommonDirectory,
            primaryCheckout,
            existing?.RhinoVersion ?? 8,
            access,
            buildProfile);

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

    public async Task<ProjectRegistration> UpdateSettingsAsync(
        ProjectSettingsRequest request,
        CancellationToken cancellationToken)
    {
        ProjectRegistration current = (await LoadRegistrationsAsync(cancellationToken))
            .FirstOrDefault(registration => string.Equals(
                registration.ProjectId,
                request.ProjectId,
                StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidOperationException($"Project '{request.ProjectId}' is not registered.");
        BuildProfile profile;
        if (request.BuildMode == BuildMode.Typed)
        {
            profile = BuildProfileDiscovery.Discover(current.PrimaryCheckout);
        }
        else if (!string.IsNullOrWhiteSpace(request.ImportedDriverPath))
        {
            profile = await ImportDriverAsync(
                current.ProjectId,
                request.ImportedDriverPath,
                cancellationToken);
        }
        else if (current.BuildProfile.Mode == BuildMode.ImportedDriver)
        {
            profile = current.BuildProfile;
        }
        else
        {
            throw new InvalidOperationException("Choose the custom driver RWL should import.");
        }

        ProjectRegistration updated = current with
        {
            Access = new ProjectAccessGrant(true, request.ReadRemote),
            BuildProfile = profile
        };
        await ModifyAsync(file =>
        {
            int index = file.Projects.FindIndex(record => string.Equals(
                record.ProjectId,
                current.ProjectId,
                StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new InvalidOperationException($"Project '{current.ProjectId}' is not registered.");
            file.Projects[index] = CatalogRegistrationRecord.From(updated);
        }, cancellationToken);
        return updated;
    }

    private async Task<BuildProfile> ImportDriverAsync(
        string projectId,
        string selectedPath,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(selectedPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("The selected custom driver was not found.", source);

        string relativePath = Path.Combine("projects", projectId, "drivers", "Driver.ps1");
        string destination = ResolveApplicationPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporaryPath = $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream input = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            await using (FileStream output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return new BuildProfile(
            BuildMode.ImportedDriver,
            Array.Empty<BuildStep>(),
            BuildProfile.Unconfigured.Artifacts,
            relativePath);
    }

    private ProjectSnapshot LoadSnapshot(CatalogRegistrationRecord record, int index)
    {
        if (!record.IsComplete)
        {
            ProjectRegistration invalid = new ProjectRegistration(
                record.ProjectId ?? $"legacy-{index}",
                record.DisplayName ?? record.ProjectId ?? $"Legacy project {index + 1}",
                record.GitCommonDirectory ?? string.Empty,
                record.PrimaryCheckout ?? string.Empty,
                record.RhinoVersion,
                record.Access ?? ProjectAccessGrant.Full,
                record.BuildProfile ?? BuildProfile.Unconfigured);
            return Degraded(
                invalid,
                "catalog_registration_invalid",
                "The migrated project registration is incomplete and must be added again.");
        }

        ProjectRegistration registration = record.ToRegistration();
        try
        {
            Validate(registration);
            List<Diagnostic> diagnostics = new List<Diagnostic>();
            if (!registration.BuildProfile.IsConfigured)
            {
                diagnostics.Add(new Diagnostic(
                    "build_profile_incomplete",
                    "RWL could not fully detect this project's build profile. Edit the app-owned profile before launching.",
                    DiagnosticSeverity.Warning));
            }
            else if (registration.BuildProfile.Mode == BuildMode.ImportedDriver &&
                !File.Exists(ResolveApplicationPath(registration.BuildProfile.ImportedDriverPath!)))
            {
                diagnostics.Add(new Diagnostic(
                    "imported_driver_missing",
                    "The imported driver copy is missing. Re-import it in project settings.",
                    DiagnosticSeverity.Warning));
            }
            return new ProjectSnapshot(registration, ProjectAvailability.Available, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            InvalidDataException)
        {
            return Degraded(registration, "project_configuration_invalid", exception.Message);
        }
    }

    private static void Validate(ProjectRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.ProjectId) ||
            string.IsNullOrWhiteSpace(registration.DisplayName))
        {
            throw new InvalidDataException("ProjectId and DisplayName are required.");
        }
        if (registration.RhinoVersion != 8)
            throw new InvalidDataException($"Unsupported Rhino version {registration.RhinoVersion}; expected 8.");
        if (!registration.Access.ReadProject)
            throw new InvalidDataException("A registered project must retain its project-read grant.");
        if (!Directory.Exists(registration.PrimaryCheckout))
        {
            throw new DirectoryNotFoundException(
                $"Primary checkout was not found at '{registration.PrimaryCheckout}'.");
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
        if (schemaVersion == CurrentSchemaVersion)
            return;

        string json = await File.ReadAllTextAsync(_catalogPath, cancellationToken);
        List<CatalogRegistrationRecord> projects;
        if (schemaVersion >= 3)
        {
            LegacyVersionedCatalogFile previous = JsonSerializer.Deserialize<LegacyVersionedCatalogFile>(
                json,
                JsonDefaults.Read) ?? new LegacyVersionedCatalogFile();
            projects = previous.Projects.Select(MigrateVersioned).ToList();
        }
        else
        {
            LegacyCatalogFile previous = JsonSerializer.Deserialize<LegacyCatalogFile>(json, JsonDefaults.Read) ??
                new LegacyCatalogFile();
            projects = previous.Projects.Select(MigrateLegacy).ToList();
        }

        string backupPath = Path.Combine(
            Path.GetDirectoryName(_catalogPath)!,
            $"projects.schema{schemaVersion}.backup.json");
        if (!File.Exists(backupPath))
            File.Copy(_catalogPath, backupPath);
        await WriteCurrentFileAsync(new CatalogFile
        {
            SchemaVersion = CurrentSchemaVersion,
            Projects = projects
        }, cancellationToken);
    }

    private static CatalogRegistrationRecord MigrateVersioned(LegacyVersionedRegistrationRecord previous)
    {
        ProjectIdentity identity = ProjectIdentity.Create(
            string.IsNullOrWhiteSpace(previous.PrimaryCheckout)
                ? Environment.CurrentDirectory
                : previous.PrimaryCheckout,
            previous.ProjectId);
        return new CatalogRegistrationRecord
        {
            ProjectId = previous.ProjectId ?? identity.ProjectId,
            DisplayName = previous.DisplayName ?? identity.DisplayName,
            GitCommonDirectory = previous.GitCommonDirectory,
            PrimaryCheckout = previous.PrimaryCheckout,
            RhinoVersion = previous.RhinoVersion ?? previous.Launch?.RhinoVersion ?? 8,
            Access = previous.Access ?? ProjectAccessGrant.Full,
            BuildProfile = previous.BuildProfile ?? DiscoverProfile(previous.PrimaryCheckout)
        };
    }

    private static CatalogRegistrationRecord MigrateLegacy(LegacyCatalogRegistrationRecord previous)
    {
        string primaryCheckout = previous.PrimaryCheckout ??
            (string.IsNullOrWhiteSpace(previous.ManifestPath)
                ? string.Empty
                : Path.GetDirectoryName(Path.GetFullPath(previous.ManifestPath))!);
        LegacyProjectManifest? manifest = TryLoadLegacyManifest(previous, primaryCheckout);
        ProjectIdentity identity = ProjectIdentity.Create(
            string.IsNullOrWhiteSpace(primaryCheckout) ? Environment.CurrentDirectory : primaryCheckout,
            previous.ProjectId ?? manifest?.ProjectId);
        return new CatalogRegistrationRecord
        {
            ProjectId = previous.ProjectId ?? manifest?.ProjectId ?? identity.ProjectId,
            DisplayName = manifest?.DisplayName ?? identity.DisplayName,
            GitCommonDirectory = previous.GitCommonDirectory,
            PrimaryCheckout = primaryCheckout,
            RhinoVersion = manifest?.Launch?.RhinoVersion ?? 8,
            Access = ProjectAccessGrant.Full,
            BuildProfile = DiscoverProfile(primaryCheckout)
        };
    }

    private static LegacyProjectManifest? TryLoadLegacyManifest(
        LegacyCatalogRegistrationRecord previous,
        string primaryCheckout)
    {
        string? manifestPath = !string.IsNullOrWhiteSpace(previous.ManifestPath)
            ? previous.ManifestPath
            : string.IsNullOrWhiteSpace(primaryCheckout)
                ? null
                : Path.Combine(
                    primaryCheckout,
                    previous.ManifestRelativePath ?? ".rhino-worktree-launcher.json");
        if (manifestPath is null || !File.Exists(manifestPath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<LegacyProjectManifest>(
                File.ReadAllText(manifestPath),
                JsonDefaults.Read);
        }
        catch
        {
            return null;
        }
    }

    private static BuildProfile DiscoverProfile(string? primaryCheckout) =>
        !string.IsNullOrWhiteSpace(primaryCheckout) && Directory.Exists(primaryCheckout)
            ? BuildProfileDiscovery.Discover(primaryCheckout)
            : BuildProfile.Unconfigured;

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
        throw new IOException($"Could not acquire the project catalog lock at '{_catalogPath}.lock'.");
    }

    private Task<string> RunGitReadOnlyAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) => ProcessRunner.RunAsync(
        "git",
        workingDirectory,
        new[] { "--no-optional-locks", "-C", workingDirectory }.Concat(arguments),
        cancellationToken);

    private string ResolveApplicationPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Application-owned paths must be relative.");
        string path = Path.GetFullPath(Path.Combine(_applicationRoot, relativePath));
        string rootPrefix = _applicationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The application-owned path escaped RWL storage.");
        return path;
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
        public int RhinoVersion { get; init; }
        public ProjectAccessGrant? Access { get; init; }
        public BuildProfile? BuildProfile { get; init; }

        [JsonIgnore]
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(ProjectId) &&
            !string.IsNullOrWhiteSpace(DisplayName) &&
            !string.IsNullOrWhiteSpace(GitCommonDirectory) &&
            !string.IsNullOrWhiteSpace(PrimaryCheckout) &&
            RhinoVersion > 0 &&
            Access is not null &&
            BuildProfile is not null;

        public ProjectRegistration ToRegistration() => new ProjectRegistration(
            ProjectId!,
            DisplayName!,
            GitCommonDirectory!,
            PrimaryCheckout!,
            RhinoVersion,
            Access!,
            BuildProfile!);

        public static CatalogRegistrationRecord From(ProjectRegistration registration) => new CatalogRegistrationRecord
        {
            ProjectId = registration.ProjectId,
            DisplayName = registration.DisplayName,
            GitCommonDirectory = registration.GitCommonDirectory,
            PrimaryCheckout = registration.PrimaryCheckout,
            RhinoVersion = registration.RhinoVersion,
            Access = registration.Access,
            BuildProfile = registration.BuildProfile
        };
    }

    private sealed class LegacyVersionedCatalogFile
    {
        public List<LegacyVersionedRegistrationRecord> Projects { get; init; } =
            new List<LegacyVersionedRegistrationRecord>();
    }

    private sealed class LegacyVersionedRegistrationRecord
    {
        public string? ProjectId { get; init; }
        public string? DisplayName { get; init; }
        public string? GitCommonDirectory { get; init; }
        public string? PrimaryCheckout { get; init; }
        public int? RhinoVersion { get; init; }
        public LegacyLaunchContract? Launch { get; init; }
        public ProjectAccessGrant? Access { get; init; }
        public BuildProfile? BuildProfile { get; init; }
    }

    private sealed class LegacyCatalogFile
    {
        public List<LegacyCatalogRegistrationRecord> Projects { get; init; } =
            new List<LegacyCatalogRegistrationRecord>();
    }

    private sealed class LegacyCatalogRegistrationRecord
    {
        public string? ProjectId { get; init; }
        public string? GitCommonDirectory { get; init; }
        public string? PrimaryCheckout { get; init; }
        public string? ManifestRelativePath { get; init; }
        public string? ManifestPath { get; init; }
    }

    private sealed class LegacyProjectManifest
    {
        public string? ProjectId { get; init; }
        public string? DisplayName { get; init; }
        public LegacyLaunchContract? Launch { get; init; }
    }

    private sealed class LegacyLaunchContract
    {
        public int RhinoVersion { get; init; }
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
