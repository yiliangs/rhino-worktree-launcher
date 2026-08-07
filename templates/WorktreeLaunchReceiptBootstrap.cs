using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RhinoWorktreeLauncherBootstrap
{
    internal static class WorktreeLaunchReceiptBootstrap
    {
        private const string LaunchIdEnvironmentVariable = "RWL_LAUNCH_ID";
        private const string ReceiptPathEnvironmentVariable = "RWL_RECEIPT_PATH";

        public static bool IsRequested
        {
            get { return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LaunchIdEnvironmentVariable)); }
        }

        public static void WriteLoadedReceipt(params string[] criticalAssemblyNames)
        {
            string launchId = Environment.GetEnvironmentVariable(LaunchIdEnvironmentVariable);
            string receiptPath = Environment.GetEnvironmentVariable(ReceiptPathEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(launchId) || string.IsNullOrWhiteSpace(receiptPath))
                return;

            Assembly plugin = Assembly.GetExecutingAssembly();
            string pluginPath = Normalize(plugin.Location);
            string pluginDirectory = Path.GetDirectoryName(pluginPath);
            List<ReceiptDependency> dependencies = new List<ReceiptDependency>();
            foreach (string name in criticalAssemblyNames ?? new string[0])
            {
                Assembly loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                    candidate => string.Equals(candidate.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                string expectedPath = Normalize(Path.Combine(pluginDirectory, name + ".dll"));
                if (loaded == null && File.Exists(expectedPath))
                    loaded = Assembly.LoadFrom(expectedPath);
                if (loaded == null)
                    throw new FileNotFoundException("Critical assembly was not loaded: " + name, expectedPath);

                string loadedPath = Normalize(loaded.Location);
                if (!PathsEqual(expectedPath, loadedPath))
                    throw new InvalidOperationException(
                        "Critical assembly '" + name + "' loaded from '" + loadedPath +
                        "' instead of '" + expectedPath + "'.");
                dependencies.Add(new ReceiptDependency { Name = name, Path = loadedPath });
            }

            WriteAtomic(receiptPath, new LaunchReceipt
            {
                SchemaVersion = 1,
                Status = "loaded",
                LaunchId = launchId,
                ProcessId = Process.GetCurrentProcess().Id,
                PluginPath = pluginPath,
                CriticalDependencies = dependencies.ToArray()
            });
        }

        private static void WriteAtomic(string receiptPath, LaunchReceipt receipt)
        {
            string fullPath = Normalize(receiptPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            string temporaryPath = fullPath + "." + Process.GetCurrentProcess().Id + ".tmp";
            using (FileStream stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(LaunchReceipt));
                serializer.WriteObject(stream, receipt);
                stream.Flush(true);
            }
            if (File.Exists(fullPath))
                File.Replace(temporaryPath, fullPath, null);
            else
                File.Move(temporaryPath, fullPath);
        }

        private static string Normalize(string path) { return Path.GetFullPath(path); }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        [DataContract]
        private sealed class LaunchReceipt
        {
            [DataMember(Name = "schemaVersion", Order = 1)]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "status", Order = 2)]
            public string Status { get; set; }

            [DataMember(Name = "launchId", Order = 3)]
            public string LaunchId { get; set; }

            [DataMember(Name = "processId", Order = 4)]
            public int ProcessId { get; set; }

            [DataMember(Name = "pluginPath", Order = 5)]
            public string PluginPath { get; set; }

            [DataMember(Name = "criticalDependencies", Order = 6)]
            public ReceiptDependency[] CriticalDependencies { get; set; }
        }

        [DataContract]
        private sealed class ReceiptDependency
        {
            [DataMember(Name = "name", Order = 1)]
            public string Name { get; set; }

            [DataMember(Name = "path", Order = 2)]
            public string Path { get; set; }
        }
    }
}
