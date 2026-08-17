using Microsoft.Win32;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher;

// A faithful copy of one registry key tree, serializable so a removal can survive the
// removing process. Environment strings are captured unexpanded and every value keeps
// its original kind.
[SupportedOSPlatform("windows")]
internal sealed record RegistryKeySnapshot(
    string Name,
    IReadOnlyList<RegistryValueSnapshot> Values,
    IReadOnlyList<RegistryKeySnapshot> Subkeys)
{
    public static RegistryKeySnapshot Capture(RegistryKey key)
    {
        List<RegistryValueSnapshot> values = key.GetValueNames()
            .Select(valueName => RegistryValueSnapshot.Capture(key, valueName))
            .ToList();
        List<RegistryKeySnapshot> subkeys = new List<RegistryKeySnapshot>();
        foreach (string subkeyName in key.GetSubKeyNames())
        {
            using RegistryKey? subkey = key.OpenSubKey(subkeyName, writable: false);
            if (subkey is not null)
                subkeys.Add(Capture(subkey));
        }
        return new RegistryKeySnapshot(key.Name[(key.Name.LastIndexOf('\\') + 1)..], values, subkeys);
    }

    public void RestoreUnder(RegistryKey parent)
    {
        using RegistryKey key = parent.CreateSubKey(Name, writable: true) ??
            throw new UnauthorizedAccessException(
                $"Cannot recreate registry key '{Name}' under '{parent.Name}'.");
        foreach (RegistryValueSnapshot value in Values)
            value.RestoreInto(key);
        foreach (RegistryKeySnapshot subkey in Subkeys)
            subkey.RestoreUnder(key);
    }
}

[SupportedOSPlatform("windows")]
internal sealed record RegistryValueSnapshot(
    string Name,
    RegistryValueKind Kind,
    string? Text,
    string[]? Texts,
    long? Number,
    byte[]? Bytes)
{
    public static RegistryValueSnapshot Capture(RegistryKey key, string name)
    {
        RegistryValueKind kind = key.GetValueKind(name);
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString =>
                new RegistryValueSnapshot(name, kind, (string?)value, null, null, null),
            RegistryValueKind.MultiString =>
                new RegistryValueSnapshot(name, kind, null, (string[]?)value, null, null),
            RegistryValueKind.DWord or RegistryValueKind.QWord =>
                new RegistryValueSnapshot(name, kind, null, null, Convert.ToInt64(value), null),
            _ => new RegistryValueSnapshot(name, kind, null, null, null, value as byte[])
        };
    }

    public void RestoreInto(RegistryKey key) => key.SetValue(Name, Payload(), Kind);

    private object Payload() => Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => Text ?? string.Empty,
        RegistryValueKind.MultiString => Texts ?? Array.Empty<string>(),
        RegistryValueKind.DWord => unchecked((int)(Number ?? 0)),
        RegistryValueKind.QWord => Number ?? 0L,
        _ => Bytes ?? Array.Empty<byte>()
    };
}
