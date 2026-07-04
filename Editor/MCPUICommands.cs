using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for creating and managing Unity UI (UGUI) elements.
    /// </summary>
    public static class MCPUICommands
    {
        // ─── Create Canvas ───

        public static object CreateCanvas(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "Canvas";
            string renderMode = args.ContainsKey("renderMode") ? args["renderMode"].ToString().ToLower() : "overlay";

            var canvasGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");

            var canvas = canvasGo.AddComponent<Canvas>();
            switch (renderMode)
            {
                case "camera": canvas.renderMode = RenderMode.ScreenSpaceCamera; break;
                case "world": canvas.renderMode = RenderMode.WorldSpace; break;
                default: canvas.renderMode = RenderMode.ScreenSpaceOverlay; break;
            }

            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            // Ensure EventSystem exists
            if (UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                var esGo = new GameObject("EventSystem");
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", canvasGo.name },
                { "renderMode", canvas.renderMode.ToString() },
                { "instanceId", MCPObjectId.Get(canvasGo) },
            };
        }

        // ─── Create UI Element ───

        public static object CreateUIElement(Dictionary<string, object> args)
        {
            string type = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "";
            if (string.IsNullOrEmpty(type))
                return new { error = "type is required (text, image, button, panel, slider, toggle, inputfield, dropdown, scrollview)" };

            string name = args.ContainsKey("name") ? args["name"].ToString() : type;
            string parent = args.ContainsKey("parent") ? args["parent"].ToString() : "";

            // Find or create canvas
            Transform parentTransform = null;
            if (!string.IsNullOrEmpty(parent))
            {
                var parentGo = GameObject.Find(parent);
                if (parentGo != null)
                    parentTransform = parentGo.transform;
            }
            if (parentTransform == null)
            {
                var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
                if (canvas == null)
                    return new { error = "No Canvas found. Create a canvas first." };
                parentTransform = canvas.transform;
            }

            GameObject go = null;
            switch (type)
            {
                case "text":
                    go = CreateTextElement(name);
                    break;
                case "image":
                    go = CreateImageElement(name);
                    break;
                case "button":
                    go = CreateButtonElement(name, args);
                    break;
                case "panel":
                    go = CreatePanelElement(name);
                    break;
                case "slider":
                    go = new GameObject(name);
                    go.AddComponent<RectTransform>();
                    go.AddComponent<Slider>();
                    break;
                case "toggle":
                    go = new GameObject(name);
                    go.AddComponent<RectTransform>();
                    go.AddComponent<Toggle>();
                    break;
                case "inputfield":
                    go = CreateInputFieldElement(name);
                    break;
                default:
                    return new { error = $"Unknown UI type '{type}'. Use: text, image, button, panel, slider, toggle, inputfield" };
            }

            go.transform.SetParent(parentTransform, false);
            Undo.RegisterCreatedObjectUndo(go, "Create UI Element");

            // Apply position if provided
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                if (args.ContainsKey("anchoredPosition") && args["anchoredPosition"] is Dictionary<string, object> posDict)
                {
                    float x = posDict.ContainsKey("x") ? Convert.ToSingle(posDict["x"]) : 0;
                    float y = posDict.ContainsKey("y") ? Convert.ToSingle(posDict["y"]) : 0;
                    rt.anchoredPosition = new Vector2(x, y);
                }
                if (args.ContainsKey("sizeDelta") && args["sizeDelta"] is Dictionary<string, object> sizeDict)
                {
                    float w = sizeDict.ContainsKey("x") ? Convert.ToSingle(sizeDict["x"]) : rt.sizeDelta.x;
                    float h = sizeDict.ContainsKey("y") ? Convert.ToSingle(sizeDict["y"]) : rt.sizeDelta.y;
                    rt.sizeDelta = new Vector2(w, h);
                }
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "type", type },
                { "instanceId", MCPObjectId.Get(go) },
                { "parent", parentTransform.name },
            };
        }

        // ─── Get UI Info ───

        public static object GetUIInfo(Dictionary<string, object> args)
        {
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var canvasInfos = new List<Dictionary<string, object>>();

            foreach (var canvas in canvases)
            {
                int childCount = CountUIElements(canvas.transform);
                canvasInfos.Add(new Dictionary<string, object>
                {
                    { "name", canvas.gameObject.name },
                    { "renderMode", canvas.renderMode.ToString() },
                    { "sortingOrder", canvas.sortingOrder },
                    { "uiElementCount", childCount },
                    { "instanceId", MCPObjectId.Get(canvas.gameObject) },
                });
            }

            int totalTexts = UnityEngine.Object.FindObjectsByType<Text>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int totalImages = UnityEngine.Object.FindObjectsByType<Image>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int totalButtons = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

            return new Dictionary<string, object>
            {
                { "canvasCount", canvases.Length },
                { "canvases", canvasInfos },
                { "totalTexts", totalTexts },
                { "totalImages", totalImages },
                { "totalButtons", totalButtons },
            };
        }

        // ─── Set UI Text ───

        public static object SetUIText(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var text = go.GetComponent<Text>();
            if (text == null)
                return new { error = $"No Text component on '{path}'" };

            Undo.RecordObject(text, "Set UI Text");

            if (args.ContainsKey("text"))
                text.text = args["text"].ToString();
            if (args.ContainsKey("fontSize"))
                text.fontSize = Convert.ToInt32(args["fontSize"]);
            if (args.ContainsKey("color") && args["color"] is Dictionary<string, object> colorDict)
            {
                float r = colorDict.ContainsKey("r") ? Convert.ToSingle(colorDict["r"]) : text.color.r;
                float g = colorDict.ContainsKey("g") ? Convert.ToSingle(colorDict["g"]) : text.color.g;
                float b = colorDict.ContainsKey("b") ? Convert.ToSingle(colorDict["b"]) : text.color.b;
                float a = colorDict.ContainsKey("a") ? Convert.ToSingle(colorDict["a"]) : text.color.a;
                text.color = new Color(r, g, b, a);
            }
            if (args.ContainsKey("alignment"))
            {
                string align = args["alignment"].ToString().ToLower();
                switch (align)
                {
                    case "upperleft": text.alignment = TextAnchor.UpperLeft; break;
                    case "uppercenter": text.alignment = TextAnchor.UpperCenter; break;
                    case "upperright": text.alignment = TextAnchor.UpperRight; break;
                    case "middleleft": text.alignment = TextAnchor.MiddleLeft; break;
                    case "middlecenter": text.alignment = TextAnchor.MiddleCenter; break;
                    case "middleright": text.alignment = TextAnchor.MiddleRight; break;
                    case "lowerleft": text.alignment = TextAnchor.LowerLeft; break;
                    case "lowercenter": text.alignment = TextAnchor.LowerCenter; break;
                    case "lowerright": text.alignment = TextAnchor.LowerRight; break;
                }
            }

            EditorUtility.SetDirty(text);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "text", text.text },
                { "fontSize", text.fontSize },
                { "alignment", text.alignment.ToString() },
            };
        }

        // ─── Set UI Image ───

        public static object SetUIImage(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var image = go.GetComponent<Image>();
            if (image == null)
                return new { error = $"No Image component on '{path}'" };

            Undo.RecordObject(image, "Set UI Image");

            if (args.ContainsKey("color") && args["color"] is Dictionary<string, object> colorDict)
            {
                float r = colorDict.ContainsKey("r") ? Convert.ToSingle(colorDict["r"]) : image.color.r;
                float g = colorDict.ContainsKey("g") ? Convert.ToSingle(colorDict["g"]) : image.color.g;
                float b = colorDict.ContainsKey("b") ? Convert.ToSingle(colorDict["b"]) : image.color.b;
                float a = colorDict.ContainsKey("a") ? Convert.ToSingle(colorDict["a"]) : image.color.a;
                image.color = new Color(r, g, b, a);
            }

            if (args.ContainsKey("sprite"))
            {
                string spritePath = args["sprite"].ToString();
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite != null)
                    image.sprite = sprite;
            }

            if (args.ContainsKey("imageType"))
            {
                string imgType = args["imageType"].ToString().ToLower();
                switch (imgType)
                {
                    case "simple": image.type = Image.Type.Simple; break;
                    case "sliced": image.type = Image.Type.Sliced; break;
                    case "tiled": image.type = Image.Type.Tiled; break;
                    case "filled": image.type = Image.Type.Filled; break;
                }
            }

            if (args.ContainsKey("raycastTarget"))
                image.raycastTarget = Convert.ToBoolean(args["raycastTarget"]);

            EditorUtility.SetDirty(image);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "hasSprite", image.sprite != null },
                { "imageType", image.type.ToString() },
            };
        }

        // ─── Editor UI Toolkit Windows ───

        public static object ListEditorUIWindows(Dictionary<string, object> args)
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(window => window != null)
                .OrderBy(window => window.GetType().FullName)
                .Select(BuildWindowInfo)
                .ToList();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "count", windows.Count },
                { "windows", windows },
            };
        }

        public static object GetEditorUITree(Dictionary<string, object> args)
        {
            var window = FindEditorWindow(args, out string error);
            if (window == null)
                return new { error };

            var root = window.rootVisualElement;
            if (root == null)
                return new { error = $"Window '{window.titleContent?.text}' has no rootVisualElement" };

            int maxDepth = GetInt(args, "maxDepth", 8);
            int maxNodes = GetInt(args, "maxNodes", 300);
            bool includeStyle = GetBool(args, "includeStyle", false);
            int count = 0;
            bool truncated = false;
            var tree = BuildElementTree(root, "root", 0, maxDepth, maxNodes, includeStyle, ref count, ref truncated);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "window", BuildWindowInfo(window) },
                { "nodeCount", count },
                { "truncated", truncated },
                { "tree", tree },
            };
        }

        public static object QueryEditorUI(Dictionary<string, object> args)
        {
            var window = FindEditorWindow(args, out string error);
            if (window == null)
                return new { error };

            string name = GetString(args, "name");
            string className = GetString(args, "className");
            string typeName = GetString(args, "typeName");
            string text = GetString(args, "text");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(className) &&
                string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(text))
                return new { error = "At least one query filter is required: name, className, typeName, or text" };

            int maxResults = GetInt(args, "maxResults", 50);
            bool includeStyle = GetBool(args, "includeStyle", false);
            var results = new List<Dictionary<string, object>>();
            QueryElements(window.rootVisualElement, "root", name, className, typeName, text, includeStyle, maxResults, results);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "window", BuildWindowInfo(window) },
                { "count", results.Count },
                { "results", results },
            };
        }

        public static object GetEditorUIStyle(Dictionary<string, object> args)
        {
            var window = FindEditorWindow(args, out string error);
            if (window == null)
                return new { error };

            var element = FindElement(args, window, out error);
            if (element == null)
                return new { error };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "window", BuildWindowInfo(window) },
                { "element", BuildElementInfo(element, GetString(args, "path"), false) },
                { "inlineStyle", BuildInlineStyleInfo(element) },
                { "resolvedStyle", BuildResolvedStyleInfo(element) },
            };
        }

        public static object RepaintEditorUI(Dictionary<string, object> args)
        {
            var window = FindEditorWindow(args, out string error);
            if (window == null)
                return new { error };

            string path = GetString(args, "path");
            if (!string.IsNullOrEmpty(path))
            {
                var element = GetElementByPath(window.rootVisualElement, path);
                if (element == null)
                    return new { error = $"UI Toolkit element path '{path}' was not found" };

                element.MarkDirtyRepaint();
            }

            window.rootVisualElement?.MarkDirtyRepaint();
            window.Repaint();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "window", BuildWindowInfo(window) },
                { "repaintedPath", string.IsNullOrEmpty(path) ? "root" : path },
            };
        }

        // ─── Helpers ───

        private static GameObject CreateTextElement(string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(160, 30);
            var text = go.AddComponent<Text>();
            text.text = "New Text";
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.color = Color.black;
            text.alignment = TextAnchor.MiddleCenter;
            return go;
        }

        private static GameObject CreateImageElement(string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(100, 100);
            go.AddComponent<Image>();
            return go;
        }

        private static GameObject CreateButtonElement(string name, Dictionary<string, object> args)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(160, 30);
            go.AddComponent<Image>();
            go.AddComponent<Button>();

            // Add label
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;
            var text = textGo.AddComponent<Text>();
            text.text = args.ContainsKey("label") ? args["label"].ToString() : "Button";
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.color = Color.black;
            text.alignment = TextAnchor.MiddleCenter;

            return go;
        }

        private static GameObject CreatePanelElement(string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            var image = go.AddComponent<Image>();
            image.color = new Color(1, 1, 1, 0.39f);
            return go;
        }

        private static GameObject CreateInputFieldElement(string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(160, 30);
            var image = go.AddComponent<Image>();
            image.color = Color.white;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = new Vector2(-10, -6);
            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.color = Color.black;
            text.supportRichText = false;

            var placeholderGo = new GameObject("Placeholder");
            placeholderGo.transform.SetParent(go.transform, false);
            var phRt = placeholderGo.AddComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.sizeDelta = new Vector2(-10, -6);
            var phText = placeholderGo.AddComponent<Text>();
            phText.text = "Enter text...";
            phText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            phText.fontStyle = FontStyle.Italic;
            phText.color = new Color(0, 0, 0, 0.5f);

            var inputField = go.AddComponent<InputField>();
            inputField.textComponent = text;
            inputField.placeholder = phText;

            return go;
        }

        private static int CountUIElements(Transform parent)
        {
            int count = 0;
            foreach (Transform child in parent)
            {
                count++;
                count += CountUIElements(child);
            }
            return count;
        }

        private static Dictionary<string, object> BuildWindowInfo(EditorWindow window)
        {
            var root = window.rootVisualElement;
            return new Dictionary<string, object>
            {
                { "instanceId", window.GetInstanceID() },
                { "title", window.titleContent?.text ?? "" },
                { "type", window.GetType().Name },
                { "fullType", window.GetType().FullName },
                { "hasRootVisualElement", root != null },
                { "rootChildCount", root?.childCount ?? 0 },
            };
        }

        private static EditorWindow FindEditorWindow(Dictionary<string, object> args, out string error)
        {
            error = "";
            int instanceId = GetInt(args, "instanceId", 0);
            string windowQuery = GetString(args, "window");
            string typeQuery = GetString(args, "windowType");
            string titleQuery = GetString(args, "title");
            if (string.IsNullOrEmpty(typeQuery))
                typeQuery = GetString(args, "type");

            if (instanceId == 0 && int.TryParse(windowQuery, out int parsedId))
                instanceId = parsedId;

            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId) as EditorWindow;
                if (obj != null)
                    return obj;

                error = $"EditorWindow instanceId '{instanceId}' was not found";
                return null;
            }

            if (string.IsNullOrEmpty(windowQuery) && string.IsNullOrEmpty(typeQuery) && string.IsNullOrEmpty(titleQuery))
            {
                if (EditorWindow.focusedWindow != null)
                    return EditorWindow.focusedWindow;

                error = "No focused EditorWindow. Pass instanceId, window, windowType, or title.";
                return null;
            }

            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>().Where(window => window != null).ToList();
            foreach (var window in windows)
            {
                if (MatchesWindow(window, windowQuery, typeQuery, titleQuery, true))
                    return window;
            }

            foreach (var window in windows)
            {
                if (MatchesWindow(window, windowQuery, typeQuery, titleQuery, false))
                    return window;
            }

            error = $"No EditorWindow matched window='{windowQuery}', windowType='{typeQuery}', title='{titleQuery}'";
            return null;
        }

        private static bool MatchesWindow(EditorWindow window, string windowQuery, string typeQuery, string titleQuery, bool exact)
        {
            string title = window.titleContent?.text ?? "";
            string typeName = window.GetType().Name;
            string fullTypeName = window.GetType().FullName ?? "";

            if (!string.IsNullOrEmpty(windowQuery) &&
                Matches(title, windowQuery, exact) == false &&
                Matches(typeName, windowQuery, exact) == false &&
                Matches(fullTypeName, windowQuery, exact) == false)
                return false;

            if (!string.IsNullOrEmpty(typeQuery) &&
                Matches(typeName, typeQuery, exact) == false &&
                Matches(fullTypeName, typeQuery, exact) == false)
                return false;

            if (!string.IsNullOrEmpty(titleQuery) && Matches(title, titleQuery, exact) == false)
                return false;

            return true;
        }

        private static bool Matches(string value, string query, bool exact)
        {
            if (string.IsNullOrEmpty(query))
                return true;

            return exact
                ? string.Equals(value, query, StringComparison.OrdinalIgnoreCase)
                : value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<string, object> BuildElementTree(
            UnityEngine.UIElements.VisualElement element, string path, int depth, int maxDepth, int maxNodes,
            bool includeStyle, ref int count, ref bool truncated)
        {
            count++;
            var info = BuildElementInfo(element, path, includeStyle);
            if (depth >= maxDepth)
                return info;

            var children = new List<Dictionary<string, object>>();
            int childIndex = 0;
            foreach (var child in element.Children())
            {
                if (count >= maxNodes)
                {
                    truncated = true;
                    break;
                }

                children.Add(BuildElementTree(child, $"{path}/{childIndex}", depth + 1, maxDepth,
                    maxNodes, includeStyle, ref count, ref truncated));
                childIndex++;
            }

            info["children"] = children;
            return info;
        }

        private static Dictionary<string, object> BuildElementInfo(
            UnityEngine.UIElements.VisualElement element, string path, bool includeStyle)
        {
            var info = new Dictionary<string, object>
            {
                { "path", string.IsNullOrEmpty(path) ? "root" : path },
                { "name", element.name ?? "" },
                { "type", element.GetType().Name },
                { "fullType", element.GetType().FullName },
                { "classes", element.GetClasses().ToList() },
                { "text", GetElementText(element) },
                { "tooltip", element.tooltip ?? "" },
                { "visible", element.visible },
                { "enabledSelf", element.enabledSelf },
                { "enabledInHierarchy", element.enabledInHierarchy },
                { "pickingMode", element.pickingMode.ToString() },
                { "childCount", element.childCount },
                { "layout", RectToDictionary(element.layout) },
                { "worldBound", RectToDictionary(element.worldBound) },
            };

            if (includeStyle)
            {
                info["inlineStyle"] = BuildInlineStyleInfo(element);
                info["resolvedStyle"] = BuildResolvedStyleInfo(element);
            }

            return info;
        }

        private static void QueryElements(
            UnityEngine.UIElements.VisualElement element, string path, string name, string className,
            string typeName, string text, bool includeStyle, int maxResults, List<Dictionary<string, object>> results)
        {
            if (results.Count >= maxResults)
                return;

            if (MatchesElement(element, name, className, typeName, text))
                results.Add(BuildElementInfo(element, path, includeStyle));

            int childIndex = 0;
            foreach (var child in element.Children())
            {
                QueryElements(child, $"{path}/{childIndex}", name, className, typeName, text,
                    includeStyle, maxResults, results);
                if (results.Count >= maxResults)
                    return;
                childIndex++;
            }
        }

        private static bool MatchesElement(UnityEngine.UIElements.VisualElement element, string name,
            string className, string typeName, string text)
        {
            if (!string.IsNullOrEmpty(name) && !string.Equals(element.name, name, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(className) && !element.ClassListContains(className))
                return false;

            if (!string.IsNullOrEmpty(typeName) &&
                !Matches(element.GetType().Name, typeName, false) &&
                !Matches(element.GetType().FullName ?? "", typeName, false))
                return false;

            if (!string.IsNullOrEmpty(text) &&
                GetElementText(element).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return true;
        }

        private static UnityEngine.UIElements.VisualElement FindElement(
            Dictionary<string, object> args, EditorWindow window, out string error)
        {
            error = "";
            string path = GetString(args, "path");
            if (!string.IsNullOrEmpty(path))
            {
                var element = GetElementByPath(window.rootVisualElement, path);
                if (element != null)
                    return element;

                error = $"UI Toolkit element path '{path}' was not found";
                return null;
            }

            string name = GetString(args, "name");
            string className = GetString(args, "className");
            string typeName = GetString(args, "typeName");
            string text = GetString(args, "text");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(className) &&
                string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(text))
                return window.rootVisualElement;

            var results = new List<Dictionary<string, object>>();
            QueryElements(window.rootVisualElement, "root", name, className, typeName, text, false, 1, results);
            if (results.Count == 0)
            {
                error = "No UI Toolkit element matched the supplied query filters";
                return null;
            }

            return GetElementByPath(window.rootVisualElement, results[0]["path"].ToString());
        }

        private static UnityEngine.UIElements.VisualElement GetElementByPath(
            UnityEngine.UIElements.VisualElement root, string path)
        {
            if (root == null)
                return null;

            if (string.IsNullOrEmpty(path) || string.Equals(path, "root", StringComparison.OrdinalIgnoreCase))
                return root;

            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            foreach (var part in parts)
            {
                if (string.Equals(part, "root", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!int.TryParse(part, out int index))
                    return null;

                int currentIndex = 0;
                UnityEngine.UIElements.VisualElement next = null;
                foreach (var child in current.Children())
                {
                    if (currentIndex == index)
                    {
                        next = child;
                        break;
                    }

                    currentIndex++;
                }

                if (next == null)
                    return null;

                current = next;
            }

            return current;
        }

        private static Dictionary<string, object> BuildInlineStyleInfo(UnityEngine.UIElements.VisualElement element)
        {
            var style = element.style;
            return new Dictionary<string, object>
            {
                { "display", style.display.ToString() },
                { "visibility", style.visibility.ToString() },
                { "position", style.position.ToString() },
                { "left", style.left.ToString() },
                { "top", style.top.ToString() },
                { "right", style.right.ToString() },
                { "bottom", style.bottom.ToString() },
                { "width", style.width.ToString() },
                { "height", style.height.ToString() },
                { "minWidth", style.minWidth.ToString() },
                { "minHeight", style.minHeight.ToString() },
                { "maxWidth", style.maxWidth.ToString() },
                { "maxHeight", style.maxHeight.ToString() },
                { "flexGrow", style.flexGrow.ToString() },
                { "flexShrink", style.flexShrink.ToString() },
                { "flexBasis", style.flexBasis.ToString() },
                { "flexDirection", style.flexDirection.ToString() },
                { "alignItems", style.alignItems.ToString() },
                { "alignSelf", style.alignSelf.ToString() },
                { "justifyContent", style.justifyContent.ToString() },
                { "marginLeft", style.marginLeft.ToString() },
                { "marginTop", style.marginTop.ToString() },
                { "marginRight", style.marginRight.ToString() },
                { "marginBottom", style.marginBottom.ToString() },
                { "paddingLeft", style.paddingLeft.ToString() },
                { "paddingTop", style.paddingTop.ToString() },
                { "paddingRight", style.paddingRight.ToString() },
                { "paddingBottom", style.paddingBottom.ToString() },
                { "backgroundColor", style.backgroundColor.ToString() },
                { "unityBackgroundImageTintColor", style.unityBackgroundImageTintColor.ToString() },
                { "color", style.color.ToString() },
                { "opacity", style.opacity.ToString() },
            };
        }

        private static Dictionary<string, object> BuildResolvedStyleInfo(UnityEngine.UIElements.VisualElement element)
        {
            var style = element.resolvedStyle;
            return new Dictionary<string, object>
            {
                { "display", style.display.ToString() },
                { "visibility", style.visibility.ToString() },
                { "position", style.position.ToString() },
                { "left", style.left },
                { "top", style.top },
                { "right", style.right },
                { "bottom", style.bottom },
                { "width", style.width },
                { "height", style.height },
                { "minWidth", style.minWidth.ToString() },
                { "minHeight", style.minHeight.ToString() },
                { "maxWidth", style.maxWidth.ToString() },
                { "maxHeight", style.maxHeight.ToString() },
                { "flexGrow", style.flexGrow },
                { "flexShrink", style.flexShrink },
                { "flexBasis", style.flexBasis.ToString() },
                { "flexDirection", style.flexDirection.ToString() },
                { "alignItems", style.alignItems.ToString() },
                { "alignSelf", style.alignSelf.ToString() },
                { "justifyContent", style.justifyContent.ToString() },
                { "marginLeft", style.marginLeft },
                { "marginTop", style.marginTop },
                { "marginRight", style.marginRight },
                { "marginBottom", style.marginBottom },
                { "paddingLeft", style.paddingLeft },
                { "paddingTop", style.paddingTop },
                { "paddingRight", style.paddingRight },
                { "paddingBottom", style.paddingBottom },
                { "backgroundColor", style.backgroundColor.ToString() },
                { "unityBackgroundImageTintColor", style.unityBackgroundImageTintColor.ToString() },
                { "color", style.color.ToString() },
                { "opacity", style.opacity },
            };
        }

        private static Dictionary<string, object> RectToDictionary(Rect rect)
        {
            return new Dictionary<string, object>
            {
                { "x", rect.x },
                { "y", rect.y },
                { "width", rect.width },
                { "height", rect.height },
            };
        }

        private static string GetElementText(UnityEngine.UIElements.VisualElement element)
        {
            if (element is UnityEngine.UIElements.TextElement textElement)
                return textElement.text ?? "";

            return "";
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.ContainsKey(key) && args[key] != null ? args[key].ToString() : "";
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            if (args[key] is bool value)
                return value;

            return bool.TryParse(args[key].ToString(), out bool parsed) ? parsed : defaultValue;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            return int.TryParse(args[key].ToString(), out int parsed) ? parsed : defaultValue;
        }
    }
}
