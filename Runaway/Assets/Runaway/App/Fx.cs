using System.Collections.Generic;
using UnityEngine;

namespace Runaway.App
{
    /// <summary>Procedural textures, meshes and a material cache. Shaders come from assets in Resources so builds include them.</summary>
    public static class Fx
    {
        static Material _lit, _litEmissive, _alpha, _add, _plume;
        static readonly Dictionary<string, Material> _cache = new Dictionary<string, Material>();
        static Texture2D _puff, _flame, _dot, _ring, _ground, _gravel, _noiseTex;
        static Mesh _cone, _gable, _quad;

        static Material Base(ref Material m, string name)
        {
            if (m == null) m = Resources.Load<Material>("Materials/" + name);
            return m;
        }

        public static Material Lit(Color c, float smooth = 0.25f, float metal = 0f)
        {
            var key = $"lit{c}{smooth}{metal}";
            if (_cache.TryGetValue(key, out var m)) return m;
            m = new Material(Base(ref _lit, "Lit"));
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", metal);
            m.enableInstancing = true;
            _cache[key] = m;
            return m;
        }

        public static Material Emissive(Color baseColor, Color emission, float intensity)
        {
            var key = $"em{baseColor}{emission}{intensity}";
            if (_cache.TryGetValue(key, out var m)) return m;
            m = new Material(Base(ref _litEmissive, "LitEmissive"));
            m.SetColor("_BaseColor", baseColor);
            m.SetColor("_EmissionColor", emission * intensity);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            _cache[key] = m;
            return m;
        }

        /// <summary>Unique emissive material for things that animate (status lights, charring, glow).</summary>
        public static Material EmissiveInstance(Color baseColor)
        {
            var m = new Material(Base(ref _litEmissive, "LitEmissive"));
            m.SetColor("_BaseColor", baseColor);
            m.SetColor("_EmissionColor", Color.black);
            m.EnableKeyword("_EMISSION");
            m.SetFloat("_Smoothness", 0.3f);
            return m;
        }

        public static Material Particle(Texture2D tex, bool additive, float soft = 2f)
        {
            var m = new Material(additive ? Base(ref _add, "ParticleAdd") : Base(ref _alpha, "ParticleAlpha"));
            m.SetTexture("_MainTex", tex);
            m.SetFloat("_SoftFade", soft);
            return m;
        }

        public static Material Plume() => new Material(Base(ref _plume, "Plume"));

        // ------------------------------------------------------------------ textures

        public static Texture2D Puff => _puff ?? (_puff = MakePuff(128));
        public static Texture2D Flame => _flame ?? (_flame = MakeFlame(128));
        public static Texture2D Dot => _dot ?? (_dot = MakeDot(32));
        public static Texture2D Ring => _ring ?? (_ring = MakeRing(256));
        public static Texture2D Noise => _noiseTex ?? (_noiseTex = MakeNoise(256));

        static Texture2D NewTex(int n, TextureFormat f = TextureFormat.RGBA32, bool mips = true)
        {
            var t = new Texture2D(n, n, f, mips) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            return t;
        }

        static float Fbm(float x, float y, int oct = 4)
        {
            float v = 0, a = 0.5f, f = 1;
            for (int i = 0; i < oct; i++) { v += a * Mathf.PerlinNoise(x * f + 31.7f * i, y * f + 17.3f * i); f *= 2; a *= 0.5f; }
            return v;
        }

        static Texture2D MakePuff(int n)
        {
            var t = NewTex(n);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2 - 1, v = (y + 0.5f) / n * 2 - 1;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float nse = Fbm(x * 0.045f, y * 0.045f, 5);
                    float a = Mathf.Clamp01(1 - r * (0.85f + 0.45f * nse));
                    a = Mathf.SmoothStep(0, 1, a) * Mathf.Lerp(0.55f, 1f, nse);
                    byte g = (byte)(205 + 50 * nse);
                    px[y * n + x] = new Color32(g, g, g, (byte)(a * 255));
                }
            t.SetPixels32(px); t.Apply(true);
            return t;
        }

        static Texture2D MakeFlame(int n)
        {
            var t = NewTex(n);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2 - 1, v = (y + 0.5f) / n * 2 - 1;
                    float r = Mathf.Sqrt(u * u * 1.6f + v * v * 0.9f);
                    float nse = Fbm(x * 0.06f, y * 0.06f, 4);
                    float a = Mathf.Clamp01(1 - r * (0.9f + 0.5f * nse));
                    a = a * a * (3 - 2 * a);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            t.SetPixels32(px); t.Apply(true);
            return t;
        }

        static Texture2D MakeDot(int n)
        {
            var t = NewTex(n);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2 - 1, v = (y + 0.5f) / n * 2 - 1;
                    float a = Mathf.Clamp01(1 - Mathf.Sqrt(u * u + v * v));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * a * 255));
                }
            t.SetPixels32(px); t.Apply(true);
            return t;
        }

        static Texture2D MakeRing(int n)
        {
            var t = NewTex(n);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2 - 1, v = (y + 0.5f) / n * 2 - 1;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float ring = Mathf.Exp(-Mathf.Pow((r - 0.86f) / 0.05f, 2));
                    float fill = Mathf.Clamp01(1 - r) * 0.35f;
                    float a = Mathf.Clamp01(ring + fill) * (r < 1 ? 1 : 0);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            t.SetPixels32(px); t.Apply(true);
            return t;
        }

        static Texture2D MakeNoise(int n)
        {
            var t = NewTex(n);
            t.wrapMode = TextureWrapMode.Repeat;
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float f = Fbm(x / (float)n * 8, y / (float)n * 8, 5);
                    byte b = (byte)(f * 255);
                    px[y * n + x] = new Color32(b, b, b, 255);
                }
            t.SetPixels32(px); t.Apply(true);
            return t;
        }

        /// <summary>Tileable ground texture: dry grass and dirt at night.</summary>
        public static Texture2D GroundTex(Color a, Color b, Color c, float scale, int n = 512)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float fx = x / (float)n, fy = y / (float)n;
                    // tileable fbm by blending four offsets
                    float s = TileFbm(fx, fy, scale);
                    float d = TileFbm(fx + 0.37f, fy + 0.11f, scale * 6.3f);
                    var col = Color.Lerp(a, b, Mathf.SmoothStep(0.25f, 0.75f, s));
                    col = Color.Lerp(col, c, Mathf.Clamp01((d - 0.55f) * 3f));
                    col *= 0.9f + 0.2f * TileFbm(fx, fy, scale * 18f);
                    px[y * n + x] = col;
                }
            t.SetPixels(px); t.Apply(true);
            return t;
        }

        static float TileFbm(float x, float y, float s)
        {
            float a = Fbm(x * s, y * s), b = Fbm((x - 1) * s, y * s), c = Fbm(x * s, (y - 1) * s), d = Fbm((x - 1) * s, (y - 1) * s);
            float wx = x, wy = y;
            return Mathf.Lerp(Mathf.Lerp(a, b, wx), Mathf.Lerp(c, d, wx), wy);
        }

        // ------------------------------------------------------------------ meshes

        public static Mesh Cone => _cone ?? (_cone = MakeCone(9));
        public static Mesh Gable => _gable ?? (_gable = MakeGable());
        public static Mesh Quad => _quad ?? (_quad = MakeQuad());

        static Mesh MakeCone(int seg)
        {
            var m = new Mesh();
            var v = new List<Vector3>(); var tri = new List<int>();
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2 / seg, a1 = (i + 1) * Mathf.PI * 2 / seg;
                int b = v.Count;
                v.Add(new Vector3(Mathf.Cos(a0) * 0.5f, 0, Mathf.Sin(a0) * 0.5f));
                v.Add(new Vector3(0, 1, 0));
                v.Add(new Vector3(Mathf.Cos(a1) * 0.5f, 0, Mathf.Sin(a1) * 0.5f));
                tri.Add(b); tri.Add(b + 1); tri.Add(b + 2);
            }
            m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        /// <summary>Unit gable roof prism: 1 wide (x), 1 long (z), 1 tall, sitting on y=0.</summary>
        static Mesh MakeGable()
        {
            var m = new Mesh();
            var p = new[]
            {
                new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 0, -0.5f), new Vector3(0, 1, -0.5f),
                new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0, 1, 0.5f),
            };
            var v = new List<Vector3>(); var tri = new List<int>();
            void Face(params int[] idx) { int b = v.Count; foreach (var i in idx) v.Add(p[i]); for (int k = 1; k < idx.Length - 1; k++) { tri.Add(b); tri.Add(b + k); tri.Add(b + k + 1); } }
            Face(0, 2, 1); Face(3, 4, 5); Face(0, 3, 5, 2); Face(1, 2, 5, 4);
            m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        static Mesh MakeQuad()
        {
            var m = new Mesh();
            m.SetVertices(new List<Vector3> { new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 0, -0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(-0.5f, 0, 0.5f) });
            m.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            m.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ helpers

        public static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat, bool shadows = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        public static GameObject Cyl(Transform parent, string name, Vector3 pos, Vector3 size, Material mat, bool shadows = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        public static GameObject MeshObj(Transform parent, string name, Mesh mesh, Vector3 pos, Vector3 scale, Material mat, Quaternion? rot = null, bool shadows = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            if (rot.HasValue) go.transform.localRotation = rot.Value;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        public static Color Hex(string hex) { ColorUtility.TryParseHtmlString(hex, out var c); return c; }
    }
}
