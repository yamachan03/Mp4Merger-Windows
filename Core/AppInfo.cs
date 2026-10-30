using System;
using System.Reflection;

namespace Mp4Merger.Core
{
    public static class AppInfo
    {
        /// The version this build was stamped with. The window reads it from here
        /// and the installer reads it off the exe, so <Version> in the project
        /// file is the only place a release number is written.
        public static string DisplayVersion { get; } = Resolve();

        private static string Resolve()
        {
            // This assembly, not the entry assembly: it is the one carrying the
            // version, whoever happens to be hosting it.
            var assembly = typeof(AppInfo).Assembly;
            string version =
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "1.0.0";

            // The SDK appends "+<commit>" to the informational version.
            int plus = version.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }
    }
}
