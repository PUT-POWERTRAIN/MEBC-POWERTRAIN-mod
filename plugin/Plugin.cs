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
        public const string PluginVersion = "1.5.0";

        internal static ManualLogSource Log;
        internal static BoatModPlugin Instance;
        internal static List<MeshGroup> StaticGroups;
        internal static float s_scale = 1f, s_offX, s_offY, s_offZ, s_rotY;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _hideOriginal;
        private ConfigEntry<bool> _postSwap;
        internal ConfigEntry<bool> Diag;
        internal ConfigEntry<bool> _autoFit;
        internal ConfigEntry<float> _scale;
        internal ConfigEntry<float> _offsetX, _offsetY, _offsetZ;
        internal ConfigEntry<float> _rotY;
        private ConfigEntry<string> _modelPath;

        private List<MeshGroup> _groups;
        private float _timer;
        private int _beats;
        private string _resolvedType;
        private Type _boatType;
        private bool _probeLogged;
        private bool _dumped;

        private void CacheModelConfig()
        {
            s_scale = _scale?.Value ?? 1f;
            s_offX = _offsetX?.Value ?? 0f;
            s_offY = _offsetY?.Value ?? 0f;
            s_offZ = _offsetZ?.Value ?? 0f;
            s_rotY = _rotY?.Value ?? 0f;
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            _enabled = Config.Bind("General", "Enabled", true, "Master switch");
            _hideOriginal = Config.Bind("General", "HideOriginal", true, "Hide the original boat meshes");
            _postSwap = Config.Bind("General", "PostSwapFallback", false, "Post-hoc mesh swap in races (native Visual is used instead)");
            Diag = Config.Bind("General", "Diagnostics", false, "Verbose builder dumps in the log");
            _autoFit = Config.Bind("Model", "AutoFit", true, "Auto-scale model to original boat footprint");
            _scale = Config.Bind("Model", "Scale", 1f, "Extra scale multiplier on top of AutoFit");
            _offsetX = Config.Bind("Model", "OffsetX", 0f, "Local position offset X (meters)");
            _offsetY = Config.Bind("Model", "OffsetY", 0f, "Local position offset Y (meters)");
            _offsetZ = Config.Bind("Model", "OffsetZ", 0f, "Local position offset Z (meters)");
            _rotY = Config.Bind("Model", "RotY", 0f, "Yaw rotation of the model in degrees");
            _modelPath = Config.Bind("Model", "Path",
                Path.Combine(Paths.PluginPath, "BoatMod", "boat.obj"),
                "OBJ file to load (expects matching .mtl next to it)");

            CacheModelConfig();
            Config.SettingChanged += (s, e) => CacheModelConfig();

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
            StaticGroups = _groups;
            Log.LogInfo($"[BoatMod] awake on instance id {GetInstanceID()}");

            try
            {
                _harmony = new Harmony(PluginGuid);
                PatchGameMethods();
                HullIntegration.PatchSelectHull(_harmony);
                HullIntegration.PatchSetFlagSafety(_harmony);
                HullIntegration.EnsureIntegrated();
                HullIntegration.EnsureShopItem();
                HullIntegration.InstallVisual();
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, __) =>
                {
                    try { Log.LogInfo($"[BoatMod] scene loaded: '{scene.name}', rescanning"); }
                    catch (Exception e) { Log.LogError($"[BoatMod] scene log failed: {e}"); return; }
                    try { HullIntegration.EnsureIntegrated(); }
                    catch (Exception e) { Log.LogError($"[BoatMod] integrate failed: {e}"); }
                    try { HullIntegration.EnsureShopItem(); }
                    catch (Exception e) { Log.LogError($"[BoatMod] shop item failed: {e}"); }
                    try { HullIntegration.InstallVisual(); }
                    catch (Exception e) { Log.LogError($"[BoatMod] visual install outer failed: {e}"); }
                    try { PatchGameMethods(); }
                    catch (Exception e) { Log.LogError($"[BoatMod] patch failed: {e}"); }
                    try { HullIntegration.PatchSetFlagSafety(_harmony); }
                    catch (Exception e) { Log.LogError($"[BoatMod] setflag patch failed: {e}"); }
                    try
                    {
                        float now = Time.unscaledTime;
                        _pendingDumps.Add(new PendingDump { Scene = scene.name, Due = now + DumpDelay1, At = DumpDelay1 });
                        _pendingDumps.Add(new PendingDump { Scene = scene.name, Due = now + DumpDelay2, At = DumpDelay2 });
                    }
                    catch (Exception e) { Log.LogError($"[BoatMod] dump schedule failed: {e}"); }
                    try
                    {
                        var sln = scene.name.ToLowerInvariant();
                        if (Instance.Diag.Value && (sln.Contains("builder") || sln.Contains("shop") || sln.Contains("garage") || sln.Contains("menu") || sln.Contains("lobby")))
                            HullIntegration.DumpBuilder(scene);
                    }
                    catch (Exception e) { Log.LogError($"[BoatMod] builder dump failed: {e}"); }
                    try { Scan(); }
                    catch (Exception e) { Log.LogError($"[BoatMod] scan failed: {e}"); }
                    try { HullIntegration.DumpBoatRenderers(); }
                    catch (Exception e) { Log.LogError($"[BoatMod] renderer dump failed: {e}"); }
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
            foreach (var typeName in new[] { "BoatController", "BoatInputActionsHandler", "BoatInputHandler", "PlayerSynchronizer", "GameManager",
                "ShopPreview", "ShopBoatBuilder", "ShopManager", "ShopMenuController", "ShopCameraController", "ShopInventory" })
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
                HullIntegration.DumpBoatRenderers();
            }
            catch { }
        }

        private static void GameTickHook(object __instance)
        {
            try
            {
                var self = Instance;
                if (self == null || self._groups == null || !self._enabled.Value) return;
                PumpDumps();
                if (++self._tickCounter % 60 == 0)
                {
                    self.Scan();
                    HullIntegration.RehideNativeProps();
                }
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
            if (!HullIntegration.IsOurHull(root))
            {
                if (_skipLogged.Add(root.GetInstanceID()))
                    Log.LogInfo($"[BoatMod] skipping '{root.name}', not our hull");
                return;
            }
            if (!_postSwap.Value)
            {
                if (_nativeLogged.Add(root.GetInstanceID()))
                    Log.LogInfo($"[BoatMod] native-visual mode, no post-swap on '{root.name}'");
                return;
            }
            if (!HullIntegration.LastDecisionByRef &&
                _compLogged.Add(root.GetInstanceID()))
            {
                var parts = new List<string>();
                foreach (var c in root.GetComponents<Component>())
                    if (c != null) parts.Add(c.GetType().Name);
                Log.LogInfo($"[BoatMod] race boat '{root.name}' comps: [{string.Join(", ", parts)}]");
            }
            try
            {
                ApplySwap(root, null);
            }
            catch (Exception e)
            {
                Log.LogError($"[BoatMod] swap failed on '{root.name}': {e}");
                root.AddComponent<BoatModMarker>();
            }
        }

        private readonly HashSet<int> _skipLogged = new HashSet<int>();
        private readonly HashSet<int> _nativeLogged = new HashSet<int>();
        private readonly HashSet<int> _compLogged = new HashSet<int>();
        private readonly HashSet<int> _dictLogged = new HashSet<int>();
        private int _lastPreviewSel = -999;
        private string _previewEmptyScene;
        private int _previewLogCounter;

        internal void HandleBuilderPreview()
        {
            if (_groups == null || !_enabled.Value) return;
            var pvType = FindType("ShopPreview") ?? FindType("BoatPreview");
            if (pvType == null) { Log.LogInfo("[BoatMod] preview: no preview type found"); return; }
            var previews = new List<Component>();
            foreach (var o in FindObjectsOfType(pvType))
                if (o is Component c && c != null) previews.Add(c);
            if (previews.Count == 0)
            {
                try
                {
                    foreach (var o in Resources.FindObjectsOfTypeAll(pvType))
                        if (o is Component c && c != null && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy)
                            previews.Add(c);
                }
                catch { }
            }
            if (previews.Count == 0)
            {
                var scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (_previewEmptyScene != scn)
                {
                    _previewEmptyScene = scn;
                    Log.LogInfo("[BoatMod] preview: no preview instances");
                }
                return;
            }
            int sel = HullIntegration.SelectedIndex;
            if (sel < 0) sel = HullIntegration.ReadSelectedIndexFallback();
            if (sel < 0) sel = ReadPreviewHullSelection();
            int ours = HullIntegration.OurItemIndex >= 0 ? HullIntegration.OurItemIndex : HullIntegration.OurIndex;
            _previewLogCounter++;
            if (sel != _lastPreviewSel || _previewLogCounter % 20 == 0)
            {
                _lastPreviewSel = sel;
                Log.LogInfo($"[BoatMod] preview: sel={sel} ours={ours} instances={previews.Count}");
            }
        }

        private int ReadPreviewHullSelection()
        {
            try
            {
                var previewType = FindType("ShopPreview");
                if (previewType != null)
                {
                    foreach (var obj in FindObjectsOfType(previewType))
                    {
                        var comp = obj as Component;
                        if (comp == null) continue;
                        object dict = null;
                        try
                        {
                            var prop = previewType.GetProperty("SelectedItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (prop != null) dict = prop.GetValue(comp);
                        }
                        catch { }
                        if (dict == null)
                        {
                            foreach (var f in previewType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                try
                                {
                                    if (typeof(System.Collections.IDictionary).IsAssignableFrom(f.FieldType))
                                    { dict = f.GetValue(comp); break; }
                                }
                                catch { }
                            }
                        }
                        if (dict is System.Collections.IDictionary d)
                        {
                            if (_dictLogged.Add(comp.GetInstanceID()))
                            {
                                var dp = new List<string>();
                                foreach (var k in d.Keys) dp.Add(k == null ? "null" : k.ToString());
                                Log.LogInfo($"[BoatMod] SelectedItems keys: [{string.Join(", ", dp)}] count={d.Count}");
                                foreach (var v in d.Values)
                                    Log.LogInfo($"[BoatMod] SelectedItems value: {(v == null ? "null" : v.GetType().Name + " '" + (v as UnityEngine.Object)?.name + "'")}");
                            }
                            foreach (var v in d.Values)
                            {
                                if (v == null || v.GetType().Name != "HullShopItem") continue;
                                if (HullIntegration.OurItem != null && ReferenceEquals(v, HullIntegration.OurItem))
                                    return HullIntegration.OurItemIndex >= 0 ? HullIntegration.OurItemIndex : HullIntegration.OurIndex;
                                return -2;
                            }
                        }
                    }
                }
                var builderType = FindType("ShopBoatBuilder");
                if (builderType == null) return -1;
                foreach (var obj in FindObjectsOfType(builderType))
                {
                    var comp = obj as Component;
                    if (comp == null) continue;
                    foreach (var f in builderType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object item = null;
                        try
                        {
                            if (f.FieldType.Name == "HullShopItem") item = f.GetValue(comp);
                            else continue;
                        }
                        catch { continue; }
                        if (item == null) continue;
                        foreach (var hf in item.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            try
                            {
                                if (hf.FieldType.Name != "HullPropertiesSO") continue;
                                var hull = hf.GetValue(item) as UnityEngine.Object;
                                if (hull == null) continue;
                                if (HullIntegration.OurHull != null && ReferenceEquals(hull, HullIntegration.OurHull))
                                    return HullIntegration.OurIndex;
                                return -2;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return -1;
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

            try
            {
                bool mineOnly = _resolvedType == "BoatController" || _resolvedType == "PlayerSynchronizer";
                foreach (var obj in FindObjectsOfType(_boatType))
                    TrySwap(obj as Component, mineOnly);
            }
            catch (Exception e) { Log.LogError($"[BoatMod] race scan failed: {e}"); }
            try { HandleBuilderPreview(); }
            catch (Exception e) { Log.LogError($"[BoatMod] preview handle failed: {e}"); }
        }

        private static readonly HashSet<string> _dumpedDeferred = new HashSet<string>();

        private struct PendingDump { public string Scene; public float Due; public float At; }

        private const float DumpDelay1 = 3f;
        private const float DumpDelay2 = 8f;

        private static readonly List<PendingDump> _pendingDumps = new List<PendingDump>();

        private static void PumpDumps()
        {
            if (_pendingDumps.Count == 0) return;
            try
            {
                float now = Time.unscaledTime;
                for (int i = _pendingDumps.Count - 1; i >= 0; i--)
                {
                    var p = _pendingDumps[i];
                    if (now < p.Due) continue;
                    _pendingDumps.RemoveAt(i);
                    DumpStrayRenderers(p.Scene, p.At);
                }
            }
            catch (Exception e) { Log?.LogError($"[BoatMod] dump pump failed: {e}"); }
        }

        private static void DumpStrayRenderers(string sceneName, float atSec)
        {
            try
            {
                if (!_dumpedDeferred.Add($"{sceneName}|{atSec}")) return;
                var scn = default(UnityEngine.SceneManagement.Scene);
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (s.name == sceneName) { scn = s; break; }
                }
                if (!scn.IsValid()) return;
                Log.LogInfo($"[BoatMod] deferred renderer dump @{atSec}s scene '{sceneName}'");
                foreach (var root in scn.GetRootGameObjects())
                {
                    var active = new List<Renderer>();
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                        if (r != null && r.enabled && r.gameObject.activeInHierarchy) active.Add(r);
                    if (active.Count == 0) continue;
                    var nl = root.name.ToLowerInvariant();
                    if (nl.Contains("boat") || nl.Contains("hull") || nl.Contains("player"))
                    {
                        var parts = new List<string>();
                        foreach (var r in active)
                            parts.Add($"{r.gameObject.name}@{r.transform.position:F1} size={r.bounds.size:F1} mat={(r.sharedMaterial != null ? r.sharedMaterial.name : "null")}");
                        Log.LogInfo($"[BoatMod] '{root.name}' active renderers ({active.Count}): {string.Join(" | ", parts)}");
                    }
                    else
                    {
                        Log.LogInfo($"[BoatMod] root '{root.name}' active renderers: {active.Count}");
                        foreach (var r in active)
                            Log.LogInfo($"[BoatMod]   stray '{root.name}/{r.gameObject.name}' @{r.transform.position:F1} size={r.bounds.size:F1} mat={(r.sharedMaterial != null ? r.sharedMaterial.name : "null")}");
                    }
                }
            }
            catch (Exception e) { Log.LogError($"[BoatMod] deferred dump failed: {e}"); }
        }

        internal static Type FindType(string simpleName)
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
            try
            {
                var pv = go.GetComponent("PhotonView") as Behaviour;
                if (pv == null)
                {
                    var pt = FindType("PhotonView");
                    if (pt == null) return true;
                    pv = go.GetComponentInChildren(pt) as Behaviour;
                }
                if (pv == null) return true;
                var t = pv.GetType();
                var prop = t.GetProperty("isMine") ?? t.GetProperty("IsMine");
                if (prop != null) return Equals(true, prop.GetValue(pv, null));
                var field = t.GetField("isMine") ?? t.GetField("IsMine");
                if (field != null) return Equals(true, field.GetValue(pv));
                return true;
            }
            catch (Exception e)
            {
                Log?.LogWarning($"[BoatMod] IsMine failed on '{go.name}': {e.Message}");
                return true;
            }
        }

        private void ApplySwap(GameObject root, List<Renderer> recordHidden)
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

            BuildParts(model);

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
                    recordHidden?.Add(r);
                }
            }

            root.AddComponent<BoatModMarker>();
            Log.LogInfo(
                $"[BoatMod] swapped visuals on '{root.name}' " +
                $"(orig bounds {origBounds.size}, scale {extraScale:F3}, {ourRenderers.Length} parts)");
        }

        internal static void BuildParts(GameObject target)
        {
            var groups = Instance?._groups;
            if (groups == null || groups.Count == 0) groups = StaticGroups;
            if (groups == null || groups.Count == 0) return;
            foreach (var g in groups)
            {
                var part = new GameObject(string.IsNullOrEmpty(g.MatName) ? "part" : g.MatName);
                part.transform.SetParent(target.transform, false);
                var mf = part.AddComponent<MeshFilter>();
                mf.sharedMesh = g.ToMesh();
                var mr = part.AddComponent<MeshRenderer>();
                mr.sharedMaterial = MakeMaterial(g);
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
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
