using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Runaway.EditorTools
{
    /// <summary>Creates the assets the runtime loads from Resources, the scene, and the macOS player.</summary>
    public static class RunawayBuild
    {
        const string Res = "Assets/Runaway/Resources";

        public static void All()
        {
            try
            {
                Setup();
                Build();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("RunawayBuild failed: " + e);
                EditorApplication.Exit(1);
            }
        }

        public static void SetupOnly()
        {
            try { Setup(); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogError("RunawayBuild setup failed: " + e); EditorApplication.Exit(1); }
        }

        public static void Setup()
        {
            Directory.CreateDirectory(Res + "/Materials");
            Mat("Lit", Shader.Find("Universal Render Pipeline/Lit"), m => { m.SetFloat("_Smoothness", 0.3f); });
            Mat("LitEmissive", Shader.Find("Universal Render Pipeline/Lit"), m =>
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", Color.white);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            });
            Mat("ParticleAlpha", Shader.Find("Runaway/ParticleAlpha"), null);
            Mat("ParticleAdd", Shader.Find("Runaway/ParticleAdd"), null);
            Mat("Plume", Shader.Find("Runaway/Plume"), null);

            foreach (var w in new[] { "Regular", "Medium", "SemiBold" }) Font(w);
            Panel();
            Scene();
            Pipeline();
            Player();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("RunawayBuild setup complete");
        }

        static void Mat(string name, Shader shader, Action<Material> init)
        {
            if (shader == null) throw new Exception("Missing shader for " + name);
            var path = $"{Res}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
            m.shader = shader;
            init?.Invoke(m);
            EditorUtility.SetDirty(m);
        }

        static void Font(string weight)
        {
            var src = AssetDatabase.LoadAssetAtPath<UnityEngine.Font>($"{Res}/Fonts/Barlow-{weight}.ttf");
            if (src == null) throw new Exception("Missing font " + weight);
            var path = $"{Res}/Fonts/Barlow{weight}.asset";
            if (AssetDatabase.LoadAssetAtPath<FontAsset>(path) != null) return;
            var fa = FontAsset.CreateFontAsset(src, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            fa.name = "Barlow" + weight;
            AssetDatabase.CreateAsset(fa, path);
            foreach (var t in fa.atlasTextures) { t.name = fa.name + " Atlas"; AssetDatabase.AddObjectToAsset(t, fa); }
            fa.material.name = fa.name + " Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            EditorUtility.SetDirty(fa);
        }

        static void Panel()
        {
            var tssPath = $"{Res}/UI/Theme.tss";
            if (!File.Exists(tssPath)) { File.WriteAllText(tssPath, "@import url(\"unity-theme://default\");\n"); AssetDatabase.ImportAsset(tssPath); }
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(tssPath);
            var path = $"{Res}/UI/Panel.asset";
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (ps == null) { ps = ScriptableObject.CreateInstance<PanelSettings>(); AssetDatabase.CreateAsset(ps, path); }
            ps.themeStyleSheet = theme;
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1920, 1080);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 1f;
            EditorUtility.SetDirty(ps);
        }

        static void Scene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            var path = "Assets/Scenes/Main.unity";
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Bootstrap");
            go.AddComponent<Runaway.App.Bootstrap>();
            EditorSceneManager.SaveScene(scene, path);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(path, true) };
        }

        static void Pipeline()
        {
            var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            rp.supportsCameraDepthTexture = true;
            rp.supportsHDR = true;
            rp.msaaSampleCount = 4;
            rp.shadowDistance = 260;
            rp.shadowCascadeCount = 4;
            rp.maxAdditionalLightsCount = 8;
            EditorUtility.SetDirty(rp);
            GraphicsSettings.defaultRenderPipeline = rp;
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = rp;
            }
            QualitySettings.SetQualityLevel(QualitySettings.names.Length - 1, true);
            var so = new SerializedObject(rp);
            var res = so.FindProperty("m_MainLightShadowmapResolution"); if (res != null) res.intValue = 4096;
            var soft = so.FindProperty("m_SoftShadowsSupported"); if (soft != null) soft.boolValue = true;
            var addShadows = so.FindProperty("m_AdditionalLightShadowsSupported"); if (addShadows != null) addShadows.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void Player()
        {
            PlayerSettings.productName = "Runaway";
            PlayerSettings.companyName = "Sidhaarth Krishnan";
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone, "me.realsid08.runaway");
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.macRetinaSupport = true;
            PlayerSettings.colorSpace = ColorSpace.Linear;
        }

        public static void Build()
        {
            var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var outPath = Path.Combine(repo, "build/Runaway.app");
            var opts = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = outPath,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Debug.Log($"RunawayBuild result {report.summary.result} size {report.summary.totalSize / 1e6:F0} MB errors {report.summary.totalErrors}");
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("Player build failed");
        }
    }
}
