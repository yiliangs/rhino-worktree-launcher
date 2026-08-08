using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Rwl.RhinoVerifier
{
    [Guid("5884db24-3356-42d8-8bd2-3d7fdcb7616c")]
    public sealed class RwlVerifyLaunchCommand : Command
    {
        public override string EnglishName
        {
            get { return "RwlVerifyLaunch"; }
        }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            string requestPath = Environment.GetEnvironmentVariable("RWL_VERIFY_REQUEST");
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                RhinoApp.WriteLine("RWL verification request is missing.");
                return Result.Failure;
            }

            VerifyRequest request = null;
            try
            {
                request = Read<VerifyRequest>(requestPath);
                if (request.SchemaVersion != 1 || request.PluginId == Guid.Empty)
                    throw new InvalidDataException("The RWL verification request is invalid.");

                bool loaded = PlugIn.LoadPlugIn(request.PluginId, true, true);
                PlugIn plugin = PlugIn.Find(request.PluginId);
                if (!loaded && plugin == null)
                    throw new InvalidOperationException("Rhino could not load the requested plug-in.");
                if (plugin == null)
                    throw new InvalidOperationException("Rhino did not expose the loaded plug-in instance.");

                string pluginPath = plugin.GetType().Assembly.Location;
                List<VerifyDependency> dependencies = new List<VerifyDependency>();
                Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (VerifyDependency expected in request.CriticalDependencies ?? new VerifyDependency[0])
                {
                    Assembly assembly = loadedAssemblies.FirstOrDefault(candidate => string.Equals(
                        candidate.GetName().Name,
                        expected.Name,
                        StringComparison.OrdinalIgnoreCase));
                    if (assembly == null || string.IsNullOrWhiteSpace(assembly.Location))
                        throw new InvalidOperationException(
                            string.Format("Critical dependency '{0}' is not loaded.", expected.Name));
                    dependencies.Add(new VerifyDependency
                    {
                        Name = expected.Name,
                        Path = assembly.Location
                    });
                }

                WriteAtomic(request.ResultPath, new VerifyResult
                {
                    SchemaVersion = 1,
                    Status = "loaded",
                    LaunchId = request.LaunchId,
                    ProcessId = Process.GetCurrentProcess().Id,
                    PluginPath = pluginPath,
                    CriticalDependencies = dependencies.ToArray()
                });
                return Result.Success;
            }
            catch (Exception exception)
            {
                RhinoApp.WriteLine("RWL verification failed: {0}", exception.Message);
                if (request != null && !string.IsNullOrWhiteSpace(request.ResultPath))
                {
                    try
                    {
                        WriteAtomic(request.ResultPath, new VerifyResult
                        {
                            SchemaVersion = 1,
                            Status = "failed",
                            Error = exception.Message,
                            LaunchId = request.LaunchId,
                            ProcessId = Process.GetCurrentProcess().Id,
                            CriticalDependencies = new VerifyDependency[0]
                        });
                    }
                    catch
                    {
                    }
                }
                return Result.Failure;
            }
        }

        private static T Read<T>(string path)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (FileStream stream = File.OpenRead(path))
                return (T)serializer.ReadObject(stream);
        }

        private static void WriteAtomic<T>(string path, T value)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temporaryPath = path + "." + Process.GetCurrentProcess().Id + ".tmp";
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    serializer.WriteObject(stream, value);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        [DataContract]
        private sealed class VerifyRequest
        {
            [DataMember(Name = "schemaVersion")]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "launchId")]
            public string LaunchId { get; set; }

            [DataMember(Name = "pluginId")]
            public Guid PluginId { get; set; }

            [DataMember(Name = "pluginPath")]
            public string PluginPath { get; set; }

            [DataMember(Name = "criticalDependencies")]
            public VerifyDependency[] CriticalDependencies { get; set; }

            [DataMember(Name = "resultPath")]
            public string ResultPath { get; set; }
        }

        [DataContract]
        private sealed class VerifyResult
        {
            [DataMember(Name = "schemaVersion")]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "status")]
            public string Status { get; set; }

            [DataMember(Name = "error", EmitDefaultValue = false)]
            public string Error { get; set; }

            [DataMember(Name = "launchId")]
            public string LaunchId { get; set; }

            [DataMember(Name = "processId")]
            public int ProcessId { get; set; }

            [DataMember(Name = "pluginPath", EmitDefaultValue = false)]
            public string PluginPath { get; set; }

            [DataMember(Name = "criticalDependencies")]
            public VerifyDependency[] CriticalDependencies { get; set; }
        }

        [DataContract]
        private sealed class VerifyDependency
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "path")]
            public string Path { get; set; }
        }
    }
}
