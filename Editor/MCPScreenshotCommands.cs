using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
#if UNITY_EDITOR_WIN
using System.Diagnostics;
using System.Runtime.InteropServices;
#endif
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for capturing screenshots from the Unity Editor.
    /// </summary>
    public static class MCPScreenshotCommands
    {
        private const BindingFlags GameViewMemberFlags = BindingFlags.Instance | BindingFlags.Public |
                                                         BindingFlags.NonPublic;

        // ─── Capture Game View ───

        public static void CaptureGameView(Dictionary<string, object> args, Action<object> resolve)
        {
            args ??= new Dictionary<string, object>();
            if (EditorApplication.isPlaying == false)
            {
                resolve(MCPResponse.Error("screenshot/game requires Play Mode because ScreenCapture writes on a rendered game frame.",
                    "requires_play_mode", false, new Dictionary<string, object>
                    {
                        { "requiresPlayMode", true },
                    }));
                return;
            }

            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                path = "Assets/Screenshots/GameView_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";

            int superSize = Math.Max(1, args.ContainsKey("superSize")
                ? Convert.ToInt32(args["superSize"])
                : 1);
            int waitFrames = Math.Max(1, GetInt(args, "waitFrames", 2));
            int stableFrames = Math.Max(1, GetInt(args, "stableFrames", 2));
            int timeoutMs = Math.Max(1000, GetInt(args, "timeoutMs", 10000));

            string fullPath = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(fullPath);
            try
            {
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            catch (Exception ex)
            {
                resolve(new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", $"Could not prepare screenshot path '{path}': {ex.Message}" },
                });
                return;
            }

            if (EditorApplication.isPaused)
            {
                resolve(CapturePausedGameView(path, fullPath, superSize));
                return;
            }

            int frame = 0;
            int stableFileFrames = 0;
            long lastSize = -1;
            bool captureRequested = false;
            bool resolved = false;
            double startedAt = EditorApplication.timeSinceStartup;

            void Finish(object result)
            {
                if (resolved)
                    return;

                resolved = true;
                EditorApplication.update -= Tick;
                resolve(result);
            }

            void Tick()
            {
                frame++;
                double elapsedMs = (EditorApplication.timeSinceStartup - startedAt) * 1000d;
                if (elapsedMs >= timeoutMs)
                {
                    Finish(new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error", $"Timed out waiting for Game View screenshot '{path}'." },
                        { "path", path },
                        { "elapsedMs", Math.Round(elapsedMs, 2) },
                        { "captureRequested", captureRequested },
                        { "fileExists", File.Exists(fullPath) },
                        { "stableFileFrames", stableFileFrames },
                    });
                    return;
                }

                if (captureRequested == false)
                {
                    if (frame < waitFrames)
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                        return;
                    }

                    ScreenCapture.CaptureScreenshot(path, superSize);
                    captureRequested = true;
                    EditorApplication.QueuePlayerLoopUpdate();
                    return;
                }

                if (File.Exists(fullPath))
                {
                    long size = new FileInfo(fullPath).Length;
                    if (size > 0 && size == lastSize)
                        stableFileFrames++;
                    else
                        stableFileFrames = 0;
                    lastSize = size;

                    if (stableFileFrames >= stableFrames &&
                        TryReadPngInfo(fullPath, out int width, out int height, out _))
                    {
                        Finish(new Dictionary<string, object>
                        {
                            { "success", true },
                            { "path", path },
                            { "fullPath", fullPath.Replace('\\', '/') },
                            { "superSize", superSize },
                            { "width", width },
                            { "height", height },
                            { "sizeBytes", size },
                            { "waitFrames", waitFrames },
                            { "stableFrames", stableFrames },
                            { "elapsedMs", Math.Round(elapsedMs, 2) },
                            { "fileReady", true },
                        });
                        return;
                    }
                }

                EditorApplication.QueuePlayerLoopUpdate();
            }

            EditorApplication.update += Tick;
        }

        public static object CaptureGameView(Dictionary<string, object> args)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", "screenshot/game must be executed through the deferred route." },
            };
        }

        private static object CapturePausedGameView(string path, string fullPath, int superSize)
        {
            double startedAt = EditorApplication.timeSinceStartup;
            Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                return MCPResponse.Error("Paused Game View capture is unavailable because UnityEditor.GameView could not be resolved.",
                    "paused_game_view_unavailable", false, new Dictionary<string, object>
                    {
                        { "path", path },
                        { "paused", true },
                    });
            }

            EditorWindow focusedWindow = EditorWindow.focusedWindow;
            EditorWindow[] gameViews = Resources.FindObjectsOfTypeAll(gameViewType).OfType<EditorWindow>().ToArray();
            EditorWindow gameView = gameViews.FirstOrDefault(window => window == focusedWindow) ??
                                    gameViews.FirstOrDefault();
            if (gameView == null)
            {
                return MCPResponse.Error("Paused Game View capture requires an open Game View window.",
                    "paused_game_view_unavailable", false, new Dictionary<string, object>
                    {
                        { "path", path },
                        { "paused", true },
                    });
            }

            FieldInfo renderTextureField = gameViewType.GetField("m_RenderTexture", GameViewMemberFlags);
            var renderTexture = renderTextureField?.GetValue(gameView) as RenderTexture;
            if (renderTexture == null || renderTexture.IsCreated() == false)
            {
                return MCPResponse.Error("The paused Game View does not have a completed render texture yet.",
                    "paused_game_view_texture_unavailable", true, new Dictionary<string, object>
                    {
                        { "path", path },
                        { "paused", true },
                        { "gameViewType", gameViewType.FullName },
                    });
            }

            try
            {
                Dictionary<string, object> result = WriteRenderTexturePng(renderTexture, fullPath, superSize,
                    SystemInfo.graphicsUVStartsAtTop);
                result["path"] = path;
                result["fullPath"] = fullPath.Replace('\\', '/');
                result["superSize"] = superSize;
                result["waitFrames"] = 0;
                result["stableFrames"] = 0;
                result["elapsedMs"] = Math.Round((EditorApplication.timeSinceStartup - startedAt) * 1000d, 2);
                result["fileReady"] = true;
                result["paused"] = true;
                result["captureMethod"] = "game_view_render_texture";
                return result;
            }
            catch (Exception ex)
            {
                return MCPResponse.Error($"Could not capture the paused Game View: {ex.Message}",
                    "paused_game_view_capture_failed", false, new Dictionary<string, object>
                    {
                        { "path", path },
                        { "fullPath", fullPath.Replace('\\', '/') },
                        { "paused", true },
                    });
            }
        }

        internal static Dictionary<string, object> WriteRenderTexturePng(RenderTexture source, string fullPath,
            int superSize, bool flipVertically)
        {
            if (source == null || source.IsCreated() == false)
                throw new InvalidOperationException("The source render texture is unavailable.");

            superSize = Math.Max(1, superSize);
            long requestedWidth = (long)source.width * superSize;
            long requestedHeight = (long)source.height * superSize;
            int maxTextureSize = SystemInfo.maxTextureSize > 0 ? SystemInfo.maxTextureSize : 8192;
            if (requestedWidth > maxTextureSize || requestedHeight > maxTextureSize)
            {
                throw new InvalidOperationException(
                    $"Requested screenshot size {requestedWidth}x{requestedHeight} exceeds the GPU texture limit {maxTextureSize}.");
            }

            int width = (int)requestedWidth;
            int height = (int)requestedHeight;
            RenderTexture scaledTexture = null;
            RenderTexture readTexture = source;
            RenderTexture previousActive = RenderTexture.active;
            Texture2D image = null;
            try
            {
                if (superSize > 1)
                {
                    scaledTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                    scaledTexture.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(source, scaledTexture);
                    readTexture = scaledTexture;
                }

                RenderTexture.active = readTexture;
                image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply(false, false);

                if (flipVertically)
                {
                    Color32[] pixels = image.GetPixels32();
                    FlipPixelsVertically(pixels, width, height);
                    image.SetPixels32(pixels);
                    image.Apply(false, false);
                }

                byte[] png = image.EncodeToPNG();
                if (png == null || png.Length == 0)
                    throw new InvalidOperationException("PNG encoding returned no data.");

                string directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory) == false)
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(fullPath, png);

                if (TryReadPngInfo(fullPath, out int decodedWidth, out int decodedHeight, out string decodeError) == false)
                    throw new InvalidOperationException($"Written PNG could not be decoded: {decodeError}");

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "width", decodedWidth },
                    { "height", decodedHeight },
                    { "sizeBytes", png.Length },
                };
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (image != null)
                    UnityEngine.Object.DestroyImmediate(image);
                if (scaledTexture != null)
                    RenderTexture.ReleaseTemporary(scaledTexture);
            }
        }

        internal static void FlipPixelsVertically(Color32[] pixels, int width, int height)
        {
            if (pixels == null)
                throw new ArgumentNullException(nameof(pixels));
            if (width <= 0 || height <= 0 || pixels.Length != width * height)
                throw new ArgumentException("Pixel buffer dimensions do not match its length.", nameof(pixels));

            var row = new Color32[width];
            for (int y = 0; y < height / 2; y++)
            {
                int oppositeY = height - 1 - y;
                Array.Copy(pixels, y * width, row, 0, width);
                Array.Copy(pixels, oppositeY * width, pixels, y * width, width);
                Array.Copy(row, 0, pixels, oppositeY * width, width);
            }
        }

        // ─── Capture Scene View ───

        public static object CaptureSceneView(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                path = "Assets/Screenshots/SceneView_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";

            int width = args.ContainsKey("width") ? Convert.ToInt32(args["width"]) : 1920;
            int height = args.ContainsKey("height") ? Convert.ToInt32(args["height"]) : 1080;

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            // Ensure directory exists
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var camera = sceneView.camera;
            var rt = new RenderTexture(width, height, 24);
            camera.targetTexture = rt;
            camera.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();

            camera.targetTexture = null;
            RenderTexture.active = null;

            byte[] bytes = tex.EncodeToPNG();
            File.WriteAllBytes(path, bytes);

            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);

            AssetDatabase.Refresh();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "width", width },
                { "height", height },
                { "sizeBytes", bytes.Length },
            };
        }

        // ─── Get Scene View Camera Info ───

        public static object GetSceneViewInfo(Dictionary<string, object> args)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            var pivot = sceneView.pivot;
            var rotation = sceneView.rotation.eulerAngles;

            return new Dictionary<string, object>
            {
                { "pivot", new Dictionary<string, object>
                    {
                        { "x", pivot.x }, { "y", pivot.y }, { "z", pivot.z },
                    }
                },
                { "rotation", new Dictionary<string, object>
                    {
                        { "x", rotation.x }, { "y", rotation.y }, { "z", rotation.z },
                    }
                },
                { "size", sceneView.size },
                { "orthographic", sceneView.orthographic },
                { "is2D", sceneView.in2DMode },
                { "drawGizmos", sceneView.drawGizmos },
            };
        }

        // ─── Set Scene View Camera ───

        public static object SetSceneViewCamera(Dictionary<string, object> args)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            var updated = new List<string>();

            if (args.ContainsKey("pivot") && args["pivot"] is Dictionary<string, object> pivotDict)
            {
                float x = pivotDict.ContainsKey("x") ? Convert.ToSingle(pivotDict["x"]) : sceneView.pivot.x;
                float y = pivotDict.ContainsKey("y") ? Convert.ToSingle(pivotDict["y"]) : sceneView.pivot.y;
                float z = pivotDict.ContainsKey("z") ? Convert.ToSingle(pivotDict["z"]) : sceneView.pivot.z;
                sceneView.pivot = new Vector3(x, y, z);
                updated.Add("pivot");
            }

            if (args.ContainsKey("rotation") && args["rotation"] is Dictionary<string, object> rotDict)
            {
                float x = rotDict.ContainsKey("x") ? Convert.ToSingle(rotDict["x"]) : 0;
                float y = rotDict.ContainsKey("y") ? Convert.ToSingle(rotDict["y"]) : 0;
                float z = rotDict.ContainsKey("z") ? Convert.ToSingle(rotDict["z"]) : 0;
                sceneView.rotation = Quaternion.Euler(x, y, z);
                updated.Add("rotation");
            }

            if (args.ContainsKey("size"))
            {
                sceneView.size = Convert.ToSingle(args["size"]);
                updated.Add("size");
            }

            if (args.ContainsKey("orthographic"))
            {
                sceneView.orthographic = Convert.ToBoolean(args["orthographic"]);
                updated.Add("orthographic");
            }

            if (args.ContainsKey("is2D"))
            {
                sceneView.in2DMode = Convert.ToBoolean(args["is2D"]);
                updated.Add("is2D");
            }

            if (args.ContainsKey("lookAt") && args["lookAt"] is Dictionary<string, object> lookDict)
            {
                float x = lookDict.ContainsKey("x") ? Convert.ToSingle(lookDict["x"]) : 0;
                float y = lookDict.ContainsKey("y") ? Convert.ToSingle(lookDict["y"]) : 0;
                float z = lookDict.ContainsKey("z") ? Convert.ToSingle(lookDict["z"]) : 0;
                float sz = args.ContainsKey("lookAtSize") ? Convert.ToSingle(args["lookAtSize"]) : 10f;
                sceneView.LookAt(new Vector3(x, y, z), sceneView.rotation, sz);
                updated.Add("lookAt");
            }

            if (args.ContainsKey("frameSelected") && Convert.ToBoolean(args["frameSelected"]))
            {
                sceneView.FrameSelected();
                updated.Add("frameSelected");
            }

            sceneView.Repaint();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "updated", updated },
            };
        }

        public static object GetGameViewInfo(Dictionary<string, object> args)
        {
            if (!TryGetGameView(out Type gameViewType, out EditorWindow gameView, out object error))
                return error;

            return BuildGameViewInfo(gameViewType, gameView);
        }

        public static object SetGameViewResolution(Dictionary<string, object> args)
        {
            int width = GetInt(args, "width", 0);
            int height = GetInt(args, "height", 0);
            if (width <= 0 || height <= 0)
                return new { error = "width and height must be greater than 0" };

            if (!TryGetGameView(out Type gameViewType, out EditorWindow gameView, out object error))
                return error;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo setCustomResolution = gameViewType.GetMethod("SetCustomResolution", flags);
            if (setCustomResolution == null)
                return new { error = "UnityEditor.GameView.SetCustomResolution was not found in this Unity version" };

            string label = GetString(args, "label");
            if (string.IsNullOrEmpty(label))
                label = GetString(args, "name");
            if (string.IsNullOrEmpty(label))
                label = $"{width}x{height}";

            setCustomResolution.Invoke(gameView, new object[] { new Vector2(width, height), label });
            gameView.Repaint();

            var result = BuildGameViewInfo(gameViewType, gameView);
            result["success"] = true;
            result["requestedWidth"] = width;
            result["requestedHeight"] = height;
            result["label"] = label;
            return result;
        }

        public static object SetGameViewScale(Dictionary<string, object> args)
        {
            if (!TryGetFloat(args, "scale", out float scale))
            {
                if (!TryGetFloat(args, "value", out scale))
                    return new { error = "scale is required" };
            }

            if (scale <= 0)
                return new { error = "scale must be greater than 0" };

            if (!TryGetGameView(out Type gameViewType, out EditorWindow gameView, out object error))
                return error;

            if (!TrySnapGameViewZoom(gameViewType, gameView, scale, out string zoomError))
                return new { error = zoomError };

            var result = BuildGameViewInfo(gameViewType, gameView);
            result["success"] = true;
            result["requestedScale"] = scale;
            return result;
        }

        public static object SetGameViewMinScale(Dictionary<string, object> args)
        {
            if (!TryGetGameView(out Type gameViewType, out EditorWindow gameView, out object error))
                return error;

            float fallbackScale = GetFloat(args, "fallbackScale", 0.76f);
            float minScale = GetGameViewZoomAreaMinimumScale(gameViewType, gameView, fallbackScale);

            if (!TrySnapGameViewZoom(gameViewType, gameView, minScale, out string zoomError))
                return new { error = zoomError };

            var result = BuildGameViewInfo(gameViewType, gameView);
            result["success"] = true;
            result["appliedScale"] = minScale;
            result["fallbackScale"] = fallbackScale;
            return result;
        }

        public static object CropImage(Dictionary<string, object> args)
        {
            string sourcePath = GetString(args, "sourcePath");
            if (string.IsNullOrEmpty(sourcePath))
                sourcePath = GetString(args, "imagePath");
            if (string.IsNullOrEmpty(sourcePath))
                sourcePath = GetString(args, "path");
            if (string.IsNullOrEmpty(sourcePath))
                return new { error = "sourcePath, imagePath, or path is required" };

            string absoluteSourcePath = ResolveFilePath(sourcePath);
            if (File.Exists(absoluteSourcePath) == false)
                return new { error = $"Image file not found at '{sourcePath}'" };

            if (!TryGetRectInt(args, out RectInt rect))
                return new { error = "rect is required with x, y, width, and height" };

            string outputPath = GetString(args, "outputPath");
            if (string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(absoluteSourcePath) ?? "";
                string name = Path.GetFileNameWithoutExtension(absoluteSourcePath);
                outputPath = Path.Combine(dir, name + "_crop.png");
            }

            string absoluteOutputPath = ResolveFilePath(outputPath);
            bool originTopLeft = GetBool(args, "originTopLeft", true);
            Texture2D source = null;
            Texture2D cropped = null;
            try
            {
                source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!source.LoadImage(File.ReadAllBytes(absoluteSourcePath)))
                    return new { error = $"Could not decode image '{sourcePath}'" };

                int x = Mathf.Clamp(rect.x, 0, source.width);
                int y = Mathf.Clamp(rect.y, 0, source.height);
                int width = Mathf.Clamp(rect.width, 0, source.width - x);
                int height = Mathf.Clamp(rect.height, 0, source.height - y);
                if (width <= 0 || height <= 0)
                    return new { error = $"Crop rect is outside image bounds. Image={source.width}x{source.height}, rect={rect}" };

                int readY = originTopLeft ? source.height - y - height : y;
                readY = Mathf.Clamp(readY, 0, source.height - height);
                cropped = new Texture2D(width, height, TextureFormat.RGBA32, false);
                cropped.SetPixels(source.GetPixels(x, readY, width, height));
                cropped.Apply();

                string directory = Path.GetDirectoryName(absoluteOutputPath);
                if (string.IsNullOrEmpty(directory) == false)
                    Directory.CreateDirectory(directory);

                byte[] png = cropped.EncodeToPNG();
                File.WriteAllBytes(absoluteOutputPath, png);
                RefreshAssetIfNeeded(absoluteOutputPath);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "sourcePath", sourcePath },
                    { "outputPath", outputPath },
                    { "absoluteOutputPath", absoluteOutputPath },
                    { "sourceWidth", source.width },
                    { "sourceHeight", source.height },
                    { "originTopLeft", originTopLeft },
                    { "cropRect", new Dictionary<string, object>
                        {
                            { "x", x },
                            { "y", y },
                            { "width", width },
                            { "height", height },
                        }
                    },
                    { "sizeBytes", png.Length },
                };
            }
            finally
            {
                if (source != null)
                    UnityEngine.Object.DestroyImmediate(source);
                if (cropped != null)
                    UnityEngine.Object.DestroyImmediate(cropped);
            }
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.ContainsKey(key) && args[key] != null ? args[key].ToString() : "";
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || args.ContainsKey(key) == false || args[key] == null)
                return defaultValue;

            return int.TryParse(args[key].ToString(), out int value) ? value : defaultValue;
        }

        internal static bool TryReadPngInfo(string path, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = "";
            Texture2D texture = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0)
                {
                    error = "PNG file is empty.";
                    return false;
                }

                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(bytes, false) == false)
                {
                    error = "PNG decode failed.";
                    return false;
                }

                width = texture.width;
                height = texture.height;
                return width > 0 && height > 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static float GetFloat(Dictionary<string, object> args, string key, float defaultValue)
        {
            return TryGetFloat(args, key, out float value) ? value : defaultValue;
        }

        private static bool TryGetFloat(Dictionary<string, object> args, string key, out float value)
        {
            value = 0;
            return args != null && args.ContainsKey(key) && args[key] != null &&
                   float.TryParse(args[key].ToString(), out value);
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || args.ContainsKey(key) == false || args[key] == null)
                return defaultValue;

            if (args[key] is bool value)
                return value;

            return bool.TryParse(args[key].ToString(), out bool parsed) ? parsed : defaultValue;
        }

        private static bool TryGetRectInt(Dictionary<string, object> args, out RectInt rect)
        {
            rect = default(RectInt);
            if (args == null)
                return false;

            var dictionary = args;
            if (args.TryGetValue("rect", out object rectValue))
            {
                dictionary = rectValue as Dictionary<string, object>;
                if (dictionary == null)
                    return false;
            }

            if (!TryGetInt(dictionary, "x", out int x) ||
                !TryGetInt(dictionary, "y", out int y) ||
                !TryGetInt(dictionary, "width", out int width) ||
                !TryGetInt(dictionary, "height", out int height))
            {
                return false;
            }

            rect = new RectInt(x, y, width, height);
            return true;
        }

        private static bool TryGetInt(Dictionary<string, object> args, string key, out int value)
        {
            value = 0;
            return args != null && args.ContainsKey(key) && args[key] != null &&
                   int.TryParse(args[key].ToString(), out value);
        }

        private static string ResolveFilePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            string normalized = path.Replace('\\', '/');
            if (Path.IsPathRooted(path))
                return path;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, normalized));
        }

        private static void RefreshAssetIfNeeded(string absolutePath)
        {
            string normalized = absolutePath.Replace('\\', '/');
            string assetsRoot = Application.dataPath.Replace('\\', '/');
            if (normalized.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                AssetDatabase.Refresh();
        }

        private static bool TryGetGameView(out Type gameViewType, out EditorWindow gameView, out object error)
        {
            gameViewType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                gameView = null;
                error = new { error = "UnityEditor.GameView was not found in this Unity version" };
                return false;
            }

            gameView = EditorWindow.GetWindow(gameViewType);
            if (gameView == null)
            {
                error = new { error = "Could not open Unity Game View" };
                return false;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            gameViewType.GetMethod("InitializeZoomArea", flags)?.Invoke(gameView, null);
            error = null;
            return true;
        }

        private static bool TrySnapGameViewZoom(Type gameViewType, EditorWindow gameView, float scale,
            out string error)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo snapZoom = gameViewType.GetMethod("SnapZoom", flags, null, new[] { typeof(float) }, null);
            if (snapZoom == null)
            {
                error = "UnityEditor.GameView.SnapZoom(float) was not found in this Unity version";
                return false;
            }

            snapZoom.Invoke(gameView, new object[] { scale });
            gameView.Repaint();
            error = null;
            return true;
        }

        private static float GetGameViewZoomAreaMinimumScale(Type gameViewType, EditorWindow gameView,
            float fallbackScale)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo zoomAreaField = gameViewType.GetField("m_ZoomArea", flags);
            object zoomArea = zoomAreaField?.GetValue(gameView);
            if (zoomArea == null)
                return fallbackScale;

            Type zoomAreaType = zoomArea.GetType();
            float hMin = GetZoomAreaScaleLimit(zoomAreaType, zoomArea, "m_HScaleMin", "hScaleMin", fallbackScale);
            float vMin = GetZoomAreaScaleLimit(zoomAreaType, zoomArea, "m_VScaleMin", "vScaleMin", fallbackScale);
            float minScale = Mathf.Max(hMin, vMin);

            return IsReasonableGameViewScale(minScale) ? minScale : fallbackScale;
        }

        private static float GetZoomAreaScaleLimit(Type zoomAreaType, object zoomArea, string fieldName,
            string propertyName, float fallbackScale)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object value = zoomAreaType.GetField(fieldName, flags)?.GetValue(zoomArea);
            if (value == null)
                value = zoomAreaType.GetProperty(propertyName, flags)?.GetValue(zoomArea);

            if (value == null)
                return fallbackScale;

            try
            {
                float scale = Convert.ToSingle(value);
                return IsReasonableGameViewScale(scale) ? scale : fallbackScale;
            }
            catch
            {
                return fallbackScale;
            }
        }

        private static bool IsReasonableGameViewScale(float scale)
        {
            return !float.IsNaN(scale) && !float.IsInfinity(scale) && scale >= 0.05f && scale <= 10f;
        }

        private static Dictionary<string, object> BuildGameViewInfo(Type gameViewType, EditorWindow gameView)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object targetRenderSize = gameViewType.GetProperty("targetRenderSize", flags)?.GetValue(gameView);
            object zoomAreaScale = gameViewType.GetProperty("zoomAreaScale", flags)?.GetValue(gameView);
            object selectedSizeIndex = gameViewType.GetProperty("selectedSizeIndex", flags)?.GetValue(gameView);
            object currentSizeGroupType = gameViewType.GetProperty("currentSizeGroupType", flags)?.GetValue(gameView);
            object currentGameViewSize = gameViewType.GetProperty("currentGameViewSize", flags)?.GetValue(gameView);

            string displayText = "";
            if (currentGameViewSize != null)
            {
                displayText = currentGameViewSize.GetType()
                    .GetProperty("displayText", flags)
                    ?.GetValue(currentGameViewSize)
                    ?.ToString() ?? "";
            }

            float fallbackScale = 0.76f;
            return new Dictionary<string, object>
            {
                { "gameViewTitle", gameView.titleContent != null ? gameView.titleContent.text : gameView.name },
                { "selectedSizeIndex", selectedSizeIndex != null ? selectedSizeIndex.ToString() : "" },
                { "currentSizeGroupType", currentSizeGroupType != null ? currentSizeGroupType.ToString() : "" },
                { "displayText", displayText },
                { "targetRenderSize", targetRenderSize != null ? targetRenderSize.ToString() : "" },
                { "scale", zoomAreaScale != null ? zoomAreaScale.ToString() : "" },
                { "minScale", GetGameViewZoomAreaMinimumScale(gameViewType, gameView, fallbackScale) },
            };
        }

        // ─── Capture an arbitrary EditorWindow (Inspector, Project, custom windows…) ───
        // Unlike Game/Scene view (which ARE cameras and are captured by rendering the camera
        // into a RenderTexture), an EditorWindow is an IMGUI/UI-Toolkit window with no camera.
        // The only way to get its pixels is to capture the OS window, via the Win32 PrintWindow
        // API (occlusion-proof: the window renders itself into an offscreen DC, so no raise/focus
        // is needed). PLATFORM: Windows editor only — PrintWindow is a Win32 API with no macOS/
        // Linux equivalent, so this command is unsupported there (returns a clear error).
        //
        // args: window (required — EditorWindow type FullName e.g. "UnityEditor.InspectorWindow",
        //       simple type name, or tab title); path (optional — default Assets/Screenshots/…,
        //       any user-chosen .png path is honoured); maxDimension (optional, default 8192).
        public static object CaptureEditorWindow(Dictionary<string, object> args)
        {
#if UNITY_EDITOR_WIN
            string window = args != null && args.ContainsKey("window") ? args["window"].ToString()
                          : args != null && args.ContainsKey("typeOrTitle") ? args["typeOrTitle"].ToString() : "";
            if (string.IsNullOrEmpty(window))
                return Err("'window' is required (EditorWindow type FullName e.g. 'UnityEditor.InspectorWindow', simple type name, or tab title).");

            string path = args != null && args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                path = "Assets/Screenshots/EditorWindow_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return Err("path must end in .png");
            int maxDimension = args != null && args.ContainsKey("maxDimension") ? Convert.ToInt32(args["maxDimension"]) : 8192;

            var win = FindWindow(window, out int matchCount);
            if (win == null)
                return Err(matchCount > 1
                    ? "Ambiguous: " + matchCount + " windows match '" + window + "'. Pass the exact type FullName."
                    : "No EditorWindow matches '" + window + "'.");

            var (pid, main) = ProcInfo();
            if (main == IntPtr.Zero) return Err("Could not resolve the main editor window handle.");

            bool floating = IsFloating(win);
            bool restoreFocus = false;
            EditorWindow prevFocus = EditorWindow.focusedWindow;
            try
            {
                // Only the docked path needs the tab activated; a floating window is captured by
                // its own HWND, so it is never raised/focused (keeps the occlusion-proof promise).
                if (!floating)
                {
                    win.Focus();
                    restoreFocus = true;
                }
                win.Repaint();
                RepaintImmediately(win);

                IntPtr hwnd; bool whole; int px = 0, py = 0, pw = 0, ph = 0;
                float pixelsPerPoint = Math.Max(1f, EditorGUIUtility.pixelsPerPoint);
                if (floating)
                {
                    hwnd = FindHwndByTitleExact(win, pid, main, out int n);
                    if (hwnd == IntPtr.Zero)
                    {
                        if (n > 1)
                            return Err("Ambiguous floating-window HWND (" + n + " matches).");

                        win.Focus();
                        restoreFocus = true;
                        floating = false;
                        hwnd = main;
                        whole = false;
                        var rp = win.position;
                        px = (int)Math.Round(rp.x); py = (int)Math.Round(rp.y);
                        pw = (int)Math.Round(rp.width); ph = (int)Math.Round(rp.height);
                        if (pw <= 0 || ph <= 0) return Err("Bad panel rect " + pw + "x" + ph);
                    }
                    else
                    {
                        whole = true;
                    }
                }
                else
                {
                    hwnd = main; whole = false;
                    var rp = win.position;
                    px = (int)Math.Round(rp.x); py = (int)Math.Round(rp.y);
                    pw = (int)Math.Round(rp.width); ph = (int)Math.Round(rp.height);
                    if (pw <= 0 || ph <= 0) return Err("Bad panel rect " + pw + "x" + ph);
                }

                return GrabAndEncode(hwnd, whole, px, py, pw, ph, pixelsPerPoint, path, maxDimension, win,
                    floating);
            }
            finally
            {
                // Restore the user's previously-focused tab (only the docked path changed it).
                if (restoreFocus && prevFocus != null && prevFocus != win)
                    try { prevFocus.Focus(); } catch { }
            }
#else
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", "CaptureEditorWindow is Windows-only: it uses the Win32 PrintWindow API to grab an EditorWindow's pixels, which has no macOS/Linux equivalent. For the game or scene view use screenshot/game or screenshot/scene (camera-based, cross-platform)." },
                { "platform", Application.platform.ToString() },
            };
#endif
        }

#if UNITY_EDITOR_WIN
        static Dictionary<string, object> Err(string msg, int win32 = 0)
        {
            var d = new Dictionary<string, object> { { "success", false }, { "error", msg } };
            if (win32 != 0) d["win32Error"] = win32;
            return d;
        }

        // All GDI handles and the Texture2D are released in finally (deselect-before-delete).
        static Dictionary<string, object> GrabAndEncode(IntPtr hwnd, bool wholeWindow,
            int panelX, int panelY, int panelW, int panelH,
            float pixelsPerPoint, string path, int maxDimension, EditorWindow win, bool floating)
        {
            if (IsIconic(hwnd)) return Err("Window is minimized.");
            if (!GetWindowRect(hwnd, out RECT wr)) return Err("GetWindowRect failed.", Marshal.GetLastWin32Error());
            int winW = wr.right - wr.left, winH = wr.bottom - wr.top;
            if (winW <= 0 || winH <= 0) return Err("Bad window rect " + winW + "x" + winH);

            int cropX, cropY, cropW, cropH;
            string cropWarning = "";
            string coordinateMode = "whole-window";
            if (wholeWindow) { cropX = 0; cropY = 0; cropW = winW; cropH = winH; }
            else
            {
                var candidates = new List<CropCandidate>
                {
                    new CropCandidate(panelX - wr.left, panelY - wr.top, panelW, panelH, "screen-pixels"),
                    new CropCandidate(panelX, panelY, panelW, panelH, "window-local-pixels"),
                };
                if (Math.Abs(pixelsPerPoint - 1f) > 0.01f)
                {
                    int scaledX = (int)Math.Round(panelX * pixelsPerPoint);
                    int scaledY = (int)Math.Round(panelY * pixelsPerPoint);
                    int scaledW = (int)Math.Round(panelW * pixelsPerPoint);
                    int scaledH = (int)Math.Round(panelH * pixelsPerPoint);
                    candidates.Add(new CropCandidate(scaledX - wr.left, scaledY - wr.top, scaledW, scaledH,
                        "screen-points-scaled"));
                    candidates.Add(new CropCandidate(scaledX, scaledY, scaledW, scaledH,
                        "window-local-points-scaled"));
                }

                var selected = candidates.FirstOrDefault(candidate => candidate.HasValidOrigin(winW, winH));
                if (selected.Width <= 0 || selected.Height <= 0)
                    return Err("Panel rect could not be mapped into the captured window; DPI / multi-monitor mismatch.");

                cropX = selected.X;
                cropY = selected.Y;
                cropW = selected.Width;
                cropH = selected.Height;
                coordinateMode = selected.Mode;
                if (coordinateMode != "screen-pixels")
                    cropWarning = "EditorWindow crop used " + coordinateMode + " coordinate fallback.";

                if (cropX + cropW > winW) cropW = winW - cropX;
                if (cropY + cropH > winH) cropH = winH - cropY;
                if (cropW <= 0 || cropH <= 0) return Err("Bad crop " + cropW + "x" + cropH);
            }

            int contentX = 0;
            int contentY = 0;
            int contentW = cropW;
            int contentH = cropH;
            if (wholeWindow)
            {
                if (GetClientRect(hwnd, out RECT clientRect))
                {
                    var clientOrigin = new POINT { x = clientRect.left, y = clientRect.top };
                    if (ClientToScreen(hwnd, ref clientOrigin))
                    {
                        contentX = Math.Max(0, clientOrigin.x - wr.left - cropX);
                        contentY = Math.Max(0, clientOrigin.y - wr.top - cropY);
                        contentW = Math.Min(clientRect.right - clientRect.left, cropW - contentX);
                        contentH = Math.Min(clientRect.bottom - clientRect.top, cropH - contentY);
                    }
                    else
                    {
                        cropWarning = AppendWarning(cropWarning,
                            "Could not map the floating window client area; contentRect falls back to the full capture.");
                    }
                }
                else
                {
                    cropWarning = AppendWarning(cropWarning,
                        "Could not read the floating window client area; contentRect falls back to the full capture.");
                }
            }

            if (contentW <= 0 || contentH <= 0)
            {
                contentX = 0;
                contentY = 0;
                contentW = cropW;
                contentH = cropH;
                cropWarning = AppendWarning(cropWarning,
                    "The mapped client area was empty; contentRect falls back to the full capture.");
            }

            // Bound dimensions before allocating: int-overflow guard (long math) + GPU limit.
            int maxTex = SystemInfo.maxTextureSize; if (maxTex <= 0) maxTex = 8192;
            int cap = Math.Min(maxDimension > 0 ? maxDimension : int.MaxValue, maxTex);
            if (cropW > cap || cropH > cap) return Err("Too large " + cropW + "x" + cropH + " (cap " + cap + ")");
            long need = (long)cropW * cropH * 4L;
            if (need > int.MaxValue) return Err("Capture buffer too large (" + need + " B)");

            IntPtr hScreen = IntPtr.Zero, hMemFull = IntPtr.Zero, hBmpFull = IntPtr.Zero, oldFull = IntPtr.Zero;
            IntPtr hMemCrop = IntPtr.Zero, hBmpCrop = IntPtr.Zero, oldCrop = IntPtr.Zero;
            try
            {
                hScreen = GetDC(IntPtr.Zero); if (hScreen == IntPtr.Zero) return Err("GetDC failed.", Marshal.GetLastWin32Error());
                hMemFull = CreateCompatibleDC(hScreen); if (hMemFull == IntPtr.Zero) return Err("CreateCompatibleDC failed.", Marshal.GetLastWin32Error());
                hBmpFull = CreateCompatibleBitmap(hScreen, winW, winH); if (hBmpFull == IntPtr.Zero) return Err("CreateCompatibleBitmap failed.", Marshal.GetLastWin32Error());
                oldFull = SelectObject(hMemFull, hBmpFull);

                if (!PrintWindow(hwnd, hMemFull, PW_RENDERFULLCONTENT)) return Err("PrintWindow failed.", Marshal.GetLastWin32Error());

                IntPtr grabBmp;
                if (wholeWindow)
                {
                    // GetDIBits needs the bitmap deselected; oldFull is NOT nulled so the finally
                    // re-asserts the deselect even if this SelectObject fails (else DeleteObject leaks).
                    SelectObject(hMemFull, oldFull);
                    grabBmp = hBmpFull;
                }
                else
                {
                    hMemCrop = CreateCompatibleDC(hScreen); if (hMemCrop == IntPtr.Zero) return Err("CreateCompatibleDC(2) failed.", Marshal.GetLastWin32Error());
                    hBmpCrop = CreateCompatibleBitmap(hScreen, cropW, cropH); if (hBmpCrop == IntPtr.Zero) return Err("CreateCompatibleBitmap(2) failed.", Marshal.GetLastWin32Error());
                    oldCrop = SelectObject(hMemCrop, hBmpCrop);
                    if (!BitBlt(hMemCrop, 0, 0, cropW, cropH, hMemFull, cropX, cropY, SRCCOPY)) return Err("BitBlt failed.", Marshal.GetLastWin32Error());
                    SelectObject(hMemCrop, oldCrop);
                    grabBmp = hBmpCrop;
                }

                var bmi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = cropW, biHeight = cropH, // positive = bottom-up, matches Texture2D row order
                    biPlanes = 1, biBitCount = 32, biCompression = 0
                };
                byte[] buf = new byte[cropW * cropH * 4];
                int scan = GetDIBits(hScreen, grabBmp, 0, (uint)cropH, buf, ref bmi, DIB_RGB_COLORS);
                if (scan == 0) return Err("GetDIBits failed.", Marshal.GetLastWin32Error());
                if (scan != cropH) return Err("GetDIBits partial (" + scan + "/" + cropH + ").");

                // All-black detection (GPU refused PW_RENDERFULLCONTENT): aligned RGB sample.
                long sum = 0; int stride = Math.Max(1, (cropW * cropH) / 4096) * 4;
                for (int i = 0; i + 2 < buf.Length; i += stride) sum += buf[i] + buf[i + 1] + buf[i + 2];
                if (sum == 0) return Err("All-black frame (GPU refused PW_RENDERFULLCONTENT).");

                AnalyzeCenterPixels(buf, cropW, cropH, out int centerColorRange,
                    out int centerDistinctColorBuckets, out bool centerVisuallyBlank);

                byte[] png = EncodeBgraBottomUp(buf, cropW, cropH);
                if (png == null || png.Length == 0) return Err("PNG encode failed.");

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, png);
                string normalized = path.Replace('\\', '/');
                if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/Assets/"))
                    AssetDatabase.Refresh();

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", path },
                    { "width", cropW },
                    { "height", cropH },
                    { "sizeBytes", png.Length },
                    { "window", win.GetType().FullName },
                    { "floating", floating },
                    { "coordinateMode", coordinateMode },
                    { "contentRect", new Dictionary<string, object>
                        {
                            { "x", contentX },
                            { "y", contentY },
                            { "width", contentW },
                            { "height", contentH },
                        }
                    },
                    { "centerColorRange", centerColorRange },
                    { "centerDistinctColorBuckets", centerDistinctColorBuckets },
                    { "centerVisuallyBlank", centerVisuallyBlank },
                    { "warning", cropWarning },
                };
            }
            finally
            {
                if (oldCrop != IntPtr.Zero && hMemCrop != IntPtr.Zero) SelectObject(hMemCrop, oldCrop);
                if (hBmpCrop != IntPtr.Zero) DeleteObject(hBmpCrop);
                if (hMemCrop != IntPtr.Zero) DeleteDC(hMemCrop);
                if (oldFull != IntPtr.Zero && hMemFull != IntPtr.Zero) SelectObject(hMemFull, oldFull);
                if (hBmpFull != IntPtr.Zero) DeleteObject(hBmpFull);
                if (hMemFull != IntPtr.Zero) DeleteDC(hMemFull);
                if (hScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hScreen);
            }
        }

        private readonly struct CropCandidate
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Width;
            public readonly int Height;
            public readonly string Mode;

            public CropCandidate(int x, int y, int width, int height, string mode)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
                Mode = mode;
            }

            public bool HasValidOrigin(int windowWidth, int windowHeight)
            {
                return Width > 0 && Height > 0 && X >= 0 && Y >= 0 && X < windowWidth && Y < windowHeight;
            }
        }

        private static string AppendWarning(string existing, string warning)
        {
            return string.IsNullOrEmpty(existing) ? warning : existing + " " + warning;
        }

        private static void AnalyzeCenterPixels(byte[] bgra, int width, int height, out int colorRange,
            out int distinctColorBuckets, out bool visuallyBlank)
        {
            int minLuminance = 255;
            int maxLuminance = 0;
            var buckets = new HashSet<int>();
            int minX = width / 5;
            int maxX = Math.Max(minX + 1, width * 4 / 5);
            int minY = height / 5;
            int maxY = Math.Max(minY + 1, height * 4 / 5);
            int stepX = Math.Max(1, (maxX - minX) / 64);
            int stepY = Math.Max(1, (maxY - minY) / 64);

            for (int y = minY; y < maxY; y += stepY)
            {
                for (int x = minX; x < maxX; x += stepX)
                {
                    int offset = (y * width + x) * 4;
                    int blue = bgra[offset];
                    int green = bgra[offset + 1];
                    int red = bgra[offset + 2];
                    int luminance = (red * 3 + green * 6 + blue) / 10;
                    minLuminance = Math.Min(minLuminance, luminance);
                    maxLuminance = Math.Max(maxLuminance, luminance);
                    buckets.Add((red >> 4) << 8 | (green >> 4) << 4 | (blue >> 4));
                }
            }

            colorRange = maxLuminance - minLuminance;
            distinctColorBuckets = buckets.Count;
            visuallyBlank = colorRange <= 6 && distinctColorBuckets <= 4;
        }

        // GDI 32bpp DIB is BGRA; Texture2D wants RGBA + bottom-up rows (our positive biHeight).
        // Alpha is forced opaque (PrintWindow leaves it garbage). Texture released in finally.
        static byte[] EncodeBgraBottomUp(byte[] bgra, int w, int h)
        {
            var cols = new Color32[w * h];
            for (int i = 0; i < cols.Length; i++) { int o = i * 4; cols[i] = new Color32(bgra[o + 2], bgra[o + 1], bgra[o], 255); }
            Texture2D tex = null;
            try { tex = new Texture2D(w, h, TextureFormat.RGBA32, false); tex.SetPixels32(cols); tex.Apply(false); return tex.EncodeToPNG(); }
            finally { if (tex != null) UnityEngine.Object.DestroyImmediate(tex); }
        }

        // Exact FullName → exact simple type name → exact title → unambiguous substring.
        static EditorWindow FindWindow(string typeOrTitle, out int matchCount)
        {
            matchCount = 0;
            if (string.IsNullOrEmpty(typeOrTitle)) return null;
            var wins = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (var w in wins) if (w != null && w.GetType().FullName == typeOrTitle) { matchCount = 1; return w; }

            EditorWindow nameHit = null; int nameN = 0;
            foreach (var w in wins) if (w != null && string.Equals(w.GetType().Name, typeOrTitle, StringComparison.Ordinal)) { nameN++; if (nameHit == null) nameHit = w; }
            if (nameN >= 1) { matchCount = nameN; return nameN == 1 ? nameHit : null; }

            EditorWindow exact = null; int exactN = 0;
            foreach (var w in wins) { if (w == null) continue; var t = w.titleContent != null ? w.titleContent.text : w.name; if (!string.IsNullOrEmpty(t) && string.Equals(t, typeOrTitle, StringComparison.OrdinalIgnoreCase)) { exactN++; if (exact == null) exact = w; } }
            if (exactN >= 1) { matchCount = exactN; return exactN == 1 ? exact : null; }

            EditorWindow sub = null; int subN = 0;
            foreach (var w in wins) { if (w == null) continue; var t = w.titleContent != null ? w.titleContent.text : w.name; if (!string.IsNullOrEmpty(t) && t.IndexOf(typeOrTitle, StringComparison.OrdinalIgnoreCase) >= 0) { subN++; if (sub == null) sub = w; } }
            matchCount = subN; return subN == 1 ? sub : null;
        }

        // EditorWindow.docked is internal → reflected, guarded. Unknown ⇒ docked (main + crop).
        static bool IsFloating(EditorWindow win)
        {
            try { var p = typeof(EditorWindow).GetProperty("docked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (p != null && p.GetValue(win, null) is bool d) return !d; } catch { }
            return false;
        }

        // EditorWindow.RepaintImmediately is internal → reflected, guarded best-effort (H3).
        static void RepaintImmediately(EditorWindow win)
        {
            try { var m = typeof(EditorWindow).GetMethod("RepaintImmediately", BindingFlags.Instance | BindingFlags.NonPublic); if (m != null) m.Invoke(win, null); } catch { }
        }

        static (int pid, IntPtr main) ProcInfo()
        {
            using (var p = Process.GetCurrentProcess()) return (p.Id, p.MainWindowHandle);
        }

        // A floating EditorWindow's OS window carries its tab title (or type name). Exact match,
        // excludes the main editor HWND, reports the count so ambiguity is surfaced not guessed.
        static IntPtr FindHwndByTitleExact(EditorWindow win, int pid, IntPtr main, out int count)
        {
            string t1 = win.titleContent != null ? win.titleContent.text : null;
            string t2 = win.GetType().Name;
            IntPtr found = IntPtr.Zero; int n = 0;
            EnumWindows((h, l) =>
            {
                // Never let a managed exception unwind through the native frame.
                try
                {
                    if (h == main) return true;
                    if (!IsWindowVisible(h)) return true;
                    GetWindowThreadProcessId(h, out uint wp);
                    if (wp != (uint)pid) return true;
                    int len = GetWindowTextLength(h); if (len <= 0) return true;
                    var sb = new System.Text.StringBuilder(len + 1);
                    GetWindowText(h, sb, sb.Capacity);
                    string title = sb.ToString();
                    if ((t1 != null && string.Equals(title, t1, StringComparison.OrdinalIgnoreCase)) || string.Equals(title, t2, StringComparison.OrdinalIgnoreCase))
                    { n++; if (found == IntPtr.Zero) found = h; }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            count = n; return n == 1 ? found : IntPtr.Zero;
        }

        // ── Win32 P/Invoke + GDI types (Windows editor only) ──
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BitBlt(IntPtr hdcDest, int xD, int yD, int w, int h, IntPtr hdcSrc, int xS, int yS, uint rop);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

        const uint SRCCOPY = 0x00CC0020;
        const uint PW_RENDERFULLCONTENT = 0x00000002; // Windows 8.1+
        const uint DIB_RGB_COLORS = 0;
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth; public int biHeight;
            public ushort biPlanes; public ushort biBitCount; public uint biCompression;
            public uint biSizeImage; public int biXPPM; public int biYPPM;
            public uint biClrUsed; public uint biClrImportant;
        }
#endif
    }
}
