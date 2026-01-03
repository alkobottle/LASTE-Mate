using System;
using System.Reflection;

namespace LASTE_Mate.Core;

/// <summary>
/// Helper class to retrieve application version information.
/// </summary>
public static class VersionHelper
{
    /// <summary>
    /// Gets the semantic version string (e.g., "1.0.0").
    /// </summary>
    public static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var versionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        
        if (versionAttribute != null && !string.IsNullOrEmpty(versionAttribute.InformationalVersion))
        {
            var version = versionAttribute.InformationalVersion;
            // Remove any build metadata (everything after +)
            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
            {
                version = version.Substring(0, plusIndex);
            }
            return version;
        }
        
        var versionInfo = assembly.GetName().Version;
        if (versionInfo != null)
        {
            // Return semantic version (MAJOR.MINOR.PATCH) without build/revision
            var build = versionInfo.Build >= 0 ? versionInfo.Build : 0;
            return $"{versionInfo.Major}.{versionInfo.Minor}.{build}";
        }
        
        return "1.0.0"; // Fallback
    }
    
    /// <summary>
    /// Gets the full version string including build number (e.g., "1.0.0.0").
    /// </summary>
    public static string GetFullVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        
        if (version != null)
        {
            return version.ToString();
        }
        
        return "1.0.0.0"; // Fallback
    }
}

