using System.Collections.Generic;
using UnityEditor;

namespace UnityMCP.Editor
{
    internal static class MCPSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateUserPreferencesProvider()
        {
            return new SettingsProvider(MCPSettingsGUI.UserPreferencesPath, SettingsScope.User)
            {
                label = "Unity MCP",
                guiHandler = _ => MCPSettingsGUI.DrawUserPreferences(true, true),
                keywords = new HashSet<string>
                {
                    "Unity",
                    "MCP",
                    "port",
                    "auto-start",
                    "preferences"
                }
            };
        }

        [SettingsProvider]
        public static SettingsProvider CreateProjectSettingsProvider()
        {
            return new SettingsProvider(MCPSettingsGUI.ProjectSettingsPath, SettingsScope.Project)
            {
                label = "Unity MCP",
                guiHandler = _ => MCPSettingsGUI.DrawProjectSettings(true, true),
                keywords = new HashSet<string>
                {
                    "Unity",
                    "MCP",
                    "context",
                    "categories",
                    "action history",
                    "virtual players"
                }
            };
        }
    }
}
