using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace Runaway.App
{
    /// <summary>Builds the whole app at runtime: lighting, camera, post-processing, site, interface.</summary>
    public sealed class Bootstrap : MonoBehaviour
    {
        GameController _gc;
        SiteView _site;
        Ui _ui;
        CameraRig _rig;
        UIDocument _doc;

        void Awake()
        {
            Application.targetFrameRate = 60;
            Application.runInBackground = true;
            Environment();
            _rig = BuildCamera();
            BuildPost();
            _gc = new GameObject("Game").AddComponent<GameController>();
            _site = new GameObject("Site").AddComponent<SiteView>();
            _site.Build(_gc);
            var uiGo = new GameObject("UI");
            _doc = uiGo.AddComponent<UIDocument>();
            _doc.panelSettings = Resources.Load<PanelSettings>("UI/Panel");
            _ui = uiGo.AddComponent<Ui>();
            _ui.Doc = _doc; _ui.Rig = _rig; _ui.Site = _site;
        }

        void Start()
        {
            _ui.Build();
            var args = System.Environment.GetCommandLineArgs();
            int pv = Array.IndexOf(args, "-preview");
            if (pv >= 0)
            {
                var dir = pv + 1 < args.Length ? args[pv + 1] : "preview";
                int ci = Array.IndexOf(args, "-cards");
                var cards = ci >= 0 && ci + 1 < args.Length ? args[ci + 1] : null;
                var p = gameObject.AddComponent<Preview>();
                StartCoroutine(p.Run(dir, cards, _gc, _ui, _site, _rig));
                return;
            }
            int i = Array.IndexOf(args, "-capture");
            if (i >= 0)
            {
                var dir = i + 1 < args.Length ? args[i + 1] : "capture";
                var d = gameObject.AddComponent<Director>();
                d.Begin(dir, _gc, _ui, _site, _rig);
            }
        }

        static void Environment()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Fx.Hex("#3a4864");
            RenderSettings.ambientEquatorColor = Fx.Hex("#262e3c");
            RenderSettings.ambientGroundColor = Fx.Hex("#14161a");
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = Fx.Hex("#121823");
            RenderSettings.fogDensity = 0.0016f;
            RenderSettings.skybox = null;

            var moon = new GameObject("Moon").AddComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = Fx.Hex("#a6b8d8");
            moon.intensity = 0.9f;
            moon.shadows = LightShadows.Soft;
            moon.shadowStrength = 0.75f;
            moon.transform.rotation = Quaternion.Euler(36, 128, 0);
        }

        static CameraRig BuildCamera()
        {
            var go = new GameObject("Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Fx.Hex("#121823");
            cam.fieldOfView = 36;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 2200;
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;
            go.AddComponent<AudioListener>();
            return go.AddComponent<CameraRig>();
        }

        static void BuildPost()
        {
            var go = new GameObject("Post");
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            var p = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = p.Add<Bloom>(true);
            bloom.intensity.Override(0.85f); bloom.threshold.Override(0.95f); bloom.scatter.Override(0.74f);
            var tone = p.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);
            var ca = p.Add<ColorAdjustments>(true);
            ca.postExposure.Override(0.9f); ca.contrast.Override(14f); ca.saturation.Override(-6f);
            var wb = p.Add<WhiteBalance>(true);
            wb.temperature.Override(-6f);
            var vig = p.Add<Vignette>(true);
            vig.intensity.Override(0.26f); vig.smoothness.Override(0.5f);
            var grain = p.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Thin1); grain.intensity.Override(0.12f);
            vol.profile = p;
        }
    }
}
