using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityMCP.Editor
{
    internal static class MCPCapabilityRegistry
    {
        private sealed class Capability
        {
            internal string Name;
            internal string RoutePrefix;
            internal Func<bool> IsAvailable;
            internal string Requirement;
        }

        private static readonly Capability[] OptionalCapabilities =
        {
            new Capability
            {
                Name = "localization",
                RoutePrefix = "localization/",
                IsAvailable = () => MCPLocalizationBridge.IsAvailable,
                Requirement = "com.unity.localization"
            },
            new Capability
            {
                Name = "shadergraph",
                RoutePrefix = "shadergraph/",
                IsAvailable = MCPShaderGraphCommands.IsShaderGraphInstalled,
                Requirement = "com.unity.shadergraph or a render pipeline package that contains Shader Graph"
            },
            new Capability
            {
                Name = "amplify",
                RoutePrefix = "amplify/",
                IsAvailable = MCPAmplifyCommands.IsAmplifyInstalled,
                Requirement = "Amplify Shader Editor"
            },
            new Capability
            {
                Name = "uma",
                RoutePrefix = "uma/",
                IsAvailable = IsUmaAvailable,
                Requirement = "UMA with UMA_INSTALLED scripting define"
            }
        };

        internal static bool IsRouteAvailable(string route)
        {
            Capability capability = FindForRoute(route);
            return capability == null || SafeIsAvailable(capability);
        }

        internal static string GetCapabilityName(string route)
        {
            return FindForRoute(route)?.Name ?? "core";
        }

        internal static object GetCapabilities()
        {
            var optional = OptionalCapabilities.Select(capability => new Dictionary<string, object>
            {
                { "name", capability.Name },
                { "routePrefix", capability.RoutePrefix },
                { "available", SafeIsAvailable(capability) },
                { "requirement", capability.Requirement }
            }).ToList();

            return new Dictionary<string, object>
            {
                { "coreAvailable", true },
                { "optional", optional },
                { "availableOptional", optional.Where(item => Convert.ToBoolean(item["available"]))
                    .Select(item => item["name"]).ToList() },
                { "unavailableOptional", optional.Where(item => !Convert.ToBoolean(item["available"]))
                    .Select(item => item["name"]).ToList() }
            };
        }

        private static Capability FindForRoute(string route)
        {
            if (string.IsNullOrEmpty(route))
                return null;

            return OptionalCapabilities.FirstOrDefault(capability =>
                route.StartsWith(capability.RoutePrefix, StringComparison.Ordinal));
        }

        private static bool SafeIsAvailable(Capability capability)
        {
            try
            {
                return capability.IsAvailable();
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUmaAvailable()
        {
#if UMA_INSTALLED
            return true;
#else
            return false;
#endif
        }
    }
}
