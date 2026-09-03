using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BoatMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class BoatModPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "mateusz.energyboatsimulator.custommodel";
        public const string PluginName = "BoatModelSwap";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;
        internal static BoatModPlugin Instance;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _hideOriginal;
        private ConfigEntry<bool> _autoFit;
        private ConfigEntry<float> _scale;
        private ConfigEntry<float> _offsetX, _offsetY, _offsetZ;
        private ConfigEntry<float> _rotY;
        private ConfigEntry<string> _modelPath;

        private List<MeshGroup> _groups;
        private float _timer;
        private int _beats;
        private string _resolvedType;
        private Type _boatType;
        private bool _probeLogged;
        private bool _dumped;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            _enabled = Config.Bind("General", "Enabled", true, "Master switch");
            _hideOriginal = Config.Bind("General", "HideOriginal", true, "Hide the original boat meshes");
            _autoFit = Config.Bind("Model", "AutoFit", true, "Auto-scale model to original boat footprint");
            _scale = Config.Bind("Model", "Scale", 1f, "Extra scale multiplier on top of AutoFit");
            _offsetX = Config.Bind("Model", "OffsetX", 0f, "Local position offset X (meters)");
            _offsetY = Config.Bind("Model", "OffsetY", 0f, "Local position offset Y (meters)");
            _offsetZ = Config.Bind("Model", "OffsetZ", 0f, "Local position offset Z (meters)");
            _rotY = Config.Bind("Model", "RotY", 0f, "Yaw rotation of the model in degrees");
            _modelPath = Config.Bind("Model", "Path",
                Path.Combine(Paths.PluginPath, "BoatMod", "boat.obj"),
                "OBJ file to load (expects matching .mtl next to it)");

            try
            {
                _groups = ObjLoader.Load(_modelPath.Value, Path.ChangeExtension(_modelPath.Value, ".mtl"), Log);
            }
            catch (Exception e)
            {
                Log.LogError($"[BoatMod] failed to load model: {e.Message}");
                return;
            }

            if (_groups.Count == 0)
            {
                Log.LogError("[BoatMod] model loaded but contained no geometry");
                return;
            }

            try
            {
                PatchGameMethods();
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += (_, __) =>
                {
                    try
                    {
                        Log.LogInfo("[BoatMod] scene loaded, rescanning");
                        PatchGameMethods();
                        Scan();
                    }
                    catch (Exception e) { Log.LogError($"[BoatMod] scene hook failed: {e.Message}"); }
                };
                Log.LogInfo("[BoatMod] game hooks installed");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] hook install failed, polling only: {e.Message}");
            }

            _timer = 2f;
            Log.LogInfo("[BoatMod] loaded, scanner running on plugin component");
        }

        private Harmony _harmony;
        private bool _methodsPatched;
        private int _tickCounter;

        private void PatchGameMethods()
        {
            if (_methodsPatched) return;
            _harmony ??= new Harmony(PluginGuid);
            int hooked = 0;
            foreach (var typeName in new[] { "BoatController", "BoatInputActionsHandler", "BoatInputHandler", "PlayerSynchronizer", "GameManager" })
            {
                var t = FindType(typeName);
                if (t == null) continue;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsGenericMethodDefinition || m.GetParameters().Length != 0) continue;
                    if (m.Name == "Start" || m.Name == "Awake" || m.Name == "OnEnable")
                    { if (PatchMethod(m, nameof(BoatSpawnedHook))) hooked++; }
                    else if (m.Name == "Update" || m.Name == "LateUpdate")
                    { if (PatchMethod(m, nameof(GameTickHook))) hooked++; }
                }
            }
            if (hooked > 0) _methodsPatched = true;
            Log.LogInfo($"[BoatMod] game method scan done, {hooked} hooks installed");
        }

        private bool PatchMethod(MethodInfo target, string hookName)
        {
            try
            {
                var hook = typeof(BoatModPlugin).GetMethod(hookName, BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(target, postfix: new HarmonyMethod(hook));
                Log.LogInfo($"[BoatMod] hooked {target.DeclaringType.Name}.{target.Name}");
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning($"[BoatMod] hook failed on {target.DeclaringType.Name}.{target.Name}: {e.Message}");
                return false;
            }
        }

        private static void BoatSpawnedHook(object __instance)
        {
            try
            {
                Log?.LogInfo("[BoatMod] boat lifecycle method intercepted");
                Instance?.TrySwap(__instance as Component, true);
            }
            catch { }
        }

        private static void GameTickHook(object __instance)
        {
            try
            {
                var self = Instance;
                if (self == null || self._groups == null || !self._enabled.Value) return;
                if (++self._tickCounter % 60 == 0) self.Scan();
            }
            catch { }
        }

        private void Update()
        {
            if (_groups == null || !_enabled.Value) return;
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f) return;
            _timer = 1f;
            _beats++;
            if (_beats % 10 == 0 && _boatType == null)
                Log.LogInfo($"[BoatMod] heartbeat #{_beats}, still no boat type resolved");
            Scan();
        }

        internal void TrySwap(Component comp, bool requireMine)
        {
            if (!_enabled.Value) return;
            if (comp == null) return;
            var root = comp.transform.root.gameObject;
            if (root.GetComponent<BoatModMarker>() != null) return;
            if (requireMine && !IsMine(root)) return;
            try
            {
                ApplySwap(root);
            }
            catch (Exception e)
            {
                Log.LogError($"[BoatMod] swap failed on '{root.name}': {e}");
                root.AddComponent<BoatModMarker>();
            }
        }

        private void Scan()
        {
            if (_boatType == null)
            {
                var candidates = new[]
                {
                    "BoatInputActionsHandler", "BoatInputHandler",
                    "BoatController", "PlayerSynchronizer"
                };
                foreach (var name in candidates)
                {
                    var t = FindType(name);
                    if (t == null) continue;
                    var comps = FindObjectsOfType(t);
                    if (comps.Length == 0) continue;
                    _boatType = t;
                    _resolvedType = name;
                    Log.LogInfo($"[BoatMod] using type '{name}', {comps.Length} instance(s) found");
                    if (!_probeLogged && comps[0] is Component c0)
                    {
                        _probeLogged = true;
                        ProbeRoot(c0);
                    }
                    break;
                }
                if (_boatType == null)
                {
                    if (_beats > 15 && !_dumped)
                    {
                        _dumped = true;
                        DumpScene();
                    }
                    return;
                }
            }

            bool mineOnly = _resolvedType == "BoatController" || _resolvedType == "PlayerSynchronizer";
            foreach (var obj in FindObjectsOfType(_boatType))
                TrySwap(obj as Component, mineOnly);
        }

        private static Type FindType(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(simpleName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private void DumpScene()
        {
            Log.LogInfo("[BoatMod] scene dump start");
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int s = 0; s < sceneCount; s++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                Log.LogInfo($"[BoatMod] scene '{scene.name}':");
                foreach (var root in scene.GetRootGameObjects())
                {
                    var ln = root.name.ToLowerInvariant();
                    bool interesting = ln.Contains("boat") || ln.Contains("hull") ||
                                       ln.Contains("player") || ln.Contains("preview") ||
                                       ln.Contains("ship") || ln.Contains("vessel");
                    if (interesting) DumpNode(root.transform, 1);
                    else Log.LogInfo($"[BoatMod] root: '{root.name}'");
                }
            }
            Log.LogInfo("[BoatMod] scene dump end");
        }

        private void DumpNode(Transform t, int depth)
        {
            if (!t.gameObject.activeInHierarchy) return;
            if (depth > 3) return;
            var comps = t.GetComponents<Component>();
            var names = comps.Where(c => c != null).Select(c => c.GetType().Name);
            Log.LogInfo($"[BoatMod] {new string(' ', depth * 2)}'{t.name}' [{string.Join(", ", names)}]");
            foreach (Transform child in t)
                DumpNode(child, depth + 1);
        }

        private void ProbeRoot(Component comp)
        {
            var root = comp.transform.root;
            Log.LogInfo($"[BoatMod] probe: root='{root.name}' path='{GetPath(comp.transform)}'");
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null && !string.IsNullOrEmpty(c.name))
                    Log.LogInfo($"[BoatMod]   go='{GetPath(c.transform)}' comp='{c.GetType().FullName}'");
        }

        private static string GetPath(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }

        private static bool IsMine(GameObject go)
        {
            var pv = go.GetComponent("PhotonView") as Behaviour;
            if (pv == null)
                pv = go.GetComponentInChildren(FindType("PhotonView")) as Behaviour;
            if (pv == null) return true;
            var t = pv.GetType();
            var prop = t.GetProperty("isMine") ?? t.GetProperty("IsMine");
            if (prop != null) return Equals(true, prop.GetValue(pv, null));
            var field = t.GetField("isMine") ?? t.GetField("IsMine");
            if (field != null) return Equals(true, field.GetValue(pv));
            return true;
        }

        private void ApplySwap(GameObject root)
        {
            var originals = root.GetComponentsInChildren<Renderer>(true)
                .Where(r => !(r is ParticleSystemRenderer) && !(r is TrailRenderer))
                .Where(r => r.transform.GetComponentInParent<BoatModMarker>() == null)
                .ToList();

            if (originals.Count == 0)
            {
                Log.LogWarning($"[BoatMod] '{root.name}' has no renderers yet, will retry");
                return;
            }

            var origBounds = originals[0].bounds;
            foreach (var r in originals) origBounds.Encapsulate(r.bounds);

            var model = new GameObject("BoatMod_CustomModel");
            model.transform.SetParent(root.transform, false);
            model.transform.localRotation = Quaternion.Euler(0f, _rotY.Value, 0f);

            foreach (var g in _groups)
            {
                var part = new GameObject(string.IsNullOrEmpty(g.MatName) ? "part" : g.MatName);
                part.transform.SetParent(model.transform, false);
                var mf = part.AddComponent<MeshFilter>();
                mf.sharedMesh = g.ToMesh();
                var mr = part.AddComponent<MeshRenderer>();
                mr.sharedMaterial = MakeMaterial(g);
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            var ourLocal = new Bounds();
            var allFilters = model.GetComponentsInChildren<MeshFilter>();
            bool firstB = true;
            foreach (var f in allFilters)
            {
                if (firstB) { ourLocal = f.sharedMesh.bounds; firstB = false; }
                else ourLocal.Encapsulate(f.sharedMesh.bounds);
            }

            float extraScale = 1f;
            if (_autoFit.Value)
            {
                float origL = Mathf.Max(origBounds.size.x, origBounds.size.z);
                float ourL = Mathf.Max(0.001f, Mathf.Max(ourLocal.size.x, ourLocal.size.z));
                extraScale = origL / ourL;
            }
            extraScale *= _scale.Value;
            model.transform.localScale = new Vector3(extraScale, extraScale, extraScale);

            model.transform.position = root.transform.position;

            var ourRenderers = model.GetComponentsInChildren<MeshRenderer>();
            var ourBounds = ourRenderers[0].bounds;
            foreach (var r in ourRenderers) ourBounds.Encapsulate(r.bounds);

            var fix = new Vector3(
                origBounds.center.x - ourBounds.center.x,
                origBounds.min.y - ourBounds.min.y,
                origBounds.center.z - ourBounds.center.z);
            model.transform.position += fix + root.transform.TransformVector(
                new Vector3(_offsetX.Value, _offsetY.Value, _offsetZ.Value));

            if (_hideOriginal.Value)
            {
                foreach (var r in originals)
                {
                    if (r == null) continue;
                    var sn = r.sharedMaterial != null && r.sharedMaterial.shader != null
                        ? r.sharedMaterial.shader.name : "";
                    if (sn.Contains("TextMesh") || sn.StartsWith("GUI/")) continue;
                    r.enabled = false;
                }
            }

            root.AddComponent<BoatModMarker>();
            Log.LogInfo(
                $"[BoatMod] swapped visuals on '{root.name}' " +
                $"(orig bounds {origBounds.size}, scale {extraScale:F3}, {ourRenderers.Length} parts)");
        }

        private static Material MakeMaterial(MeshGroup g)
        {
            var srgb = new Color(
                Mathf.Pow(g.Color.r, 1f / 2.2f),
                Mathf.Pow(g.Color.g, 1f / 2.2f),
                Mathf.Pow(g.Color.b, 1f / 2.2f), 1f);

            Material m = null;
            var urp = Shader.Find("Universal Render Pipeline/Lit");
            if (urp != null) m = new Material(urp);
            else
            {
                var std = Shader.Find("Standard");
                if (std != null) m = new Material(std);
            }
            if (m == null) return new Material(Shader.Find("Legacy Shaders/Diffuse"));

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", srgb);
            if (m.HasProperty("_Color")) m.SetColor("_Color", srgb);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", g.Metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - g.Roughness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 1f - g.Roughness);
            return m;
        }
    }

    public class BoatModMarker : MonoBehaviour { }
}
