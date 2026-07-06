using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
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

        public static object InspectUIToolkitAsset(Dictionary<string, object> args)
        {
            string uxmlPath = NormalizeAssetPath(GetString(args, "uxmlPath"), "");
            if (string.IsNullOrEmpty(uxmlPath))
                uxmlPath = NormalizeAssetPath(GetString(args, "assetPath"), "");
            if (string.IsNullOrEmpty(uxmlPath))
                uxmlPath = NormalizeAssetPath(GetString(args, "path"), "");

            var ussPaths = GetStringList(args, "ussPaths", "ussPath")
                .Select(path => NormalizeAssetPath(path, uxmlPath))
                .Where(path => string.IsNullOrEmpty(path) == false)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string requestedName = GetString(args, "name");
            var requestedNames = GetStringList(args, "names", "name")
                .Where(name => string.IsNullOrEmpty(name) == false)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string className = GetString(args, "className");
            string typeName = GetString(args, "typeName");
            int maxResults = Math.Max(1, GetInt(args, "maxResults", 100));
            bool includeUss = GetBool(args, "includeUss", true);

            var elements = new List<UxmlElementInfo>();
            var styleReferences = new List<string>();
            string uxmlReadError = "";

            if (string.IsNullOrEmpty(uxmlPath) == false)
            {
                string absoluteUxmlPath = GetAbsoluteAssetPath(uxmlPath);
                if (!File.Exists(absoluteUxmlPath))
                    return new { error = $"UXML asset not found at '{uxmlPath}'" };

                try
                {
                    var document = XDocument.Load(absoluteUxmlPath, LoadOptions.SetLineInfo);
                    if (document.Root != null)
                        CollectUxmlElements(document.Root, "root", elements, styleReferences);
                }
                catch (Exception ex)
                {
                    uxmlReadError = ex.Message;
                }
            }

            foreach (string styleReference in styleReferences)
            {
                string resolvedPath = NormalizeAssetPath(styleReference, uxmlPath);
                if (string.IsNullOrEmpty(resolvedPath) == false &&
                    ussPaths.Contains(resolvedPath, StringComparer.OrdinalIgnoreCase) == false)
                {
                    ussPaths.Add(resolvedPath);
                }
            }

            var ussClassStyles = includeUss ? ReadUssClassStyles(ussPaths) : new Dictionary<string, UssClassStyle>();

            var filteredElements = elements
                .Where(element => ElementMatches(element, requestedName, className, typeName))
                .Take(maxResults)
                .Select(element => BuildUxmlElementDictionary(element, ussClassStyles))
                .ToList();

            var nameChecks = new List<Dictionary<string, object>>();
            foreach (string name in requestedNames)
            {
                var matches = elements.Where(element => string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                bool typeMatches = string.IsNullOrEmpty(typeName) ||
                    matches.Any(element => TypeMatches(element.TypeName, typeName));
                nameChecks.Add(new Dictionary<string, object>
                {
                    { "name", name },
                    { "exists", matches.Count > 0 },
                    { "matchCount", matches.Count },
                    { "typeMatches", typeMatches },
                    { "matches", matches.Take(maxResults).Select(element => BuildUxmlElementDictionary(element, ussClassStyles)).ToList() },
                });
            }

            return new Dictionary<string, object>
            {
                { "success", string.IsNullOrEmpty(uxmlReadError) },
                { "uxmlPath", uxmlPath },
                { "uxmlReadError", uxmlReadError },
                { "ussPaths", ussPaths },
                { "elementCount", elements.Count },
                { "query", new Dictionary<string, object>
                    {
                        { "name", requestedName },
                        { "names", requestedNames },
                        { "className", className },
                        { "typeName", typeName },
                    }
                },
                { "valid", string.IsNullOrEmpty(uxmlReadError) &&
                    (requestedNames.Count == 0 || nameChecks.All(check =>
                        Convert.ToBoolean(check["exists"]) && Convert.ToBoolean(check["typeMatches"]))) },
                { "nameChecks", nameChecks },
                { "matchedCount", filteredElements.Count },
                { "elements", filteredElements },
                { "ussClasses", ussClassStyles.ToDictionary(pair => pair.Key, pair => pair.Value.ToDictionary()) },
            };
        }

        public static object ListRuntimeUIDocuments(Dictionary<string, object> args)
        {
            bool includeInactive = GetBool(args, "includeInactive", true);
            var documents = GetRuntimeUIDocuments(includeInactive)
                .Select(BuildUIDocumentInfo)
                .ToList();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "count", documents.Count },
                { "documents", documents },
            };
        }

        public static object GetRuntimeUITree(Dictionary<string, object> args)
        {
            var document = FindRuntimeUIDocument(args, out string error);
            if (document == null)
                return new { error };

            var root = document.rootVisualElement;
            if (root == null)
                return new { error = $"UIDocument '{document.name}' has no rootVisualElement" };

            int maxDepth = GetInt(args, "maxDepth", 8);
            int maxNodes = GetInt(args, "maxNodes", 300);
            bool includeStyle = GetBool(args, "includeStyle", false);
            int count = 0;
            bool truncated = false;
            var tree = BuildElementTree(root, "root", 0, maxDepth, maxNodes, includeStyle, ref count, ref truncated);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "document", BuildUIDocumentInfo(document) },
                { "nodeCount", count },
                { "truncated", truncated },
                { "tree", tree },
            };
        }

        public static object QueryRuntimeUI(Dictionary<string, object> args)
        {
            var document = FindRuntimeUIDocument(args, out string error);
            if (document == null)
                return new { error };

            var root = document.rootVisualElement;
            if (root == null)
                return new { error = $"UIDocument '{document.name}' has no rootVisualElement" };

            bool includeStyle = GetBool(args, "includeStyle", false);
            var element = FindRuntimeElement(args, document, out string elementPath, out error);
            if (element != null && HasElementLocator(args))
            {
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "document", BuildUIDocumentInfo(document) },
                    { "count", 1 },
                    { "results", new List<Dictionary<string, object>>
                        {
                            BuildElementInfo(element, elementPath, includeStyle),
                        }
                    },
                };
            }

            string name = GetString(args, "name");
            string className = GetString(args, "className");
            string typeName = GetString(args, "typeName");
            string text = GetString(args, "text");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(className) &&
                string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(text))
                return new { error = string.IsNullOrEmpty(error) ? "At least one query filter or path is required" : error };

            int maxResults = GetInt(args, "maxResults", 50);
            var results = new List<Dictionary<string, object>>();
            QueryElements(root, "root", name, className, typeName, text, includeStyle, maxResults, results);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "document", BuildUIDocumentInfo(document) },
                { "count", results.Count },
                { "results", results },
            };
        }

        public static object GetRuntimeUIStyle(Dictionary<string, object> args)
        {
            var document = FindRuntimeUIDocument(args, out string error);
            if (document == null)
                return new { error };

            var element = FindRuntimeElement(args, document, out string elementPath, out error);
            if (element == null)
                return new { error };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "document", BuildUIDocumentInfo(document) },
                { "element", BuildElementInfo(element, elementPath, false) },
                { "inlineStyle", BuildInlineStyleInfo(element) },
                { "resolvedStyle", BuildResolvedStyleInfo(element) },
                { "background", BuildBackgroundInfo(element) },
            };
        }

        public static object DiagnoseRuntimeUI(Dictionary<string, object> args)
        {
            var document = FindRuntimeUIDocument(args, out string error);
            if (document == null)
                return new { error };

            var root = document.rootVisualElement;
            if (root == null)
                return new { error = $"UIDocument '{document.name}' has no rootVisualElement" };

            var checks = new List<Dictionary<string, object>>();
            var queryObjects = GetObjectList(args, "queries");
            if (queryObjects.Count == 0)
                queryObjects.Add(args);

            for (int i = 0; i < queryObjects.Count; i++)
            {
                var query = AsDictionary(queryObjects[i]);
                var element = FindRuntimeElement(query, document, out string elementPath, out string elementError);
                var check = new Dictionary<string, object>
                {
                    { "index", i },
                    { "query", query },
                    { "found", element != null },
                    { "path", elementPath },
                    { "error", element == null ? elementError : "" },
                };

                if (element != null)
                {
                    check["element"] = BuildElementInfo(element, elementPath, true);
                    check["parent"] = element.parent == null
                        ? null
                        : BuildElementInfo(element.parent, GetElementPath(root, element.parent), false);
                    check["children"] = element.Children()
                        .Select(child => BuildElementInfo(child, GetElementPath(root, child), false))
                        .ToList();
                    check["pixel"] = BuildPixelInfo(element, GetFloat(query, "pixelScale",
                        GetFloat(args, "pixelScale", 1f)));
                    check["backgroundScale"] = BuildBackgroundScaleInfo(element);
                }

                checks.Add(check);
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "document", BuildUIDocumentInfo(document) },
                { "valid", checks.All(check => GetBool(check, "found", false)) },
                { "count", checks.Count },
                { "checks", checks },
            };
        }

        public static object VisualCheckRuntimeUI(Dictionary<string, object> args)
        {
            var document = FindRuntimeUIDocument(args, out string error);
            if (document == null)
                return new { error };

            var root = document.rootVisualElement;
            if (root == null)
                return new { error = $"UIDocument '{document.name}' has no rootVisualElement" };

            var checkArgs = GetObjectList(args, "checks");
            if (checkArgs.Count == 0)
                checkArgs.Add(args);

            float defaultPixelScale = GetFloat(args, "pixelScale", 1f);
            float defaultTolerance = GetFloat(args, "tolerance", 0.01f);
            var results = new List<Dictionary<string, object>>();
            bool valid = true;

            for (int i = 0; i < checkArgs.Count; i++)
            {
                var check = AsDictionary(checkArgs[i]);
                string kind = GetString(check, "type");
                if (string.IsNullOrEmpty(kind))
                    kind = GetString(check, "kind");
                if (string.IsNullOrEmpty(kind))
                    kind = "pixel-grid";

                var element = FindAssertionElement(root, check, "", out string path, out string elementError);
                var result = new Dictionary<string, object>
                {
                    { "index", i },
                    { "type", kind },
                    { "path", path },
                };

                if (element == null)
                {
                    result["passed"] = false;
                    result["error"] = elementError;
                    results.Add(result);
                    valid = false;
                    continue;
                }

                switch (kind.ToLowerInvariant())
                {
                    case "pixel-grid":
                    case "pixel":
                        AddPixelGridResult(result, element,
                            GetFloat(check, "pixelScale", defaultPixelScale),
                            GetFloat(check, "tolerance", defaultTolerance));
                        break;
                    case "background-scale":
                    case "sprite-scale":
                    case "texture-scale":
                        AddBackgroundScaleResult(result, element,
                            GetFloat(check, "expectedScale", GetFloat(check, "scale", defaultPixelScale)),
                            GetFloat(check, "tolerance", defaultTolerance));
                        break;
                    case "size":
                        AddRuntimeSizeResult(result, element, check, defaultTolerance);
                        break;
                    default:
                        result["passed"] = false;
                        result["error"] = $"Unknown visual check type '{kind}'";
                        break;
                }

                if (GetBool(result, "passed", false) == false)
                    valid = false;

                results.Add(result);
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "valid", valid },
                { "document", BuildUIDocumentInfo(document) },
                { "count", results.Count },
                { "results", results },
            };
        }

        public static object RepaintRuntimeUI(Dictionary<string, object> args)
        {
            var document = FindRuntimeUIDocument(args, out string error);
            if (document == null)
                return new { error };

            var element = FindRuntimeElement(args, document, out string elementPath, out error);
            if (element == null && HasElementLocator(args))
                return new { error };

            if (element != null)
                element.MarkDirtyRepaint();

            document.rootVisualElement?.MarkDirtyRepaint();
            EditorApplication.QueuePlayerLoopUpdate();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "document", BuildUIDocumentInfo(document) },
                { "repaintedPath", element == null ? "root" : elementPath },
            };
        }

        public static object RefreshUIToolkit(Dictionary<string, object> args)
        {
            bool refreshAssets = GetBool(args, "refreshAssets", true);
            bool forceSynchronousImport = GetBool(args, "forceSynchronousImport", true);
            if (refreshAssets)
            {
                var options = forceSynchronousImport
                    ? ImportAssetOptions.ForceSynchronousImport
                    : ImportAssetOptions.Default;
                AssetDatabase.Refresh(options);
            }

            int documentCount = MarkAllUIToolkitDirty();
            EditorApplication.QueuePlayerLoopUpdate();

            return BuildUIToolkitRefreshResult(true, false, 0, 0, documentCount);
        }

        public static void WaitForUIToolkitRefresh(Dictionary<string, object> args, Action<object> resolve)
        {
            bool refreshAssets = GetBool(args, "refreshAssets", true);
            bool forceSynchronousImport = GetBool(args, "forceSynchronousImport", true);
            if (refreshAssets)
            {
                var options = forceSynchronousImport
                    ? ImportAssetOptions.ForceSynchronousImport
                    : ImportAssetOptions.Default;
                AssetDatabase.Refresh(options);
            }

            int timeoutMs = Math.Max(1, GetInt(args, "timeoutMs", 10000));
            int stableFrames = Math.Max(1, GetInt(args, "stableFrames", 2));
            double startTime = EditorApplication.timeSinceStartup;
            int frameCount = 0;
            int stableFrameCount = 0;
            bool resolved = false;

            void Resolve(object result)
            {
                if (resolved)
                    return;

                resolved = true;
                resolve(result);
            }

            void Tick()
            {
                frameCount++;
                int documentCount = MarkAllUIToolkitDirty();
                bool idle = !EditorApplication.isCompiling && !EditorApplication.isUpdating;
                stableFrameCount = idle ? stableFrameCount + 1 : 0;
                double elapsedMs = (EditorApplication.timeSinceStartup - startTime) * 1000d;

                if (stableFrameCount >= stableFrames)
                {
                    EditorApplication.update -= Tick;
                    Resolve(BuildUIToolkitRefreshResult(true, false, elapsedMs, frameCount, documentCount));
                    return;
                }

                if (elapsedMs >= timeoutMs)
                {
                    EditorApplication.update -= Tick;
                    Resolve(BuildUIToolkitRefreshResult(false, true, elapsedMs, frameCount, documentCount));
                }
            }

            Tick();
            if (!resolved)
                EditorApplication.update += Tick;
        }

        public static void OpenUIBuilderPreview(Dictionary<string, object> args, Action<object> resolve)
        {
            string uxmlPath = NormalizeAssetPath(GetString(args, "uxmlPath"), "");
            if (string.IsNullOrEmpty(uxmlPath))
                uxmlPath = NormalizeAssetPath(GetString(args, "assetPath"), "");
            if (string.IsNullOrEmpty(uxmlPath))
                uxmlPath = NormalizeAssetPath(GetString(args, "path"), "");
            if (string.IsNullOrEmpty(uxmlPath))
            {
                resolve(new { error = "uxmlPath, assetPath, or path is required" });
                return;
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(uxmlPath);
            if (asset == null)
            {
                resolve(new { error = $"VisualTreeAsset not found at '{uxmlPath}'" });
                return;
            }

            bool opened = AssetDatabase.OpenAsset(asset);
            int waitFrames = Math.Max(1, GetInt(args, "waitFrames", 8));
            bool capture = GetBool(args, "capture", true);
            string screenshotPath = GetString(args, "screenshotPath");
            if (string.IsNullOrEmpty(screenshotPath))
            {
                string safeName = Path.GetFileNameWithoutExtension(uxmlPath).Replace(' ', '_');
                screenshotPath = "Assets/Screenshots/UIBuilder_" + safeName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            }

            int frame = 0;
            void Tick()
            {
                frame++;
                if (frame < waitFrames)
                    return;

                EditorApplication.update -= Tick;
                int repainted = MarkAllUIToolkitDirty();
                var window = FindUIBuilderWindow();
                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "uxmlPath", uxmlPath },
                    { "opened", opened },
                    { "waitFrames", waitFrames },
                    { "repaintedRuntimeDocuments", repainted },
                    { "windowFound", window != null },
                    { "window", window == null ? null : BuildWindowInfo(window) },
                };

                if (args.ContainsKey("zoom"))
                {
                    result["requestedZoom"] = GetFloat(args, "zoom", 1);
                    result["zoomApplied"] = false;
                    result["zoomNote"] = "UI Builder zoom is not exposed through a stable public Unity API; the window is opened and captured instead.";
                }

                if (capture)
                {
                    result["screenshot"] = MCPScreenshotCommands.CaptureEditorWindow(new Dictionary<string, object>
                    {
                        { "window", "UI Builder" },
                        { "path", screenshotPath },
                        { "maxDimension", GetInt(args, "maxDimension", 8192) },
                    });
                }

                resolve(result);
            }

            EditorApplication.update += Tick;
        }

        public static object OpenUIBuilderPreview(Dictionary<string, object> args)
        {
            return new { error = "uitoolkit/builder-preview must be executed through the deferred route." };
        }

        public static object AssertUIToolkitLayout(Dictionary<string, object> args)
        {
            var document = FindRuntimeUIDocument(args, out string error);
            if (document == null)
                return new { error };

            var root = document.rootVisualElement;
            if (root == null)
                return new { error = $"UIDocument '{document.name}' has no rootVisualElement" };

            var assertionArgs = GetObjectList(args, "assertions");
            if (assertionArgs.Count == 0)
                return new { error = "assertions array is required" };

            var results = new List<Dictionary<string, object>>();
            bool valid = true;

            for (int i = 0; i < assertionArgs.Count; i++)
            {
                var assertion = AsDictionary(assertionArgs[i]);
                var result = BuildLayoutAssertion(root, assertion, i);
                results.Add(result);
                if (!GetBool(result, "passed", false))
                    valid = false;
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "valid", valid },
                { "document", BuildUIDocumentInfo(document) },
                { "count", results.Count },
                { "results", results },
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

        private static Dictionary<string, object> BuildUIDocumentInfo(UnityEngine.UIElements.UIDocument document)
        {
            var root = document != null ? document.rootVisualElement : null;
            var visualTreeAsset = document != null ? document.visualTreeAsset : null;
            var panelSettings = document != null ? document.panelSettings : null;

            return new Dictionary<string, object>
            {
                { "instanceId", document != null ? document.GetInstanceID() : 0 },
                { "name", document != null ? document.name : "" },
                { "enabled", document != null && document.enabled },
                { "gameObjectName", document != null ? document.gameObject.name : "" },
                { "gameObjectPath", document != null ? GetGameObjectPath(document.transform) : "" },
                { "gameObjectActive", document != null && document.gameObject.activeInHierarchy },
                { "visualTreeAsset", visualTreeAsset != null ? visualTreeAsset.name : "" },
                { "visualTreeAssetPath", visualTreeAsset != null ? AssetDatabase.GetAssetPath(visualTreeAsset) : "" },
                { "panelSettings", panelSettings != null ? panelSettings.name : "" },
                { "panelSettingsPath", panelSettings != null ? AssetDatabase.GetAssetPath(panelSettings) : "" },
                { "hasRootVisualElement", root != null },
                { "rootChildCount", root?.childCount ?? 0 },
                { "rootWorldBound", root != null ? RectToDictionary(root.worldBound) : null },
            };
        }

        private static List<UnityEngine.UIElements.UIDocument> GetRuntimeUIDocuments(bool includeInactive)
        {
            return Resources.FindObjectsOfTypeAll<UnityEngine.UIElements.UIDocument>()
                .Where(document => document != null &&
                                   document.gameObject != null &&
                                   document.gameObject.scene.IsValid() &&
                                   (includeInactive || (document.enabled && document.gameObject.activeInHierarchy)))
                .OrderBy(document => GetGameObjectPath(document.transform), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static UnityEngine.UIElements.UIDocument FindRuntimeUIDocument(
            Dictionary<string, object> args, out string error)
        {
            error = "";
            bool includeInactive = GetBool(args, "includeInactive", true);
            int instanceId = GetInt(args, "documentInstanceId", 0);
            if (instanceId == 0)
                instanceId = GetInt(args, "uidocumentInstanceId", 0);
            if (instanceId == 0)
                instanceId = GetInt(args, "instanceId", 0);

            string documentName = GetString(args, "documentName");
            string gameObjectPath = GetString(args, "gameObjectPath");
            string gameObjectName = GetString(args, "gameObjectName");

            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is UnityEngine.UIElements.UIDocument directDocument)
                    return directDocument;
                if (obj is GameObject go)
                {
                    var component = go.GetComponent<UnityEngine.UIElements.UIDocument>();
                    if (component != null)
                        return component;
                }

                error = $"UIDocument or GameObject instanceId '{instanceId}' was not found";
                return null;
            }

            var documents = GetRuntimeUIDocuments(includeInactive);
            if (string.IsNullOrEmpty(gameObjectPath) == false)
            {
                string normalizedPath = NormalizeGameObjectPath(gameObjectPath);
                documents = documents
                    .Where(document => string.Equals(GetGameObjectPath(document.transform), normalizedPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (string.IsNullOrEmpty(gameObjectName) == false)
            {
                documents = documents
                    .Where(document => string.Equals(document.gameObject.name, gameObjectName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (string.IsNullOrEmpty(documentName) == false)
            {
                documents = documents
                    .Where(document => string.Equals(document.name, documentName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (documents.Count == 1)
                return documents[0];

            if (documents.Count == 0)
            {
                error = "No runtime UIDocument matched the supplied filters";
                return null;
            }

            if (string.IsNullOrEmpty(gameObjectPath) && string.IsNullOrEmpty(gameObjectName) &&
                string.IsNullOrEmpty(documentName))
            {
                var activeDocuments = documents
                    .Where(document => document.enabled && document.gameObject.activeInHierarchy)
                    .ToList();
                if (activeDocuments.Count > 0)
                    return activeDocuments[0];
            }

            error = $"Multiple runtime UIDocuments matched ({documents.Count}). Pass gameObjectPath, documentName, or documentInstanceId.";
            return null;
        }

        private static UnityEngine.UIElements.VisualElement FindRuntimeElement(
            Dictionary<string, object> args, UnityEngine.UIElements.UIDocument document,
            out string elementPath, out string error)
        {
            elementPath = "root";
            error = "";

            var root = document.rootVisualElement;
            if (root == null)
            {
                error = $"UIDocument '{document.name}' has no rootVisualElement";
                return null;
            }

            string path = GetString(args, "path");
            if (string.IsNullOrEmpty(path))
                path = GetString(args, "treePath");
            if (string.IsNullOrEmpty(path) == false)
            {
                var element = GetElementByFlexiblePath(root, path);
                if (element != null)
                {
                    elementPath = GetElementPath(root, element);
                    return element;
                }

                error = $"UI Toolkit element path '{path}' was not found";
                return null;
            }

            var visualElementPath = GetVisualElementPathNames(args, "");
            if (visualElementPath.Count > 0)
            {
                var element = GetElementByVisualElementPath(root, visualElementPath);
                if (element != null)
                {
                    elementPath = GetElementPath(root, element);
                    return element;
                }

                error = $"VisualElementPath '{string.Join("/", visualElementPath)}' was not found";
                return null;
            }

            string name = GetString(args, "name");
            string className = GetString(args, "className");
            string typeName = GetString(args, "typeName");
            string text = GetString(args, "text");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(className) &&
                string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(text))
                return root;

            var results = new List<Dictionary<string, object>>();
            QueryElements(root, "root", name, className, typeName, text, false, 1, results);
            if (results.Count == 0)
            {
                error = "No UI Toolkit element matched the supplied query filters";
                return null;
            }

            elementPath = results[0]["path"].ToString();
            return GetElementByPath(root, elementPath);
        }

        private static bool HasElementLocator(Dictionary<string, object> args)
        {
            return string.IsNullOrEmpty(GetString(args, "path")) == false ||
                   string.IsNullOrEmpty(GetString(args, "treePath")) == false ||
                   string.IsNullOrEmpty(GetString(args, "visualElementPath")) == false ||
                   string.IsNullOrEmpty(GetString(args, "namePath")) == false ||
                   GetStringList(args, "visualElementNames", "").Count > 0 ||
                   GetStringList(args, "names", "").Count > 0;
        }

        private static int MarkAllUIToolkitDirty()
        {
            int documentCount = 0;
            foreach (var document in GetRuntimeUIDocuments(true))
            {
                if (document.rootVisualElement == null)
                    continue;

                document.rootVisualElement.MarkDirtyRepaint();
                documentCount++;
            }

            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>().Where(window => window != null))
            {
                window.rootVisualElement?.MarkDirtyRepaint();
                window.Repaint();
            }

            SceneView.RepaintAll();
            return documentCount;
        }

        private static Dictionary<string, object> BuildUIToolkitRefreshResult(
            bool success, bool timedOut, double elapsedMs, int frameCount, int documentCount)
        {
            return new Dictionary<string, object>
            {
                { "success", success },
                { "timedOut", timedOut },
                { "elapsedMs", Math.Round(elapsedMs, 2) },
                { "frameCount", frameCount },
                { "repaintedRuntimeDocuments", documentCount },
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating },
            };
        }

        private static string GetGameObjectPath(Transform transform)
        {
            if (transform == null)
                return "";

            var names = new List<string>();
            var current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static string NormalizeGameObjectPath(string path)
        {
            return (path ?? "").Trim().Trim('/').Replace('\\', '/');
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

        private static EditorWindow FindUIBuilderWindow()
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>().Where(window => window != null).ToList();
            foreach (var window in windows)
            {
                string title = window.titleContent?.text ?? "";
                if (string.Equals(title, "UI Builder", StringComparison.OrdinalIgnoreCase))
                    return window;
            }

            foreach (var window in windows)
            {
                string title = window.titleContent?.text ?? "";
                string typeName = window.GetType().FullName ?? window.GetType().Name;
                if (title.IndexOf("UI Builder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("UIBuilder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Builder", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return window;
                }
            }

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
                info["background"] = BuildBackgroundInfo(element);
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

        private static UnityEngine.UIElements.VisualElement GetElementByFlexiblePath(
            UnityEngine.UIElements.VisualElement root, string path)
        {
            var element = GetElementByPath(root, path);
            if (element != null)
                return element;

            var names = SplitVisualElementPath(path);
            return names.Count > 0 ? GetElementByVisualElementPath(root, names) : null;
        }

        private static UnityEngine.UIElements.VisualElement GetElementByVisualElementPath(
            UnityEngine.UIElements.VisualElement root, List<string> names)
        {
            if (root == null || names == null || names.Count == 0)
                return null;

            var current = FindNamedElement(root, names[0], true);
            for (int i = 1; i < names.Count && current != null; i++)
            {
                current = FindNamedElement(current, names[i], false);
            }

            return current;
        }

        private static UnityEngine.UIElements.VisualElement FindNamedElement(
            UnityEngine.UIElements.VisualElement root, string name, bool includeRoot)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;

            if (includeRoot && string.Equals(root.name, name, StringComparison.Ordinal))
                return root;

            foreach (var child in root.Children())
            {
                var result = FindNamedElement(child, name, true);
                if (result != null)
                    return result;
            }

            return null;
        }

        private static List<string> GetVisualElementPathNames(Dictionary<string, object> args, string prefix)
        {
            var names = new List<string>();
            if (args == null)
                return names;

            string visualElementPathKey = string.IsNullOrEmpty(prefix) ? "visualElementPath" : $"{prefix}VisualElementPath";
            if (args.TryGetValue(visualElementPathKey, out object pathValue))
                AddVisualElementPathNames(names, pathValue);

            string namePathKey = string.IsNullOrEmpty(prefix) ? "namePath" : $"{prefix}NamePath";
            AddVisualElementPathNames(names, GetString(args, namePathKey));

            string namesKey = string.IsNullOrEmpty(prefix) ? "visualElementNames" : $"{prefix}Names";
            foreach (string name in GetStringList(args, namesKey, ""))
                AddVisualElementPathNames(names, name);

            if (string.IsNullOrEmpty(prefix))
            {
                foreach (string name in GetStringList(args, "names", ""))
                    AddVisualElementPathNames(names, name);
            }

            return names.Where(name => string.IsNullOrEmpty(name) == false).ToList();
        }

        private static void AddVisualElementPathNames(List<string> names, object value)
        {
            if (value == null)
                return;

            if (value is List<object> list)
            {
                foreach (object item in list)
                    AddVisualElementPathNames(names, item);
                return;
            }

            if (value is Dictionary<string, object> dictionary)
            {
                foreach (string name in GetStringList(dictionary, "names", "name"))
                    AddVisualElementPathNames(names, name);
                return;
            }

            foreach (string name in SplitVisualElementPath(value.ToString()))
            {
                if (names.Contains(name, StringComparer.Ordinal) == false)
                    names.Add(name);
            }
        }

        private static List<string> SplitVisualElementPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new List<string>();

            return path.Split(new[] { '/', '\\', '>', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => string.IsNullOrEmpty(part) == false &&
                               string.Equals(part, "root", StringComparison.OrdinalIgnoreCase) == false)
                .ToList();
        }

        private static string GetElementPath(UnityEngine.UIElements.VisualElement root,
            UnityEngine.UIElements.VisualElement target)
        {
            if (root == null || target == null)
                return "";

            if (root == target)
                return "root";

            var indexes = new List<int>();
            if (TryBuildElementPath(root, target, indexes) == false)
                return "";

            return "root/" + string.Join("/", indexes);
        }

        private static bool TryBuildElementPath(UnityEngine.UIElements.VisualElement current,
            UnityEngine.UIElements.VisualElement target, List<int> indexes)
        {
            int childIndex = 0;
            foreach (var child in current.Children())
            {
                indexes.Add(childIndex);
                if (child == target || TryBuildElementPath(child, target, indexes))
                    return true;

                indexes.RemoveAt(indexes.Count - 1);
                childIndex++;
            }

            return false;
        }

        private static Dictionary<string, object> BuildLayoutAssertion(
            UnityEngine.UIElements.VisualElement root, Dictionary<string, object> assertion, int index)
        {
            string type = GetString(assertion, "type");
            if (string.IsNullOrEmpty(type))
                type = GetString(assertion, "kind");
            if (string.IsNullOrEmpty(type))
                type = "edge-touch";

            try
            {
                switch (type.ToLowerInvariant())
                {
                    case "edge-touch":
                    case "touch":
                    case "no-gap-no-overlap":
                        return BuildEdgeTouchAssertion(root, assertion, index, type);
                    case "inside":
                    case "contained":
                        return BuildInsideAssertion(root, assertion, index, type);
                    case "size":
                        return BuildSizeAssertion(root, assertion, index, type);
                    default:
                        return new Dictionary<string, object>
                        {
                            { "index", index },
                            { "type", type },
                            { "passed", false },
                            { "error", $"Unknown assertion type '{type}'" },
                        };
                }
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "index", index },
                    { "type", type },
                    { "passed", false },
                    { "error", ex.Message },
                };
            }
        }

        private static Dictionary<string, object> BuildEdgeTouchAssertion(
            UnityEngine.UIElements.VisualElement root, Dictionary<string, object> assertion, int index, string type)
        {
            var first = FindAssertionElement(root, assertion, "first", out string firstPath, out string firstError);
            var second = FindAssertionElement(root, assertion, "second", out string secondPath, out string secondError);
            float tolerance = GetFloat(assertion, "tolerance", 0.5f);

            if (first == null || second == null)
            {
                return new Dictionary<string, object>
                {
                    { "index", index },
                    { "type", type },
                    { "passed", false },
                    { "error", first == null ? firstError : secondError },
                };
            }

            string axis = GetString(assertion, "axis").ToLowerInvariant();
            if (axis != "y")
                axis = "x";

            string firstEdge = GetString(assertion, "firstEdge");
            string secondEdge = GetString(assertion, "secondEdge");
            if (string.IsNullOrEmpty(firstEdge))
                firstEdge = axis == "x" ? "right" : "bottom";
            if (string.IsNullOrEmpty(secondEdge))
                secondEdge = axis == "x" ? "left" : "top";

            float firstValue = GetRectEdge(first.worldBound, firstEdge);
            float secondValue = GetRectEdge(second.worldBound, secondEdge);
            float delta = secondValue - firstValue;
            bool passed = Math.Abs(delta) <= tolerance;

            return new Dictionary<string, object>
            {
                { "index", index },
                { "type", type },
                { "passed", passed },
                { "axis", axis },
                { "firstPath", firstPath },
                { "secondPath", secondPath },
                { "firstEdge", firstEdge },
                { "secondEdge", secondEdge },
                { "firstValue", SafeFloat(firstValue) },
                { "secondValue", SafeFloat(secondValue) },
                { "delta", SafeFloat(delta) },
                { "gap", SafeFloat(delta > tolerance ? delta : 0) },
                { "overlap", SafeFloat(delta < -tolerance ? -delta : 0) },
                { "tolerance", SafeFloat(tolerance) },
                { "firstRect", RectToDictionary(first.worldBound) },
                { "secondRect", RectToDictionary(second.worldBound) },
            };
        }

        private static Dictionary<string, object> BuildInsideAssertion(
            UnityEngine.UIElements.VisualElement root, Dictionary<string, object> assertion, int index, string type)
        {
            var inner = FindAssertionElement(root, assertion, "inner", out string innerPath, out string innerError);
            var outer = FindAssertionElement(root, assertion, "outer", out string outerPath, out string outerError);
            float tolerance = GetFloat(assertion, "tolerance", 0.5f);

            if (inner == null || outer == null)
            {
                return new Dictionary<string, object>
                {
                    { "index", index },
                    { "type", type },
                    { "passed", false },
                    { "error", inner == null ? innerError : outerError },
                };
            }

            Rect innerRect = inner.worldBound;
            Rect outerRect = outer.worldBound;
            bool passed = innerRect.xMin >= outerRect.xMin - tolerance &&
                          innerRect.yMin >= outerRect.yMin - tolerance &&
                          innerRect.xMax <= outerRect.xMax + tolerance &&
                          innerRect.yMax <= outerRect.yMax + tolerance;

            return new Dictionary<string, object>
            {
                { "index", index },
                { "type", type },
                { "passed", passed },
                { "innerPath", innerPath },
                { "outerPath", outerPath },
                { "tolerance", SafeFloat(tolerance) },
                { "leftOverflow", SafeFloat(Math.Max(0, outerRect.xMin - innerRect.xMin)) },
                { "topOverflow", SafeFloat(Math.Max(0, outerRect.yMin - innerRect.yMin)) },
                { "rightOverflow", SafeFloat(Math.Max(0, innerRect.xMax - outerRect.xMax)) },
                { "bottomOverflow", SafeFloat(Math.Max(0, innerRect.yMax - outerRect.yMax)) },
                { "innerRect", RectToDictionary(innerRect) },
                { "outerRect", RectToDictionary(outerRect) },
            };
        }

        private static Dictionary<string, object> BuildSizeAssertion(
            UnityEngine.UIElements.VisualElement root, Dictionary<string, object> assertion, int index, string type)
        {
            var element = FindAssertionElement(root, assertion, "", out string path, out string error);
            float expectedWidth = GetFloat(assertion, "width", float.NaN);
            if (float.IsNaN(expectedWidth))
                expectedWidth = GetFloat(assertion, "expectedWidth", float.NaN);
            float expectedHeight = GetFloat(assertion, "height", float.NaN);
            if (float.IsNaN(expectedHeight))
                expectedHeight = GetFloat(assertion, "expectedHeight", float.NaN);
            float tolerance = GetFloat(assertion, "tolerance", 0.5f);

            if (element == null)
            {
                return new Dictionary<string, object>
                {
                    { "index", index },
                    { "type", type },
                    { "passed", false },
                    { "error", error },
                };
            }

            Rect rect = element.worldBound;
            float widthDelta = float.IsNaN(expectedWidth) ? 0 : rect.width - expectedWidth;
            float heightDelta = float.IsNaN(expectedHeight) ? 0 : rect.height - expectedHeight;
            bool widthPassed = float.IsNaN(expectedWidth) || Math.Abs(widthDelta) <= tolerance;
            bool heightPassed = float.IsNaN(expectedHeight) || Math.Abs(heightDelta) <= tolerance;

            return new Dictionary<string, object>
            {
                { "index", index },
                { "type", type },
                { "passed", widthPassed && heightPassed },
                { "path", path },
                { "expectedWidth", float.IsNaN(expectedWidth) ? null : (object)expectedWidth },
                { "expectedHeight", float.IsNaN(expectedHeight) ? null : (object)expectedHeight },
                { "actualWidth", SafeFloat(rect.width) },
                { "actualHeight", SafeFloat(rect.height) },
                { "widthDelta", SafeFloat(widthDelta) },
                { "heightDelta", SafeFloat(heightDelta) },
                { "tolerance", SafeFloat(tolerance) },
                { "rect", RectToDictionary(rect) },
            };
        }

        private static UnityEngine.UIElements.VisualElement FindAssertionElement(
            UnityEngine.UIElements.VisualElement root, Dictionary<string, object> assertion,
            string prefix, out string path, out string error)
        {
            path = "";
            error = "";

            string pathKey = string.IsNullOrEmpty(prefix) ? "path" : $"{prefix}Path";
            string requestedPath = GetString(assertion, pathKey);
            if (string.IsNullOrEmpty(requestedPath) && string.IsNullOrEmpty(prefix))
                requestedPath = GetString(assertion, "elementPath");
            if (string.IsNullOrEmpty(requestedPath) == false)
            {
                var element = GetElementByFlexiblePath(root, requestedPath);
                if (element != null)
                {
                    path = GetElementPath(root, element);
                    return element;
                }

                error = $"Element path '{requestedPath}' was not found";
                return null;
            }

            var names = GetVisualElementPathNames(assertion, prefix);
            if (names.Count > 0)
            {
                var element = GetElementByVisualElementPath(root, names);
                if (element != null)
                {
                    path = GetElementPath(root, element);
                    return element;
                }

                error = $"VisualElementPath '{string.Join("/", names)}' was not found";
                return null;
            }

            string nameKey = string.IsNullOrEmpty(prefix) ? "name" : $"{prefix}Name";
            string name = GetString(assertion, nameKey);
            if (string.IsNullOrEmpty(name) == false)
            {
                var element = FindNamedElement(root, name, true);
                if (element != null)
                {
                    path = GetElementPath(root, element);
                    return element;
                }

                error = $"Element name '{name}' was not found";
                return null;
            }

            error = $"No element locator was supplied for prefix '{prefix}'";
            return null;
        }

        private static float GetRectEdge(Rect rect, string edge)
        {
            switch ((edge ?? "").ToLowerInvariant())
            {
                case "left":
                case "xmin":
                    return rect.xMin;
                case "right":
                case "xmax":
                    return rect.xMax;
                case "top":
                case "ymin":
                    return rect.yMin;
                case "bottom":
                case "ymax":
                    return rect.yMax;
                case "centerx":
                    return rect.center.x;
                case "centery":
                    return rect.center.y;
                default:
                    throw new ArgumentException($"Unknown rect edge '{edge}'");
            }
        }

        private static void CollectUxmlElements(XElement element, string path, List<UxmlElementInfo> elements,
            List<string> styleReferences)
        {
            string typeName = element.Name.LocalName;
            if (string.Equals(typeName, "Style", StringComparison.OrdinalIgnoreCase))
            {
                string styleSource = GetAttributeValue(element, "src");
                if (string.IsNullOrEmpty(styleSource) == false)
                    styleReferences.Add(styleSource);
            }

            var info = new UxmlElementInfo
            {
                Path = path,
                TypeName = typeName,
                FullTypeName = element.Name.ToString(),
                Name = GetAttributeValue(element, "name"),
                Classes = SplitClasses(GetAttributeValue(element, "class")),
                InlineStyle = GetAttributeValue(element, "style"),
                LineNumber = element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0,
            };
            elements.Add(info);

            int childIndex = 0;
            foreach (var child in element.Elements())
            {
                CollectUxmlElements(child, $"{path}/{childIndex}", elements, styleReferences);
                childIndex++;
            }
        }

        private static bool ElementMatches(UxmlElementInfo element, string name, string className, string typeName)
        {
            if (string.IsNullOrEmpty(name) == false &&
                !string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.IsNullOrEmpty(className) == false &&
                !element.Classes.Any(item => string.Equals(item, className, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (string.IsNullOrEmpty(typeName) == false && !TypeMatches(element.TypeName, typeName) &&
                !TypeMatches(element.FullTypeName, typeName))
                return false;

            return true;
        }

        private static bool TypeMatches(string actualType, string expectedType)
        {
            return string.IsNullOrEmpty(expectedType) ||
                (!string.IsNullOrEmpty(actualType) &&
                 actualType.IndexOf(expectedType, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static Dictionary<string, object> BuildUxmlElementDictionary(UxmlElementInfo element,
            Dictionary<string, UssClassStyle> ussClassStyles)
        {
            var resolvedDeclarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var matchedClasses = new List<string>();

            foreach (string className in element.Classes)
            {
                if (!ussClassStyles.TryGetValue(className, out var style))
                    continue;

                matchedClasses.Add(className);
                foreach (var pair in style.Declarations)
                    resolvedDeclarations[pair.Key] = pair.Value;
            }

            return new Dictionary<string, object>
            {
                { "path", element.Path },
                { "type", element.TypeName },
                { "fullType", element.FullTypeName },
                { "name", element.Name },
                { "classes", element.Classes },
                { "inlineStyle", element.InlineStyle },
                { "line", element.LineNumber },
                { "ussMatchedClasses", matchedClasses },
                { "ussDefaultSize", BuildDefaultSizeDictionary(resolvedDeclarations) },
                { "ussResolvedDeclarations", resolvedDeclarations },
            };
        }

        private static Dictionary<string, object> BuildDefaultSizeDictionary(Dictionary<string, string> declarations)
        {
            string[] keys =
            {
                "width", "height", "min-width", "min-height", "max-width", "max-height",
                "left", "top", "right", "bottom"
            };

            var result = new Dictionary<string, object>();
            foreach (string key in keys)
            {
                if (declarations.TryGetValue(key, out string value))
                    result[key] = value;
            }

            return result;
        }

        private static Dictionary<string, UssClassStyle> ReadUssClassStyles(List<string> ussPaths)
        {
            var styles = new Dictionary<string, UssClassStyle>(StringComparer.OrdinalIgnoreCase);
            foreach (string ussPath in ussPaths)
            {
                string absolutePath = GetAbsoluteAssetPath(ussPath);
                if (!File.Exists(absolutePath))
                    continue;

                string text = Regex.Replace(File.ReadAllText(absolutePath), @"/\*.*?\*/", "", RegexOptions.Singleline);
                foreach (Match ruleMatch in Regex.Matches(text, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}",
                             RegexOptions.Singleline))
                {
                    string selector = ruleMatch.Groups["selector"].Value;
                    string body = ruleMatch.Groups["body"].Value;
                    var declarations = ParseUssDeclarations(body);
                    if (declarations.Count == 0)
                        continue;

                    foreach (Match classMatch in Regex.Matches(selector, @"\.([A-Za-z_][A-Za-z0-9_-]*)"))
                    {
                        string className = classMatch.Groups[1].Value;
                        if (!styles.TryGetValue(className, out var style))
                        {
                            style = new UssClassStyle { ClassName = className };
                            styles[className] = style;
                        }

                        if (!style.SourcePaths.Contains(ussPath, StringComparer.OrdinalIgnoreCase))
                            style.SourcePaths.Add(ussPath);

                        foreach (var pair in declarations)
                            style.Declarations[pair.Key] = pair.Value;
                    }
                }
            }

            return styles;
        }

        private static Dictionary<string, string> ParseUssDeclarations(string body)
        {
            var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawDeclaration in body.Split(';'))
            {
                int separatorIndex = rawDeclaration.IndexOf(':');
                if (separatorIndex <= 0)
                    continue;

                string key = rawDeclaration.Substring(0, separatorIndex).Trim();
                string value = rawDeclaration.Substring(separatorIndex + 1).Trim();
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                    continue;

                declarations[key] = value;
            }

            return declarations;
        }

        private static string GetAttributeValue(XElement element, string attributeName)
        {
            foreach (var attribute in element.Attributes())
            {
                if (string.Equals(attribute.Name.LocalName, attributeName, StringComparison.OrdinalIgnoreCase))
                    return attribute.Value;
            }

            return "";
        }

        private static List<string> SplitClasses(string classValue)
        {
            if (string.IsNullOrWhiteSpace(classValue))
                return new List<string>();

            return classValue.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        private static string NormalizeAssetPath(string rawPath, string relativeToAssetPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return "";

            string path = rawPath.Trim().Replace('\\', '/');
            int queryIndex = path.IndexOf('?');
            if (queryIndex >= 0)
                path = path.Substring(0, queryIndex);

            int fragmentIndex = path.IndexOf('#');
            if (fragmentIndex >= 0)
                path = path.Substring(0, fragmentIndex);

            const string projectPrefix = "project://database/";
            if (path.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(projectPrefix.Length);

            path = Uri.UnescapeDataString(path);

            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(path))
            {
                return path;
            }

            if (string.IsNullOrEmpty(relativeToAssetPath) == false)
            {
                string directory = Path.GetDirectoryName(relativeToAssetPath)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(directory) == false)
                    return $"{directory}/{path}";
            }

            return path;
        }

        private static string GetAbsoluteAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return "";

            if (Path.IsPathRooted(assetPath))
                return Path.GetFullPath(assetPath);

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
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

        private static Dictionary<string, object> BuildPixelInfo(
            UnityEngine.UIElements.VisualElement element, float pixelScale)
        {
            var rect = element.worldBound;
            return new Dictionary<string, object>
            {
                { "pixelScale", SafeFloat(pixelScale) },
                { "worldBound", RectToDictionary(rect) },
                { "xOnGrid", IsOnPixelGrid(rect.x, pixelScale, 0.01f) },
                { "yOnGrid", IsOnPixelGrid(rect.y, pixelScale, 0.01f) },
                { "widthOnGrid", IsOnPixelGrid(rect.width, pixelScale, 0.01f) },
                { "heightOnGrid", IsOnPixelGrid(rect.height, pixelScale, 0.01f) },
            };
        }

        private static Dictionary<string, object> BuildBackgroundScaleInfo(
            UnityEngine.UIElements.VisualElement element)
        {
            var result = new Dictionary<string, object>
            {
                { "hasBackground", false },
            };

            var backgroundObject = GetBackgroundObject(element);
            if (backgroundObject == null)
                return result;

            var sourceSize = GetBackgroundObjectSize(backgroundObject);
            var rect = element.worldBound;
            result["hasBackground"] = true;
            result["background"] = BuildUnityObjectInfo(backgroundObject);
            result["sourceWidth"] = SafeFloat(sourceSize.x);
            result["sourceHeight"] = SafeFloat(sourceSize.y);
            result["renderedWidth"] = SafeFloat(rect.width);
            result["renderedHeight"] = SafeFloat(rect.height);
            result["scaleX"] = sourceSize.x > 0 ? SafeFloat(rect.width / sourceSize.x) : null;
            result["scaleY"] = sourceSize.y > 0 ? SafeFloat(rect.height / sourceSize.y) : null;
            result["uniformScale"] = sourceSize.x > 0 && sourceSize.y > 0
                ? SafeFloat(Math.Abs(rect.width / sourceSize.x - rect.height / sourceSize.y))
                : null;
            return result;
        }

        private static void AddPixelGridResult(Dictionary<string, object> result,
            UnityEngine.UIElements.VisualElement element, float pixelScale, float tolerance)
        {
            var rect = element.worldBound;
            bool xPassed = IsOnPixelGrid(rect.x, pixelScale, tolerance);
            bool yPassed = IsOnPixelGrid(rect.y, pixelScale, tolerance);
            bool widthPassed = IsOnPixelGrid(rect.width, pixelScale, tolerance);
            bool heightPassed = IsOnPixelGrid(rect.height, pixelScale, tolerance);

            result["passed"] = xPassed && yPassed && widthPassed && heightPassed;
            result["pixelScale"] = SafeFloat(pixelScale);
            result["tolerance"] = SafeFloat(tolerance);
            result["xDelta"] = SafeFloat(GetPixelGridDelta(rect.x, pixelScale));
            result["yDelta"] = SafeFloat(GetPixelGridDelta(rect.y, pixelScale));
            result["widthDelta"] = SafeFloat(GetPixelGridDelta(rect.width, pixelScale));
            result["heightDelta"] = SafeFloat(GetPixelGridDelta(rect.height, pixelScale));
            result["rect"] = RectToDictionary(rect);
        }

        private static void AddBackgroundScaleResult(Dictionary<string, object> result,
            UnityEngine.UIElements.VisualElement element, float expectedScale, float tolerance)
        {
            var backgroundObject = GetBackgroundObject(element);
            if (backgroundObject == null)
            {
                result["passed"] = false;
                result["error"] = "Element has no resolved background image";
                return;
            }

            var sourceSize = GetBackgroundObjectSize(backgroundObject);
            if (sourceSize.x <= 0 || sourceSize.y <= 0)
            {
                result["passed"] = false;
                result["error"] = "Could not determine background source size";
                result["background"] = BuildUnityObjectInfo(backgroundObject);
                return;
            }

            var rect = element.worldBound;
            float scaleX = rect.width / sourceSize.x;
            float scaleY = rect.height / sourceSize.y;
            bool passed = Math.Abs(scaleX - expectedScale) <= tolerance &&
                          Math.Abs(scaleY - expectedScale) <= tolerance;

            result["passed"] = passed;
            result["expectedScale"] = SafeFloat(expectedScale);
            result["tolerance"] = SafeFloat(tolerance);
            result["scaleX"] = SafeFloat(scaleX);
            result["scaleY"] = SafeFloat(scaleY);
            result["background"] = BuildUnityObjectInfo(backgroundObject);
            result["sourceWidth"] = SafeFloat(sourceSize.x);
            result["sourceHeight"] = SafeFloat(sourceSize.y);
            result["renderedWidth"] = SafeFloat(rect.width);
            result["renderedHeight"] = SafeFloat(rect.height);
        }

        private static void AddRuntimeSizeResult(Dictionary<string, object> result,
            UnityEngine.UIElements.VisualElement element, Dictionary<string, object> args, float defaultTolerance)
        {
            float expectedWidth = GetFloat(args, "width", float.NaN);
            if (float.IsNaN(expectedWidth))
                expectedWidth = GetFloat(args, "expectedWidth", float.NaN);
            float expectedHeight = GetFloat(args, "height", float.NaN);
            if (float.IsNaN(expectedHeight))
                expectedHeight = GetFloat(args, "expectedHeight", float.NaN);
            float tolerance = GetFloat(args, "tolerance", defaultTolerance);

            var rect = element.worldBound;
            float widthDelta = float.IsNaN(expectedWidth) ? 0 : rect.width - expectedWidth;
            float heightDelta = float.IsNaN(expectedHeight) ? 0 : rect.height - expectedHeight;
            bool widthPassed = float.IsNaN(expectedWidth) || Math.Abs(widthDelta) <= tolerance;
            bool heightPassed = float.IsNaN(expectedHeight) || Math.Abs(heightDelta) <= tolerance;

            result["passed"] = widthPassed && heightPassed;
            result["expectedWidth"] = float.IsNaN(expectedWidth) ? null : (object)expectedWidth;
            result["expectedHeight"] = float.IsNaN(expectedHeight) ? null : (object)expectedHeight;
            result["actualWidth"] = SafeFloat(rect.width);
            result["actualHeight"] = SafeFloat(rect.height);
            result["widthDelta"] = SafeFloat(widthDelta);
            result["heightDelta"] = SafeFloat(heightDelta);
            result["tolerance"] = SafeFloat(tolerance);
            result["rect"] = RectToDictionary(rect);
        }

        private static bool IsOnPixelGrid(float value, float pixelScale, float tolerance)
        {
            if (pixelScale <= 0)
                return true;

            return Math.Abs(GetPixelGridDelta(value, pixelScale)) <= tolerance;
        }

        private static float GetPixelGridDelta(float value, float pixelScale)
        {
            if (pixelScale <= 0)
                return 0;

            return value - Mathf.Round(value / pixelScale) * pixelScale;
        }

        private static UnityEngine.Object GetBackgroundObject(UnityEngine.UIElements.VisualElement element)
        {
            object styleValue = GetPropertyValue(element.resolvedStyle, "backgroundImage");
            object background = GetPropertyValue(styleValue, "value") ?? styleValue;

            return GetPropertyValue(background, "sprite") as UnityEngine.Object
                   ?? GetPropertyValue(background, "texture") as UnityEngine.Object
                   ?? GetPropertyValue(background, "renderTexture") as UnityEngine.Object
                   ?? GetPropertyValue(background, "vectorImage") as UnityEngine.Object;
        }

        private static Vector2 GetBackgroundObjectSize(UnityEngine.Object backgroundObject)
        {
            switch (backgroundObject)
            {
                case Sprite sprite:
                    return sprite.rect.size;
                case Texture texture:
                    return new Vector2(texture.width, texture.height);
                default:
                    return Vector2.zero;
            }
        }

        private static Dictionary<string, object> BuildUnityObjectInfo(UnityEngine.Object unityObject)
        {
            return new Dictionary<string, object>
            {
                { "name", unityObject != null ? unityObject.name : "" },
                { "type", unityObject != null ? unityObject.GetType().Name : "" },
                { "instanceId", unityObject != null ? unityObject.GetInstanceID() : 0 },
                { "assetPath", unityObject != null ? AssetDatabase.GetAssetPath(unityObject) : "" },
            };
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
                { "left", SafeFloat(style.left) },
                { "top", SafeFloat(style.top) },
                { "right", SafeFloat(style.right) },
                { "bottom", SafeFloat(style.bottom) },
                { "width", SafeFloat(style.width) },
                { "height", SafeFloat(style.height) },
                { "minWidth", style.minWidth.ToString() },
                { "minHeight", style.minHeight.ToString() },
                { "maxWidth", style.maxWidth.ToString() },
                { "maxHeight", style.maxHeight.ToString() },
                { "flexGrow", SafeFloat(style.flexGrow) },
                { "flexShrink", SafeFloat(style.flexShrink) },
                { "flexBasis", style.flexBasis.ToString() },
                { "flexDirection", style.flexDirection.ToString() },
                { "alignItems", style.alignItems.ToString() },
                { "alignSelf", style.alignSelf.ToString() },
                { "justifyContent", style.justifyContent.ToString() },
                { "marginLeft", SafeFloat(style.marginLeft) },
                { "marginTop", SafeFloat(style.marginTop) },
                { "marginRight", SafeFloat(style.marginRight) },
                { "marginBottom", SafeFloat(style.marginBottom) },
                { "paddingLeft", SafeFloat(style.paddingLeft) },
                { "paddingTop", SafeFloat(style.paddingTop) },
                { "paddingRight", SafeFloat(style.paddingRight) },
                { "paddingBottom", SafeFloat(style.paddingBottom) },
                { "backgroundColor", style.backgroundColor.ToString() },
                { "unityBackgroundImageTintColor", style.unityBackgroundImageTintColor.ToString() },
                { "color", style.color.ToString() },
                { "opacity", SafeFloat(style.opacity) },
            };
        }

        private static Dictionary<string, object> BuildBackgroundInfo(UnityEngine.UIElements.VisualElement element)
        {
            return new Dictionary<string, object>
            {
                { "inline", BuildBackgroundValueInfo(GetPropertyValue(element.style, "backgroundImage")) },
                { "resolved", BuildBackgroundValueInfo(GetPropertyValue(element.resolvedStyle, "backgroundImage")) },
            };
        }

        private static Dictionary<string, object> BuildBackgroundValueInfo(object styleValue)
        {
            var info = new Dictionary<string, object>
            {
                { "text", styleValue != null ? styleValue.ToString() : "" },
            };

            object background = GetPropertyValue(styleValue, "value");
            if (background == null)
                background = styleValue;

            AddBackgroundObjectInfo(info, background, "texture");
            AddBackgroundObjectInfo(info, background, "sprite");
            AddBackgroundObjectInfo(info, background, "renderTexture");
            AddBackgroundObjectInfo(info, background, "vectorImage");

            return info;
        }

        private static void AddBackgroundObjectInfo(Dictionary<string, object> info, object background, string propertyName)
        {
            object value = GetPropertyValue(background, propertyName);
            if (value is UnityEngine.Object unityObject)
            {
                info[propertyName] = new Dictionary<string, object>
                {
                    { "name", unityObject.name },
                    { "type", unityObject.GetType().Name },
                    { "instanceId", unityObject.GetInstanceID() },
                    { "assetPath", AssetDatabase.GetAssetPath(unityObject) },
                };
            }
        }

        private static object GetPropertyValue(object target, string propertyName)
        {
            if (target == null || string.IsNullOrEmpty(propertyName))
                return null;

            try
            {
                var property = target.GetType().GetProperty(propertyName,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                return property != null ? property.GetValue(target, null) : null;
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> RectToDictionary(Rect rect)
        {
            return new Dictionary<string, object>
            {
                { "x", SafeFloat(rect.x) },
                { "y", SafeFloat(rect.y) },
                { "width", SafeFloat(rect.width) },
                { "height", SafeFloat(rect.height) },
                { "xMin", SafeFloat(rect.xMin) },
                { "yMin", SafeFloat(rect.yMin) },
                { "xMax", SafeFloat(rect.xMax) },
                { "yMax", SafeFloat(rect.yMax) },
            };
        }

        private static object SafeFloat(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? null : (object)value;
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

        private static float GetFloat(Dictionary<string, object> args, string key, float defaultValue)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            return float.TryParse(args[key].ToString(), out float parsed) ? parsed : defaultValue;
        }

        private static List<object> GetObjectList(Dictionary<string, object> args, string key)
        {
            if (args == null || args.TryGetValue(key, out object value) == false || value == null)
                return new List<object>();

            if (value is List<object> list)
                return list;

            return new List<object> { value };
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static List<string> GetStringList(Dictionary<string, object> args, string arrayKey, string singleKey)
        {
            var results = new List<string>();
            if (args == null)
                return results;

            if (args.TryGetValue(arrayKey, out object arrayValue) && arrayValue is List<object> list)
            {
                foreach (object item in list)
                {
                    if (item != null)
                        results.Add(item.ToString());
                }
            }

            string singleValue = GetString(args, singleKey);
            if (string.IsNullOrEmpty(singleValue) == false &&
                results.Contains(singleValue, StringComparer.OrdinalIgnoreCase) == false)
            {
                results.Add(singleValue);
            }

            return results;
        }

        private sealed class UxmlElementInfo
        {
            public string Path;
            public string TypeName;
            public string FullTypeName;
            public string Name;
            public List<string> Classes = new List<string>();
            public string InlineStyle;
            public int LineNumber;
        }

        private sealed class UssClassStyle
        {
            public string ClassName;
            public readonly List<string> SourcePaths = new List<string>();
            public readonly Dictionary<string, string> Declarations =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    { "className", ClassName },
                    { "sourcePaths", SourcePaths },
                    { "declarations", Declarations },
                    { "defaultSize", BuildDefaultSizeDictionary(Declarations) },
                };
            }
        }
    }
}
