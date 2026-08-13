using System;
using System.Runtime.Serialization;

namespace RhinoWorktreeLauncher
{
    [DataContract]
    public sealed class VerifierRequest
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
        }

        [DataMember(Name = "launchId")]
        public string LaunchId
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
#if NETFRAMEWORK
        }
#else
        } = string.Empty;
#endif

        [DataMember(Name = "pluginId")]
        public Guid PluginId
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
        }

        [DataMember(Name = "pluginPath")]
        public string PluginPath
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
#if NETFRAMEWORK
        }
#else
        } = string.Empty;
#endif

        [DataMember(Name = "criticalDependencies")]
        public VerifiedDependency[] CriticalDependencies
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
#if NETFRAMEWORK
        }
#else
        } = Array.Empty<VerifiedDependency>();
#endif

        [DataMember(Name = "resultPath")]
        public string ResultPath
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
#if NETFRAMEWORK
        }
#else
        } = string.Empty;
#endif
    }

    [DataContract]
    public sealed class VerifierResult
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
        }

        [DataMember(Name = "status")]
        public string Status
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
#if NETFRAMEWORK
        }
#else
        } = string.Empty;
#endif

#if NETFRAMEWORK
        [DataMember(Name = "error", EmitDefaultValue = false)]
        public string Error { get; set; }
#else
        [DataMember(Name = "error", EmitDefaultValue = false)]
        public string? Error { get; init; }
#endif

        [DataMember(Name = "launchId")]
        public string LaunchId
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
#if NETFRAMEWORK
        }
#else
        } = string.Empty;
#endif

        [DataMember(Name = "processId")]
        public int ProcessId
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
        }

        [DataMember(Name = "pluginPath", EmitDefaultValue = false)]
        public string PluginPath
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
#if NETFRAMEWORK
        }
#else
        } = string.Empty;
#endif

        [DataMember(Name = "criticalDependencies")]
        public VerifiedDependency[] CriticalDependencies
        {
            get;
#if NETFRAMEWORK
            set;
#else
            init;
#endif
#if NETFRAMEWORK
        }
#else
        } = Array.Empty<VerifiedDependency>();
#endif
    }

#if NETFRAMEWORK
    [DataContract]
    public sealed class VerifiedDependency
    {
        public VerifiedDependency()
        {
        }

        public VerifiedDependency(string name, string path)
        {
            Name = name;
            Path = path;
        }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "path")]
        public string Path { get; set; }
    }
#else
    [DataContract]
    public sealed record VerifiedDependency(
        [property: DataMember(Name = "name")] string Name,
        [property: DataMember(Name = "path")] string Path);
#endif
}
