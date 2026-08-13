using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class VerifierProtocolTests
{
    private static readonly JsonSerializerOptions CoreJson = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Core_request_is_readable_by_the_Rhino_verifier_serializer()
    {
        VerifierRequest expected = new VerifierRequest
        {
            SchemaVersion = 1,
            LaunchId = "launch-123",
            PluginId = Guid.Parse("ef680fd0-d674-41b5-9c08-5a5d6f925fd1"),
            PluginPath = @"C:\worktree\Sample.rhp",
            CriticalDependencies = new[]
            {
                new VerifiedDependency("Sample.Core", @"C:\worktree\Sample.Core.dll")
            },
            ResultPath = @"C:\launches\verification-result.json"
        };

        string json = JsonSerializer.Serialize(expected, CoreJson);
        VerifierRequest actual = ReadDataContract<VerifierRequest>(json);

        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.LaunchId, actual.LaunchId);
        Assert.Equal(expected.PluginId, actual.PluginId);
        Assert.Equal(expected.PluginPath, actual.PluginPath);
        Assert.Equal(expected.CriticalDependencies, actual.CriticalDependencies);
        Assert.Equal(expected.ResultPath, actual.ResultPath);
    }

    [Fact]
    public void Rhino_verifier_result_is_readable_by_the_Core_serializer()
    {
        VerifierResult expected = new VerifierResult
        {
            SchemaVersion = 1,
            Status = "loaded",
            LaunchId = "launch-123",
            ProcessId = 42,
            PluginPath = @"C:\worktree\Sample.rhp",
            CriticalDependencies = new[]
            {
                new VerifiedDependency("Sample.Core", @"C:\worktree\Sample.Core.dll")
            }
        };

        string json = WriteDataContract(expected);
        VerifierResult actual = JsonSerializer.Deserialize<VerifierResult>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.DoesNotContain("\"error\"", json, StringComparison.Ordinal);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Null(actual.Error);
        Assert.Equal(expected.LaunchId, actual.LaunchId);
        Assert.Equal(expected.ProcessId, actual.ProcessId);
        Assert.Equal(expected.PluginPath, actual.PluginPath);
        Assert.Equal(expected.CriticalDependencies, actual.CriticalDependencies);
    }

    private static T ReadDataContract<T>(string json)
    {
        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
        using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)serializer.ReadObject(stream)!;
    }

    private static string WriteDataContract<T>(T value)
    {
        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
        using MemoryStream stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
