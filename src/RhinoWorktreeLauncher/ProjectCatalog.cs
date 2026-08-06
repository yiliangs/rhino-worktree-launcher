using System.IO;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

public sealed class ProjectCatalog
{
    private readonly List<ProjectRegistration> _registrations;
    private readonly string _catalogPath;

    private ProjectCatalog(string catalogPath, List<ProjectRegistration> registrations)
    {
        _catalogPath = catalogPath;
        _registrations = registrations;
    }

    public IReadOnlyList<ProjectManifest> LoadProjects()
    {
        List<ProjectManifest> projects = new List<ProjectManifest>();
        foreach (ProjectRegistration registration in _registrations.ToArray())
        {
            try
            {
                projects.Add(ProjectManifest.Load(registration.ManifestPath));
            }
            catch
            {
                _registrations.Remove(registration);
            }
        }
        Save();
        return projects
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ProjectManifest AddProject(string repositoryOrManifestPath)
    {
        ProjectManifest project = ProjectManifest.Load(repositoryOrManifestPath);
        _registrations.RemoveAll(registration => string.Equals(
            Path.GetFullPath(registration.ManifestPath),
            project.ManifestPath,
            StringComparison.OrdinalIgnoreCase));
        _registrations.Add(new ProjectRegistration { ManifestPath = project.ManifestPath });
        Save();
        return project;
    }

    public static ProjectCatalog Load()
    {
        LauncherStoragePaths.EnsureDataRoot();
        string catalogPath = LauncherStoragePaths.ProjectCatalogPath;
        if (!File.Exists(catalogPath))
            return new ProjectCatalog(catalogPath, new List<ProjectRegistration>());

        ProjectCatalogFile? file = JsonSerializer.Deserialize<ProjectCatalogFile>(
            File.ReadAllText(catalogPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return new ProjectCatalog(
            catalogPath,
            file?.Projects ?? new List<ProjectRegistration>());
    }

    private void Save()
    {
        ProjectCatalogFile file = new ProjectCatalogFile { Projects = _registrations };
        string temporaryPath = _catalogPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _catalogPath, true);
    }

    private sealed class ProjectCatalogFile
    {
        public List<ProjectRegistration> Projects { get; set; } = new List<ProjectRegistration>();
    }

    private sealed class ProjectRegistration
    {
        public string ManifestPath { get; set; } = string.Empty;
    }
}
