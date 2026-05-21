using UnityEngine;
using UnityEditor;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Cross-version object-identity helpers.
    ///
    /// Unity 6.5 (6000.5) deprecates the legacy InstanceID APIs as compile errors
    /// (CS0619) in favour of EntityId. EntityId implicitly converts to/from int, so
    /// the plugin keeps int-based ids on the wire (JSON "instanceId" unchanged) and
    /// this shim only swaps the underlying API per Unity version. Pre-6.5 (down to
    /// the supported 2021.3 LTS) keeps the classic InstanceID APIs, since EntityId
    /// does not exist there.
    /// </summary>
    internal static class MCPObjectId
    {
        /// <summary>Stable per-object id. Returns the same int wire format on every Unity version.</summary>
        public static int Get(Object obj)
        {
#if UNITY_6000_5_OR_NEWER
            return obj.GetEntityId();
#else
            return obj.GetInstanceID();
#endif
        }

        /// <summary>Resolve the object previously identified by <see cref="Get"/>.</summary>
        public static Object ToObject(int id)
        {
#if UNITY_6000_5_OR_NEWER
            return EditorUtility.EntityIdToObject(id);
#else
            return EditorUtility.InstanceIDToObject(id);
#endif
        }

        /// <summary>Whether the asset preview for the given object id is still loading.</summary>
        public static bool IsLoadingPreview(int id)
        {
#if UNITY_6000_5_OR_NEWER
            return AssetPreview.IsLoadingAssetPreview((EntityId)id);
#else
            return AssetPreview.IsLoadingAssetPreview(id);
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
