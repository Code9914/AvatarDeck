namespace AvatarDeck.Core.Parameters;

/// <summary>Describes one avatar parameter as exposed by VRChat or an avatar project.</summary>
/// <param name="Name">Parameter name, e.g. <c>Hoodie</c> or <c>Clothes/Hoodie</c>.</param>
/// <param name="Type">Value type.</param>
/// <param name="IsWritable">False for read-only parameters (VRChat built-ins such as <c>VelocityX</c>).</param>
public sealed record ParameterDefinition(string Name, ParameterType Type, bool IsWritable = true);

/// <summary>Helpers for VRChat OSC avatar parameter addresses.</summary>
public static class ParameterAddress
{
    /// <summary>Prefix of every avatar parameter address.</summary>
    public const string Prefix = "/avatar/parameters/";

    /// <summary>Address VRChat sends when the local avatar changes.</summary>
    public const string AvatarChange = "/avatar/change";

    /// <summary>Builds the OSC address for a parameter name.</summary>
    public static string For(string parameterName) => string.Concat(Prefix, parameterName);

    /// <summary>Extracts the parameter name from an OSC address, if it is an avatar parameter address.</summary>
    public static bool TryGetName(string address, out string name)
    {
        if (address.StartsWith(Prefix, StringComparison.Ordinal) && address.Length > Prefix.Length)
        {
            name = address[Prefix.Length..];
            return true;
        }

        name = string.Empty;
        return false;
    }
}
