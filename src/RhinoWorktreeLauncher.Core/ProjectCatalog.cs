using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoWorktreeLauncher;

public sealed class ProjectCatalog
{
    private const int CurrentSchemaVersion = 6;
    private const int LockAttempts = 20;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly string _catalogPath;

    public ProjectCatalog(string catalogPath) => _catalogPath = Path.GetFullPath(catalogPath);

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
        string? pluginProjectPath,
        string? solutionPath,
        BuildConfiguration? buildConfiguration,
        LaunchMode launchMode,
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
            .FirstOrDefault(registration => PathIdentity.AreEquivalent(
                registration.GitCommonDirectory,
                gitCommonDirectory));
        ProjectIdentity identity = existing is null
            ? ProjectIdentity.Create(primaryCheckout)
            : new ProjectIdentity(existing.ProjectId, existing.DisplayName);
        string? effectivePluginProject = pluginProjectPath ?? (existing?.BuildProfile.IsConfigured == true
            ? existing.BuildProfile.PluginProjectPath
            : null);
        string? effectiveSolution = solutionPath ?? (existing?.BuildProfile.IsConfigured == true
            ? existing.BuildProfile.SolutionPath
            : null);
        BuildConfiguration? effectiveConfiguration = buildConfiguration ??
            (existing?.BuildProfile.IsConfigured == true
                ? existing.BuildProfile.SelectedConfiguration
                : null);
        BuildProfile buildProfile = existing is not null &&
            string.IsNullOrWhiteSpace(pluginProjectPath) &&
            string.IsNullOrWhiteSpace(solutionPath) &&
            buildConfiguration is null &&
            launchMode == LaunchMode.BuildAndLaunch
                ? existing.BuildProfile
                : BuildProfileDiscovery.Discover(
                    repositoryRoot,
                    effectivePluginProject,
                    effectiveSolution,
                    effectiveConfiguration,
                    launchMode);
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
                (!string.IsNullOrWhiteSpace(record.GitCommonDirectory) && PathIdentity.AreEquivalent(
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

    public async Task<ProjectRegistration> UpdateConfigAsync(
        ProjectConfigRequest request,
        CancellationToken cancellationToken)
    {
        ProjectRegistration current = (await LoadRegistrationsAsync(cancellationToken))
            .FirstOrDefault(registration => string.Equals(
                registration.ProjectId,
                request.ProjectId,
                StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidOperationException($"Project '{request.ProjectId}' is not registered.");
        BuildProfile profile = BuildProfileDiscovery.Discover(
            current.PrimaryCheckout,
            request.PluginProjectPath,
            request.SolutionPath,
            request.BuildConfiguration,
            request.LaunchMode);

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
            return Classify(registration);
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

    // A registration stays visible whatever its state; only its availability changes.
    // Absent is the one case that cannot be repaired in Config, because there is nothing left to choose.
    private static ProjectSnapshot Classify(ProjectRegistration registration)
    {
        if (!registration.BuildProfile.IsConfigured)
        {
            return Warned(
                registration,
                "build_configuration_incomplete",
                "Choose a canonical solution build configuration in Config before launching.");
        }

        return BuildProfileResolver.Evaluate(registration.PrimaryCheckout, registration.BuildProfile) switch
        {
            BuildProfileState.Absent => Degraded(
                registration,
                "plugin_project_absent",
                $"'{registration.PrimaryCheckout}' no longer contains a Rhino plug-in project. Remove this project or register it again."),
            BuildProfileState.Relocated => Warned(
                registration,
                "plugin_project_missing",
                $"Canonical plug-in project '{registration.BuildProfile.PluginProjectPath}' was not found in the primary checkout. Choose it again in Config."),
            _ => new ProjectSnapshot(registration, ProjectAvailability.Available, Array.Empty<Diagnostic>())
        };
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

    private static ProjectSnapshot Warned(
        ProjectRegistration registration,
        string code,
        string message) => new ProjectSnapshot(
        registration,
        ProjectAvailability.Available,
        new[] { new Diagnostic(code, message, DiagnosticSeverity.Warning) });

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
            BuildProfile = DiscoverProfile(previous.PrimaryCheckout)
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

    private static BuildProfile DiscoverProfile(string? primaryCheckout)
    {
        if (string.IsNullOrWhiteSpace(primaryCheckout) || !Directory.Exists(primaryCheckout))
            return BuildProfile.Unconfigured;
        try
        {
            return BuildProfileDiscovery.Discover(primaryCheckout);
        }
        catch (InvalidDataException)
        {
            return BuildProfile.Unconfigured;
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
        public JsonElement? BuildProfile { get; init; }
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
