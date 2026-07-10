using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityMCP.Editor
{
    internal static class MCPLocalizationBridge
    {
        private const string CommandTypeName =
            "UnityMCP.Editor.Localization.MCPLocalizationCommands, AnkleBreaker.UnityMCP.Editor.Localization";

        public static bool IsAvailable => ResolveCommandType() != null;

        public static object Execute(string route, Dictionary<string, object> args)
        {
            var commandType = ResolveCommandType();
            if (commandType == null)
            {
                return new
                {
                    success = false,
                    errorCode = "localization_package_missing",
                    error = "Unity Localization is not installed. Add com.unity.localization to expose localization tools.",
                };
            }

            var execute = commandType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
            if (execute == null)
                return new { success = false, error = "Localization command entry point was not found" };

            try
            {
                return execute.Invoke(null, new object[] { route, args ?? new Dictionary<string, object>() });
            }
            catch (TargetInvocationException exception)
            {
                var cause = exception.InnerException ?? exception;
                return new { success = false, error = cause.Message, stackTrace = cause.StackTrace };
            }
            catch (Exception exception)
            {
                return new { success = false, error = exception.Message, stackTrace = exception.StackTrace };
            }
        }

        private static Type ResolveCommandType()
        {
            return Type.GetType(CommandTypeName, false);
        }
    }
}
