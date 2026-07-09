using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace UnityMCP.Editor
{
    internal static class MCPResponse
    {
        public static Dictionary<string, object> Error(string message, string errorCode = "error",
            bool retryable = false, Dictionary<string, object> extra = null)
        {
            var response = new Dictionary<string, object>
            {
                { "success", false },
                { "error", message ?? "Unknown error" },
                { "message", message ?? "Unknown error" },
                { "errorCode", string.IsNullOrEmpty(errorCode) ? "error" : errorCode },
                { "retryable", retryable },
            };

            if (extra != null)
            {
                foreach (var pair in extra)
                    response[pair.Key] = pair.Value;
            }

            return response;
        }

        public static Dictionary<string, object> Success(object result = null, Dictionary<string, object> extra = null)
        {
            var response = new Dictionary<string, object>
            {
                { "success", true },
            };

            if (result != null)
                response["result"] = result;

            if (extra != null)
            {
                foreach (var pair in extra)
                    response[pair.Key] = pair.Value;
            }

            return response;
        }

        public static bool TryGetError(object data, out string message, out string errorCode, out bool retryable)
        {
            message = null;
            errorCode = null;
            retryable = false;

            var dictionary = ToDictionary(data);
            if (dictionary == null)
                return false;

            if (dictionary.TryGetValue("retryable", out var retryableValue))
                retryable = ToBool(retryableValue);

            if (dictionary.TryGetValue("errorCode", out var codeValue) && codeValue != null)
                errorCode = codeValue.ToString();

            if (dictionary.TryGetValue("error", out var errorValue) && errorValue != null)
            {
                message = errorValue.ToString();
                if (string.IsNullOrEmpty(errorCode))
                    errorCode = "error";
                return !string.IsNullOrEmpty(message);
            }

            if (dictionary.TryGetValue("success", out var successValue) && ToBool(successValue) == false)
            {
                if (dictionary.TryGetValue("message", out var messageValue) && messageValue != null)
                    message = messageValue.ToString();

                if (string.IsNullOrEmpty(message))
                    message = "Operation failed.";

                if (string.IsNullOrEmpty(errorCode))
                    errorCode = "operation_failed";

                return true;
            }

            return false;
        }

        public static Dictionary<string, object> NormalizeError(object data, string fallbackCode = "error",
            bool fallbackRetryable = false)
        {
            if (!TryGetError(data, out var message, out var errorCode, out var retryable))
                return Error("Operation failed.", fallbackCode, fallbackRetryable);

            var dictionary = ToDictionary(data);
            var response = dictionary != null
                ? new Dictionary<string, object>(dictionary)
                : new Dictionary<string, object>();

            response["success"] = false;
            response["error"] = message;
            response["message"] = message;
            response["errorCode"] = string.IsNullOrEmpty(errorCode) ? fallbackCode : errorCode;
            response["retryable"] = retryable || fallbackRetryable;
            return response;
        }

        public static Dictionary<string, object> ToDictionary(object data)
        {
            if (data == null)
                return null;

            if (data is Dictionary<string, object> typed)
                return typed;

            if (data is IDictionary dictionary)
            {
                var result = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                    result[entry.Key.ToString()] = entry.Value;
                return result;
            }

            var type = data.GetType();
            if (type.IsPrimitive || data is string || data is decimal)
                return null;

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            if (properties.Length == 0)
                return null;

            var reflected = new Dictionary<string, object>();
            foreach (var property in properties)
            {
                if (!property.CanRead)
                    continue;

                try
                {
                    reflected[property.Name] = property.GetValue(data, null);
                }
                catch
                {
                    reflected[property.Name] = null;
                }
            }

            return reflected;
        }

        private static bool ToBool(object value)
        {
            if (value is bool boolValue)
                return boolValue;

            return value != null && bool.TryParse(value.ToString(), out var parsed) && parsed;
        }
    }
}
