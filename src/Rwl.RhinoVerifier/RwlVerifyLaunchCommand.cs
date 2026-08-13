using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;
using RhinoWorktreeLauncher;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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

            VerifierRequest request = null;
            try
            {
                request = Read<VerifierRequest>(requestPath);
                if (request.SchemaVersion != 1 || request.PluginId == Guid.Empty)
                    throw new InvalidDataException("The RWL verification request is invalid.");

                bool loaded = PlugIn.LoadPlugIn(request.PluginId, true, true);
                PlugIn plugin = PlugIn.Find(request.PluginId);
                if (!loaded && plugin == null)
                    throw new InvalidOperationException("Rhino could not load the requested plug-in.");
                if (plugin == null)
                    throw new InvalidOperationException("Rhino did not expose the loaded plug-in instance.");

                string pluginPath = plugin.GetType().Assembly.Location;
                List<VerifiedDependency> dependencies = new List<VerifiedDependency>();
                Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (VerifiedDependency expected in request.CriticalDependencies ?? new VerifiedDependency[0])
                {
                    Assembly assembly = loadedAssemblies.FirstOrDefault(candidate => string.Equals(
                        candidate.GetName().Name,
                        expected.Name,
                        StringComparison.OrdinalIgnoreCase));
                    if (assembly == null || string.IsNullOrWhiteSpace(assembly.Location))
                        throw new InvalidOperationException(
                            string.Format("Critical dependency '{0}' is not loaded.", expected.Name));
                    dependencies.Add(new VerifiedDependency(expected.Name, assembly.Location));
                }

                WriteAtomic(request.ResultPath, new VerifierResult
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
                        WriteAtomic(request.ResultPath, new VerifierResult
                        {
                            SchemaVersion = 1,
                            Status = "failed",
                            Error = exception.Message,
                            LaunchId = request.LaunchId,
                            ProcessId = Process.GetCurrentProcess().Id,
                            CriticalDependencies = new VerifiedDependency[0]
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

    }
}
