#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Development-only runtime snapshot endpoint for FrameDebuggerAI linked captures.
/// It is idle unless the Unity Editor sends a one-shot PlayerConnection request.
/// </summary>
internal static class FrameDebuggerAIRuntimeSnapshotAgent
{
    private static readonly Guid RequestGuid = new Guid("4b9f2a33-9f07-4ec7-b558-c2ef0f7162a1");
    private static readonly Guid ResponseGuid = new Guid("3f85df43-8c6a-4a1a-8c77-6c8bb93a2b7d");
    private const int MaxRenderers = 6000;
    private const int MaxGraphics = 6000;
    private const int MaxParticles = 3000;
    private const int MaxTextures = 8000;
    private const int MaxTextureThumbnails = 16;
    private const int TextureThumbnailSize = 96;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        try
        {
            PlayerConnection.instance.Register(RequestGuid, OnSnapshotRequest);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FrameDebuggerAI] Runtime snapshot registration failed: " + ex.Message);
        }
    }

    private static void OnSnapshotRequest(MessageEventArgs args)
    {
        try
        {
            string json = BuildSnapshotJson();
            PlayerConnection.instance.Send(ResponseGuid, Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            string json = "{\"schemaVersion\":\"framedebug-ai-runtime-player-snapshot/v1\",\"success\":false,\"error\":\"" + Escape(ex.GetType().Name + ": " + ex.Message) + "\"}";
            PlayerConnection.instance.Send(ResponseGuid, Encoding.UTF8.GetBytes(json));
        }
    }

    private static string BuildSnapshotJson()
    {
        var sb = new StringBuilder(1024 * 256);
        var start = DateTime.UtcNow;
        AppendLine(sb, "{");
        Prop(sb, "schemaVersion", "framedebug-ai-runtime-player-snapshot/v1", true, 2);
        Prop(sb, "success", true, true, 2);
        Prop(sb, "capturedAtUtc", start.ToString("o", CultureInfo.InvariantCulture), true, 2);
        Prop(sb, "frameCount", Time.frameCount, true, 2);
        Prop(sb, "scene", SceneManager.GetActiveScene().name, true, 2);
        Prop(sb, "screenWidth", Screen.width, true, 2);
        Prop(sb, "screenHeight", Screen.height, true, 2);
        AppendResolution(sb, 2, true);
        Prop(sb, "developmentOnly", true, true, 2);
        AppendCameras(sb, 2, true);
        AppendRenderers(sb, 2, true);
        AppendGraphics(sb, 2, true);
        AppendParticles(sb, 2, true);
        AppendTextures(sb, 2, true);
        AppendTextureThumbnails(sb, 2, true);
        Prop(sb, "elapsedMs", (int)(DateTime.UtcNow - start).TotalMilliseconds, false, 2);
        AppendLine(sb, "}");
        return sb.ToString();
    }

    private static void AppendCameras(StringBuilder sb, int indent, bool comma)
    {
        var cameras = SafeFindObjects<Camera>();
        sb.Append(Indent(indent)).Append("\"cameras\": [");
        for (int i = 0; i < cameras.Count; i++)
        {
            var c = cameras[i];
            if (i > 0) sb.Append(",");
            sb.AppendLine();
            sb.Append(Indent(indent + 2)).Append("{");
            Inline(sb, "path", PathOf(c.gameObject), true);
            Inline(sb, "name", c.name, true);
            Inline(sb, "enabled", c.enabled, true);
            Inline(sb, "activeInHierarchy", c.gameObject.activeInHierarchy, true);
            Inline(sb, "depth", c.depth, true);
            Inline(sb, "clearFlags", c.clearFlags.ToString(), true);
            Inline(sb, "cullingMask", c.cullingMask, true);
            Inline(sb, "cullingMaskNames", LayerMaskNames(c.cullingMask), true);
            Inline(sb, "orthographic", c.orthographic, true);
            Inline(sb, "orthographicSize", c.orthographicSize, true);
            Inline(sb, "fieldOfView", c.fieldOfView, true);
            Inline(sb, "nearClipPlane", c.nearClipPlane, true);
            Inline(sb, "farClipPlane", c.farClipPlane, true);
            Inline(sb, "allowHDR", c.allowHDR, true);
            Inline(sb, "allowMSAA", c.allowMSAA, true);
            Inline(sb, "pixelWidth", SafeCameraInt(c, "pixelWidth"), true);
            Inline(sb, "pixelHeight", SafeCameraInt(c, "pixelHeight"), true);
            Inline(sb, "scaledPixelWidth", SafeCameraInt(c, "scaledPixelWidth"), true);
            Inline(sb, "scaledPixelHeight", SafeCameraInt(c, "scaledPixelHeight"), true);
            Inline(sb, "aspect", c.aspect, true);
            Inline(sb, "rect", RectString(c.rect), true);
            Inline(sb, "cameraType", c.cameraType.ToString(), true);
            Inline(sb, "actualRenderingPath", c.actualRenderingPath.ToString(), true);
            Inline(sb, "renderingPath", c.renderingPath.ToString(), true);
            Inline(sb, "useOcclusionCulling", c.useOcclusionCulling, true);
            Inline(sb, "targetTexture", c.targetTexture != null ? c.targetTexture.name : "", true);
            Inline(sb, "targetTextureMeta", TextureSummary(c.targetTexture), true);
            Inline(sb, "pixelRect", RectString(c.pixelRect), true);
            Inline(sb, "urpAdditionalData", SummarizeObjectFields(GetUniversalAdditionalCameraData(c), 32), false);
            sb.Append("}");
        }
        if (cameras.Count > 0) sb.AppendLine();
        sb.Append(Indent(indent)).Append("]");
        if (comma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendResolution(StringBuilder sb, int indent, bool comma)
    {
        sb.Append(Indent(indent)).Append("\"resolution\": {");
        Inline(sb, "screenWidth", Screen.width, true);
        Inline(sb, "screenHeight", Screen.height, true);
        Inline(sb, "safeArea", RectString(Screen.safeArea), true);
        Inline(sb, "currentResolution", CurrentResolutionString(), true);
        Inline(sb, "fullScreen", Screen.fullScreen, true);
        Inline(sb, "fullScreenMode", Screen.fullScreenMode.ToString(), true);
        Inline(sb, "orientation", Screen.orientation.ToString(), true);
        Inline(sb, "dpi", Screen.dpi, true);
        Inline(sb, "platform", Application.platform.ToString(), true);
        Inline(sb, "targetFrameRate", Application.targetFrameRate, true);
        Inline(sb, "scalableBufferWidthScale", ScalableBufferManager.widthScaleFactor, true);
        Inline(sb, "scalableBufferHeightScale", ScalableBufferManager.heightScaleFactor, true);
        Inline(sb, "currentRenderPipeline", GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.GetType().FullName : "", true);
        Inline(sb, "currentRenderPipelineSummary", SummarizeObjectFields(GraphicsSettings.currentRenderPipeline, 32), true);
        Inline(sb, "qualityRenderPipeline", QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline.GetType().FullName : "", true);
        Inline(sb, "qualityRenderPipelineSummary", SummarizeObjectFields(QualitySettings.renderPipeline, 32), true);
        Inline(sb, "displays", DisplaysSummary(), false);
        sb.Append("}");
        if (comma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendRenderers(StringBuilder sb, int indent, bool comma)
    {
        var renderers = SafeFindObjects<Renderer>();
        sb.Append(Indent(indent)).Append("\"renderers\": [");
        int written = 0;
        for (int i = 0; i < renderers.Count && written < MaxRenderers; i++)
        {
            var r = renderers[i];
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (written > 0) sb.Append(",");
            sb.AppendLine();
            sb.Append(Indent(indent + 2)).Append("{");
            Inline(sb, "path", PathOf(r.gameObject), true);
            Inline(sb, "name", r.gameObject.name, true);
            Inline(sb, "type", r.GetType().Name, true);
            Inline(sb, "layer", r.gameObject.layer, true);
            Inline(sb, "enabled", r.enabled, true);
            Inline(sb, "activeInHierarchy", r.gameObject.activeInHierarchy, true);
            Inline(sb, "instanceId", r.GetInstanceID(), true);
            Inline(sb, "meshName", MeshName(r), true);
            Inline(sb, "meshInstanceId", MeshInstanceId(r), true);
            Inline(sb, "sortingLayerId", r.sortingLayerID, true);
            Inline(sb, "sortingOrder", r.sortingOrder, true);
            Inline(sb, "sortingGroup", SortingGroupSummary(r.gameObject), true);
            Inline(sb, "materialName", r.sharedMaterial != null ? r.sharedMaterial.name : "", true);
            Inline(sb, "materialInstanceId", r.sharedMaterial != null ? r.sharedMaterial.GetInstanceID() : 0, true);
            Inline(sb, "shaderName", r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "", true);
            Inline(sb, "renderQueue", r.sharedMaterial != null ? r.sharedMaterial.renderQueue : -1, true);
            Inline(sb, "mainTexture", MainTextureSummary(r.sharedMaterial), true);
            Inline(sb, "textureName", MainTextureName(r.sharedMaterial), true);
            Inline(sb, "materialTextures", MaterialTexturesSummary(r.sharedMaterial, 12), true);
            Inline(sb, "bounds", BoundsString(r.bounds), true);
            Inline(sb, "materials", MaterialsSummary(r.sharedMaterials), true);
            Inline(sb, "materialFingerprint", MaterialFingerprint(r.sharedMaterial), false);
            sb.Append("}");
            written++;
        }
        if (written > 0) sb.AppendLine();
        sb.Append(Indent(indent)).Append("]");
        if (comma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendGraphics(StringBuilder sb, int indent, bool comma)
    {
        var graphics = SafeFindObjects<Graphic>();
        sb.Append(Indent(indent)).Append("\"uiGraphics\": [");
        int written = 0;
        for (int i = 0; i < graphics.Count && written < MaxGraphics; i++)
        {
            var g = graphics[i];
            if (g == null || !g.enabled || !g.gameObject.activeInHierarchy) continue;
            var canvas = g.GetComponentInParent<Canvas>();
            var rootCanvas = g.canvas != null ? g.canvas.rootCanvas : null;
            var material = g.materialForRendering != null ? g.materialForRendering : g.material;
            var texture = g.mainTexture;
            var rectTransform = g.transform as RectTransform;
            var canvasRenderer = g.GetComponent<CanvasRenderer>();
            if (written > 0) sb.Append(",");
            sb.AppendLine();
            sb.Append(Indent(indent + 2)).Append("{");
            Inline(sb, "path", PathOf(g.gameObject), true);
            Inline(sb, "type", g.GetType().Name, true);
            Inline(sb, "activeInHierarchy", g.gameObject.activeInHierarchy, true);
            Inline(sb, "enabled", g.enabled, true);
            Inline(sb, "canvasPath", canvas != null ? PathOf(canvas.gameObject) : "", true);
            Inline(sb, "rootCanvasPath", rootCanvas != null ? PathOf(rootCanvas.gameObject) : "", true);
            Inline(sb, "canvasCamera", canvas != null && canvas.worldCamera != null ? PathOf(canvas.worldCamera.gameObject) : "", true);
            Inline(sb, "canvasRenderMode", canvas != null ? canvas.renderMode.ToString() : "", true);
            Inline(sb, "canvasSortingLayerId", canvas != null ? canvas.sortingLayerID : 0, true);
            Inline(sb, "canvasSortingOrder", canvas != null ? canvas.sortingOrder : 0, true);
            Inline(sb, "canvasEnabled", canvas != null && canvas.enabled, true);
            Inline(sb, "canvasScaleFactor", canvas != null ? canvas.scaleFactor : 0f, true);
            Inline(sb, "canvasReferencePixelsPerUnit", canvas != null ? canvas.referencePixelsPerUnit : 0f, true);
            Inline(sb, "depth", g.depth, true);
            Inline(sb, "raycastTarget", g.raycastTarget, true);
            Inline(sb, "materialName", material != null ? material.name : "", true);
            Inline(sb, "materialInstanceId", material != null ? material.GetInstanceID() : 0, true);
            Inline(sb, "shaderName", material != null && material.shader != null ? material.shader.name : "", true);
            Inline(sb, "renderQueue", material != null ? material.renderQueue : -1, true);
            Inline(sb, "mainTexture", texture != null ? texture.name + " " + texture.width + "x" + texture.height : "", true);
            Inline(sb, "textureName", texture != null ? texture.name : "", true);
            Inline(sb, "textureMeta", TextureSummary(texture), true);
            Inline(sb, "materialTextures", MaterialTexturesSummary(material, 12), true);
            Inline(sb, "spriteName", ReflectedObjectName(g, "sprite"), true);
            Inline(sb, "rect", rectTransform != null ? RectString(rectTransform.rect) : "", true);
            Inline(sb, "worldCorners", rectTransform != null ? WorldCornersString(rectTransform) : "", true);
            Inline(sb, "screenRect", rectTransform != null ? ScreenRectString(rectTransform, canvas) : "", true);
            Inline(sb, "maskState", MaskState(g), true);
            Inline(sb, "sortingGroup", SortingGroupSummary(g.gameObject), true);
            Inline(sb, "canvasRenderer", CanvasRendererSummary(canvasRenderer), true);
            Inline(sb, "isTextComponent", IsTextComponent(g), true);
            Inline(sb, "textLength", TextLength(g), true);
            Inline(sb, "fontName", ReflectedObjectName(g, "font"), true);
            Inline(sb, "fontMaterialName", ReflectedObjectName(g, "fontMaterial"), true);
            Inline(sb, "fontSharedMaterialName", ReflectedObjectName(g, "fontSharedMaterial"), true);
            Inline(sb, "material", MaterialSummary(material), true);
            Inline(sb, "materialFingerprint", MaterialFingerprint(material), false);
            sb.Append("}");
            written++;
        }
        if (written > 0) sb.AppendLine();
        sb.Append(Indent(indent)).Append("]");
        if (comma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendParticles(StringBuilder sb, int indent, bool comma)
    {
        var particles = SafeFindObjects<ParticleSystem>();
        sb.Append(Indent(indent)).Append("\"particles\": [");
        int written = 0;
        for (int i = 0; i < particles.Count && written < MaxParticles; i++)
        {
            var ps = particles[i];
            if (ps == null || !ps.gameObject.activeInHierarchy) continue;
            int alive = 0;
            try { alive = ps.particleCount; } catch { }
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (written > 0) sb.Append(",");
            sb.AppendLine();
            sb.Append(Indent(indent + 2)).Append("{");
            Inline(sb, "path", PathOf(ps.gameObject), true);
            Inline(sb, "isPlaying", ps.isPlaying, true);
            Inline(sb, "isEmitting", ps.isEmitting, true);
            Inline(sb, "aliveParticles", alive, true);
            Inline(sb, "rendererEnabled", renderer != null && renderer.enabled, true);
            Inline(sb, "maxParticles", ps.main.maxParticles, true);
            Inline(sb, "rendererMode", renderer != null ? renderer.renderMode.ToString() : "", true);
            Inline(sb, "sortingLayerId", renderer != null ? renderer.sortingLayerID : 0, true);
            Inline(sb, "sortingOrder", renderer != null ? renderer.sortingOrder : 0, true);
            Inline(sb, "sortingGroup", SortingGroupSummary(ps.gameObject), true);
            Inline(sb, "bounds", renderer != null ? BoundsString(renderer.bounds) : "", true);
            Inline(sb, "materialName", renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "", true);
            Inline(sb, "materialInstanceId", renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.GetInstanceID() : 0, true);
            Inline(sb, "shaderName", renderer != null && renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null ? renderer.sharedMaterial.shader.name : "", true);
            Inline(sb, "renderQueue", renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.renderQueue : -1, true);
            Inline(sb, "mainTexture", renderer != null ? MainTextureSummary(renderer.sharedMaterial) : "", true);
            Inline(sb, "textureName", renderer != null ? MainTextureName(renderer.sharedMaterial) : "", true);
            Inline(sb, "materialTextures", renderer != null ? MaterialTexturesSummary(renderer.sharedMaterial, 12) : "", true);
            Inline(sb, "material", renderer != null ? MaterialSummary(renderer.sharedMaterial) : "", true);
            Inline(sb, "materialFingerprint", renderer != null ? MaterialFingerprint(renderer.sharedMaterial) : "", false);
            sb.Append("}");
            written++;
        }
        if (written > 0) sb.AppendLine();
        sb.Append(Indent(indent)).Append("]");
        if (comma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendTextures(StringBuilder sb, int indent, bool comma)
    {
        var textures = CollectRuntimeTextures();
        sb.Append(Indent(indent)).Append("\"textures\": [");
        for (int i = 0; i < textures.Count && i < MaxTextures; i++)
        {
            if (i > 0) sb.Append(",");
            sb.AppendLine();
            AppendTextureObject(sb, textures[i], indent + 2);
        }
        if (textures.Count > 0) sb.AppendLine();
        sb.Append(Indent(indent)).Append("]");
        if (comma) sb.Append(",");
        sb.AppendLine();
    }

    private static void AppendTextureThumbnails(StringBuilder sb, int indent, bool comma)
    {
        var textures = CollectRuntimeTextures()
            .Where(t => t is RenderTexture)
            .OrderBy(t => t.name)
            .Take(MaxTextureThumbnails)
            .ToList();

        sb.Append(Indent(indent)).Append("\"textureThumbnails\": [");
        int written = 0;
        for (int i = 0; i < textures.Count; i++)
        {
            Texture texture = textures[i];
            if (!TryBuildTextureThumbnail(texture, TextureThumbnailSize, out string base64, out int width, out int height))
                continue;
            if (written > 0) sb.Append(",");
            sb.AppendLine();
            sb.Append(Indent(indent + 2)).Append("{");
            Inline(sb, "name", texture != null ? texture.name : "", true);
            Inline(sb, "type", texture != null ? texture.GetType().Name : "", true);
            Inline(sb, "instanceId", texture != null ? texture.GetInstanceID() : 0, true);
            Inline(sb, "width", texture != null ? texture.width : 0, true);
            Inline(sb, "height", texture != null ? texture.height : 0, true);
            Inline(sb, "thumbnailWidth", width, true);
            Inline(sb, "thumbnailHeight", height, true);
            Inline(sb, "pngBase64", base64, false);
            sb.Append("}");
            written++;
        }
        if (written > 0) sb.AppendLine();
        sb.Append(Indent(indent)).Append("]");
        if (comma) sb.Append(",");
        sb.AppendLine();
    }

    private static List<T> SafeFindObjects<T>() where T : UnityEngine.Object
    {
        try
        {
#if UNITY_2023_1_OR_NEWER
            return new List<T>(UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None));
#else
            return new List<T>(UnityEngine.Object.FindObjectsOfType<T>(true));
#endif
        }
        catch
        {
            return new List<T>();
        }
    }

    private static string MaterialsSummary(Material[] materials)
    {
        if (materials == null || materials.Length == 0) return "";
        var parts = new List<string>();
        for (int i = 0; i < materials.Length && i < 4; i++)
            parts.Add(MaterialSummary(materials[i]));
        return string.Join(" | ", parts);
    }

    private static string CurrentResolutionString()
    {
        try
        {
            Resolution resolution = Screen.currentResolution;
            string refresh = resolution.refreshRate.ToString(CultureInfo.InvariantCulture);
            try
            {
                PropertyInfo prop = typeof(Resolution).GetProperty("refreshRateRatio");
                object ratio = prop != null ? prop.GetValue(resolution, null) : null;
                PropertyInfo valueProp = ratio != null ? ratio.GetType().GetProperty("value") : null;
                object value = valueProp != null ? valueProp.GetValue(ratio, null) : null;
                if (value is double d) refresh = d.ToString("0.###", CultureInfo.InvariantCulture);
                else if (value is float f) refresh = f.ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch { }
            return resolution.width.ToString(CultureInfo.InvariantCulture) + "x" +
                   resolution.height.ToString(CultureInfo.InvariantCulture) + "@" + refresh;
        }
        catch
        {
            return "";
        }
    }

    private static int SafeCameraInt(Camera camera, string propertyName)
    {
        if (camera == null || string.IsNullOrEmpty(propertyName)) return 0;
        try
        {
            PropertyInfo prop = typeof(Camera).GetProperty(propertyName);
            object value = prop != null ? prop.GetValue(camera, null) : null;
            if (value is int i) return i;
        }
        catch { }
        return 0;
    }

    private static Component GetUniversalAdditionalCameraData(Camera camera)
    {
        if (camera == null) return null;
        try
        {
            return camera.GetComponents<Component>()
                .FirstOrDefault(c => c != null && c.GetType().Name.IndexOf("UniversalAdditionalCameraData", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch
        {
            return null;
        }
    }

    private static string DisplaysSummary()
    {
        var parts = new List<string>();
        try
        {
            var displays = Display.displays;
            for (int i = 0; displays != null && i < displays.Length; i++)
            {
                var display = displays[i];
                if (display == null) continue;
                parts.Add("display" + i.ToString(CultureInfo.InvariantCulture) +
                          ":system=" + display.systemWidth.ToString(CultureInfo.InvariantCulture) + "x" + display.systemHeight.ToString(CultureInfo.InvariantCulture) +
                          ";rendering=" + display.renderingWidth.ToString(CultureInfo.InvariantCulture) + "x" + display.renderingHeight.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch { }
        return string.Join(" | ", parts);
    }

    private static string SummarizeObjectFields(object target, int maxItems)
    {
        if (target == null) return "";
        var parts = new List<string>();
        Type type = target.GetType();
        try
        {
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (parts.Count >= maxItems) break;
                if (prop == null || prop.GetIndexParameters().Length != 0 || !prop.CanRead) continue;
                Type pt = prop.PropertyType;
                if (!IsSimpleType(pt) && !typeof(UnityEngine.Object).IsAssignableFrom(pt)) continue;
                object value;
                try { value = prop.GetValue(target, null); }
                catch { continue; }
                parts.Add(prop.Name + "=" + SimpleValue(value));
            }
        }
        catch { }
        return type.Name + "[" + string.Join(";", parts) + "]";
    }

    private static bool IsSimpleType(Type type)
    {
        if (type == null) return false;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);
    }

    private static string SimpleValue(object value)
    {
        if (value == null) return "";
        if (value is float f) return f.ToString("0.###", CultureInfo.InvariantCulture);
        if (value is double d) return d.ToString("0.###", CultureInfo.InvariantCulture);
        var obj = value as UnityEngine.Object;
        if (obj != null) return obj.name + "(" + obj.GetType().Name + ")";
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static string MaterialSummary(Material material)
    {
        if (material == null) return "";
        string shader = material.shader != null ? material.shader.name : "";
        int queue = material.renderQueue;
        return material.name + "[" + shader + ";q=" + queue.ToString(CultureInfo.InvariantCulture) + "]";
    }

    private static string MaterialFingerprint(Material material)
    {
        if (material == null) return "";
        return MaterialSummary(material) + ";id=" + material.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ";mainTex=" + MainTextureName(material) + ";textures=" + MaterialTexturesSummary(material, 8);
    }

    private static string MaterialTexturesSummary(Material material, int maxItems)
    {
        if (material == null) return "";
        var parts = new List<string>();
        try
        {
            foreach (string property in material.GetTexturePropertyNames())
            {
                if (parts.Count >= maxItems) break;
                Texture texture = material.GetTexture(property);
                if (texture == null) continue;
                parts.Add(property + "=" + TextureSummary(texture));
            }
        }
        catch { }
        return string.Join(" | ", parts);
    }

    private static string MainTextureName(Material material)
    {
        Texture texture = MainTexture(material);
        return texture != null ? texture.name : "";
    }

    private static string MainTextureSummary(Material material)
    {
        return TextureSummary(MainTexture(material));
    }

    private static Texture MainTexture(Material material)
    {
        if (material == null) return null;
        try { return material.mainTexture; } catch { return null; }
    }

    private static string MeshName(Renderer renderer)
    {
        if (renderer == null) return "";
        var skinned = renderer as SkinnedMeshRenderer;
        if (skinned != null && skinned.sharedMesh != null) return skinned.sharedMesh.name;
        var filter = renderer.GetComponent<MeshFilter>();
        return filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "";
    }

    private static int MeshInstanceId(Renderer renderer)
    {
        if (renderer == null) return 0;
        var skinned = renderer as SkinnedMeshRenderer;
        if (skinned != null && skinned.sharedMesh != null) return skinned.sharedMesh.GetInstanceID();
        var filter = renderer.GetComponent<MeshFilter>();
        return filter != null && filter.sharedMesh != null ? filter.sharedMesh.GetInstanceID() : 0;
    }

    private static List<Texture> CollectRuntimeTextures()
    {
        var result = new List<Texture>();
        var seen = new HashSet<int>();
        Action<Texture> add = texture =>
        {
            if (texture == null) return;
            int id = texture.GetInstanceID();
            if (seen.Add(id))
                result.Add(texture);
        };

        foreach (var camera in SafeFindObjects<Camera>())
            if (camera != null) add(camera.targetTexture);
        foreach (var renderer in SafeFindObjects<Renderer>())
        {
            if (renderer == null) continue;
            foreach (var material in renderer.sharedMaterials ?? new Material[0])
                CollectMaterialTextures(material, add);
        }
        foreach (var graphic in SafeFindObjects<Graphic>())
        {
            if (graphic == null) continue;
            try { add(graphic.mainTexture); } catch { }
            CollectMaterialTextures(graphic.material, add);
            CollectMaterialTextures(graphic.materialForRendering, add);
        }
        return result.OrderBy(t => t.name).ToList();
    }

    private static void CollectMaterialTextures(Material material, Action<Texture> add)
    {
        if (material == null || add == null) return;
        add(MainTexture(material));
        try
        {
            foreach (string property in material.GetTexturePropertyNames())
                add(material.GetTexture(property));
        }
        catch { }
    }

    private static void AppendTextureObject(StringBuilder sb, Texture texture, int indent)
    {
        sb.Append(Indent(indent)).Append("{");
        Inline(sb, "name", texture != null ? texture.name : "", true);
        Inline(sb, "type", texture != null ? texture.GetType().Name : "", true);
        Inline(sb, "instanceId", texture != null ? texture.GetInstanceID() : 0, true);
        Inline(sb, "width", texture != null ? texture.width : 0, true);
        Inline(sb, "height", texture != null ? texture.height : 0, true);
        Inline(sb, "dimension", texture != null ? texture.dimension.ToString() : "", true);
        Inline(sb, "format", TextureFormatName(texture), true);
        Inline(sb, "mipMapCount", texture != null ? texture.mipmapCount : 0, true);
        Inline(sb, "antiAliasing", texture is RenderTexture rt ? rt.antiAliasing : 1, true);
        Inline(sb, "depth", texture is RenderTexture rtDepth ? rtDepth.depth : 0, true);
        Inline(sb, "memoryBytes", TextureMemoryBytes(texture), false);
        sb.Append("}");
    }

    private static bool TryBuildTextureThumbnail(Texture texture, int maxSize, out string base64, out int width, out int height)
    {
        base64 = "";
        width = 0;
        height = 0;
        if (texture == null || texture.width <= 0 || texture.height <= 0 || maxSize <= 0)
            return false;

        RenderTexture previous = RenderTexture.active;
        RenderTexture temp = null;
        Texture2D readable = null;
        try
        {
            float scale = Math.Min(1f, maxSize / (float)Math.Max(texture.width, texture.height));
            width = Math.Max(1, Mathf.RoundToInt(texture.width * scale));
            height = Math.Max(1, Mathf.RoundToInt(texture.height * scale));
            temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Graphics.Blit(texture, temp);
            RenderTexture.active = temp;
            readable = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            readable.Apply(false, false);
            byte[] png = readable.EncodeToPNG();
            if (png == null || png.Length == 0)
                return false;
            base64 = Convert.ToBase64String(png);
            return true;
        }
        catch
        {
            base64 = "";
            width = 0;
            height = 0;
            return false;
        }
        finally
        {
            RenderTexture.active = previous;
            if (temp != null) RenderTexture.ReleaseTemporary(temp);
            if (readable != null) UnityEngine.Object.Destroy(readable);
        }
    }

    private static string TextureSummary(Texture texture)
    {
        if (texture == null) return "";
        return texture.name + "[" + texture.GetType().Name + ";" + texture.width.ToString(CultureInfo.InvariantCulture) + "x" + texture.height.ToString(CultureInfo.InvariantCulture) + ";" + TextureFormatName(texture) + ";mip=" + texture.mipmapCount.ToString(CultureInfo.InvariantCulture) + ";msaa=" + (texture is RenderTexture rt ? rt.antiAliasing : 1).ToString(CultureInfo.InvariantCulture) + "]";
    }

    private static string TextureFormatName(Texture texture)
    {
        if (texture == null) return "";
        if (texture is Texture2D texture2D) return texture2D.format.ToString();
        if (texture is RenderTexture renderTexture) return renderTexture.format + "/" + renderTexture.graphicsFormat;
        if (texture is Cubemap cubemap) return cubemap.format.ToString();
        return "";
    }

    private static int TextureMemoryBytes(Texture texture)
    {
        try
        {
            long bytes = texture != null ? UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture) : 0L;
            if (bytes > int.MaxValue) return int.MaxValue;
            return (int)Math.Max(0L, bytes);
        }
        catch
        {
            return 0;
        }
    }

    private static string LayerMaskNames(int mask)
    {
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            string name = LayerMask.LayerToName(i);
            names.Add(string.IsNullOrEmpty(name) ? i.ToString(CultureInfo.InvariantCulture) : name);
        }
        return string.Join("|", names);
    }

    private static string SortingGroupSummary(GameObject go)
    {
        if (go == null) return "";
        try
        {
            var group = go.GetComponentInParent<UnityEngine.Rendering.SortingGroup>();
            if (group == null) return "";
            return PathOf(group.gameObject) +
                   ";layerId=" + group.sortingLayerID.ToString(CultureInfo.InvariantCulture) +
                   ";order=" + group.sortingOrder.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return "";
        }
    }

    private static string MaskState(Graphic graphic)
    {
        if (graphic == null) return "";
        var parts = new List<string>();
        try
        {
            foreach (var component in graphic.GetComponentsInParent<Component>(true))
            {
                if (component == null) continue;
                string type = component.GetType().Name;
                if (type == "Mask" || type == "RectMask2D")
                    parts.Add(type + "@" + PathOf(component.gameObject));
            }
            object maskable = GetReflectedValue(graphic, "maskable");
            if (maskable is bool b && b)
                parts.Add("maskable=true");
        }
        catch { }
        return parts.Count == 0 ? "no-mask" : string.Join("|", parts);
    }

    private static string CanvasRendererSummary(CanvasRenderer canvasRenderer)
    {
        if (canvasRenderer == null) return "";
        var parts = new List<string>();
        try { parts.Add("materialCount=" + canvasRenderer.materialCount.ToString(CultureInfo.InvariantCulture)); } catch { }
        try { parts.Add("popMaterialCount=" + canvasRenderer.popMaterialCount.ToString(CultureInfo.InvariantCulture)); } catch { }
        try { parts.Add("hasMoved=" + canvasRenderer.hasMoved); } catch { }
        try { parts.Add("cull=" + canvasRenderer.cull); } catch { }
        return string.Join(";", parts);
    }

    private static string ReflectedObjectName(object target, string propertyName)
    {
        if (target == null || string.IsNullOrEmpty(propertyName)) return "";
        try
        {
            var property = target.GetType().GetProperty(propertyName);
            object value = property != null ? property.GetValue(target, null) : null;
            var obj = value as UnityEngine.Object;
            return obj != null ? obj.name : "";
        }
        catch
        {
            return "";
        }
    }

    private static object GetReflectedValue(object target, string propertyName)
    {
        if (target == null || string.IsNullOrEmpty(propertyName)) return null;
        try
        {
            var property = target.GetType().GetProperty(propertyName);
            return property != null ? property.GetValue(target, null) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTextComponent(Graphic graphic)
    {
        if (graphic == null) return false;
        string type = graphic.GetType().FullName ?? graphic.GetType().Name;
        return type.IndexOf("Text", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("TMP", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int TextLength(Graphic graphic)
    {
        if (graphic == null) return 0;
        try
        {
            var property = graphic.GetType().GetProperty("text");
            string text = property != null ? property.GetValue(graphic, null) as string : null;
            return string.IsNullOrEmpty(text) ? 0 : text.Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string WorldCornersString(RectTransform rectTransform)
    {
        if (rectTransform == null) return "";
        var corners = new Vector3[4];
        rectTransform.GetWorldCorners(corners);
        return Vec3(corners[0]) + "|" + Vec3(corners[1]) + "|" + Vec3(corners[2]) + "|" + Vec3(corners[3]);
    }

    private static string ScreenRectString(RectTransform rectTransform, Canvas canvas)
    {
        if (rectTransform == null) return "";
        var corners = new Vector3[4];
        rectTransform.GetWorldCorners(corners);
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 max = min;
        for (int i = 1; i < 4; i++)
        {
            Vector2 p = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        return min.x.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               min.y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               (max.x - min.x).ToString("0.###", CultureInfo.InvariantCulture) + "," +
               (max.y - min.y).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string PathOf(GameObject go)
    {
        if (go == null) return "";
        var names = new List<string>();
        var t = go.transform;
        while (t != null)
        {
            names.Add(t.name);
            t = t.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static string BoundsString(Bounds b)
    {
        return Vec3(b.center) + " size=" + Vec3(b.size);
    }

    private static string RectString(Rect r)
    {
        return r.x.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               r.y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               r.width.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               r.height.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Vec3(Vector3 v)
    {
        return v.x.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               v.y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
               v.z.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Indent(int count)
    {
        return new string(' ', count);
    }

    private static void AppendLine(StringBuilder sb, string line)
    {
        sb.AppendLine(line);
    }

    private static void Prop(StringBuilder sb, string name, string value, bool comma, int indent)
    {
        sb.Append(Indent(indent)).Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
        if (comma) sb.Append(',');
        sb.AppendLine();
    }

    private static void Prop(StringBuilder sb, string name, int value, bool comma, int indent)
    {
        sb.Append(Indent(indent)).Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        if (comma) sb.Append(',');
        sb.AppendLine();
    }

    private static void Prop(StringBuilder sb, string name, bool value, bool comma, int indent)
    {
        sb.Append(Indent(indent)).Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
        if (comma) sb.Append(',');
        sb.AppendLine();
    }

    private static void Inline(StringBuilder sb, string name, string value, bool comma)
    {
        sb.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
        if (comma) sb.Append(',');
    }

    private static void Inline(StringBuilder sb, string name, int value, bool comma)
    {
        sb.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        if (comma) sb.Append(',');
    }

    private static void Inline(StringBuilder sb, string name, float value, bool comma)
    {
        sb.Append('"').Append(name).Append("\":").Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        if (comma) sb.Append(',');
    }

    private static void Inline(StringBuilder sb, string name, bool value, bool comma)
    {
        sb.Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
        if (comma) sb.Append(',');
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
#endif
