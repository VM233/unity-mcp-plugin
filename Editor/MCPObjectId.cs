using UnityEngine;
using UnityEditor;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Cross-version object-identity helpers.
    ///
    /// Unity 6.5 (6000.5) deprecates the legacy InstanceID APIs as compile errors
    /// (CS0619) in favour of EntityId, and also deprecates EntityId's int cast —
    /// entity ids will no longer be representable by an int. This shim therefore
    /// exposes ids as long and, on 6.5, round-trips through the non-obsolete
    /// EntityId.ToULong / EntityId.FromULong API. Pre-6.5 (down to the supported
    /// 2021.3 LTS) keeps the classic int InstanceID APIs, widened to long.
    ///
    /// long is chosen because int converts to it implicitly, so existing int-based
    /// call sites are unaffected. The JSON "instanceId" wire field stays a number.
    /// </summary>
    internal static class MCPObjectId
    {
        /// <summary>Stable per-object id. JSON "instanceId" wire field (numeric).</summary>
        public static long Get(Object obj)
        {
#if UNITY_6000_5_OR_NEWER
            return (long)EntityId.ToULong(obj.GetEntityId());
#else
            return obj.GetInstanceID();
#endif
        }

        /// <summary>Resolve the object previously identified by <see cref="Get"/>.</summary>
        public static Object ToObject(long id)
        {
#if UNITY_6000_5_OR_NEWER
            return EditorUtility.EntityIdToObject(EntityId.FromULong((ulong)id));
#else
            return EditorUtility.InstanceIDToObject((int)id);
#endif
        }

        /// <summary>Whether the asset preview for the given object id is still loading.</summary>
        public static bool IsLoadingPreview(long id)
        {
#if UNITY_6000_5_OR_NEWER
            return AssetPreview.IsLoadingAssetPreview(EntityId.FromULong((ulong)id));
#else
            return AssetPreview.IsLoadingAssetPreview((int)id);
#endif
        }

        /// <summary>Whether a SerializedProperty currently holds a non-null object reference.</summary>
        public static bool HasObjectRef(SerializedProperty sp)
        {
#if UNITY_6000_5_OR_NEWER
            return sp.objectReferenceEntityIdValue != EntityId.None;
#else
            return sp.objectReferenceInstanceIDValue != 0;
#endif
        }
    }
}
