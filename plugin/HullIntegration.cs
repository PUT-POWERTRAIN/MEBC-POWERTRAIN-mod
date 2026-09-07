using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BoatMod
{
    internal static class HullIntegration
    {
        internal const string OurName = "POWERTRAIN POWERBoat";
        internal static UnityEngine.Object OurHull;
        internal static int OurIndex = -1;
        internal static int SelectedIndex = -1;
        internal static bool Done;

        internal static void EnsureIntegrated()
        {
            if (Done) return;
            try
            {
                var hullType = BoatModPlugin.FindType("HullPropertiesSO");
                if (hullType == null)
                {
                    BoatModPlugin.Log.LogInfo("[BoatMod] HullPropertiesSO not loaded yet, will retry on scene load");
                    return;
                }

                var existing = Resources.FindObjectsOfTypeAll(hullType);
                UnityEngine.Object template = null;
                foreach (var o in existing)
                {
                    if (o == null) continue;
                    if (o.name.IndexOf("Lana", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        o.name.IndexOf("BME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        o.name.IndexOf("Solar", StringComparison.OrdinalIgnoreCase) >= 0)
                    { template = o; break; }
                }
                template ??= existing.FirstOrDefault(o => o != null);
                if (template == null)
                {
                    BoatModPlugin.Log.LogInfo("[BoatMod] no hull instances found yet, will retry on scene load");
                    return;
                }
                BoatModPlugin.Log.LogInfo($"[BoatMod] cloning hull template '{template.name}'");

                var clone = UnityEngine.Object.Instantiate(template);
                clone.name = OurName;
                foreach (var f in clone.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType != typeof(string)) continue;
                    var ln = f.Name.ToLowerInvariant();
                    if (!ln.Contains("name") && !ln.Contains("design") && !ln.Contains("title") &&
                        !ln.Contains("desc") && !ln.Contains("info") && !ln.Contains("text")) continue;
                    if (ln.Contains("prefab") || ln.Contains("file") || ln.Contains("path") ||
                        ln.Contains("sprite") || ln.Contains("icon") || ln.Contains("key") ||
                        ln.Contains("guid") || ln.Contains("categor") || ln.Contains("asset")) continue;
                    try
                    {
                        f.SetValue(clone, OurName);
                        BoatModPlugin.Log.LogInfo($"[BoatMod] set hull field '{f.Name}' = '{OurName}'");
                    }
                    catch { }
                }

                int appendedAt = -1;
                string holderName = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("Assembly")) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (!IsHullListField(f, hullType)) continue;
                            if (f.IsStatic)
                            {
                                if (AppendToField(null, f, clone, hullType, out appendedAt))
                                {
                                    holderName = t.Name + "." + f.Name + " (static)";
                                    break;
                                }
                                continue;
                            }
                            bool isHolderKind = typeof(Component).IsAssignableFrom(t) || typeof(ScriptableObject).IsAssignableFrom(t);
                            if (!isHolderKind) continue;
                            UnityEngine.Object[] holders;
                            try { holders = Resources.FindObjectsOfTypeAll(t); } catch { continue; }
                            foreach (var h in holders)
                            {
                                if (h == null) continue;
                                if (AppendToField(h, f, clone, hullType, out appendedAt))
                                {
                                    holderName = t.Name + "." + f.Name;
                                    break;
                                }
                            }
                            if (appendedAt >= 0) break;
                        }
                        if (appendedAt >= 0) break;
                    }
                    if (appendedAt >= 0) break;
                }

                if (appendedAt < 0)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!asm.GetName().Name.Contains("Assembly")) continue;
                        Type[] types;
                        try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var t in types)
                        {
                            if (!typeof(Component).IsAssignableFrom(t) && !typeof(ScriptableObject).IsAssignableFrom(t)) continue;
                            var tn = t.Name.ToLowerInvariant();
                            if (!tn.Contains("builder") && !tn.Contains("shop") && !tn.Contains("garage") &&
                                !tn.Contains("selector") && !tn.Contains("manager") && !tn.Contains("boat") &&
                                !tn.Contains("config") && !tn.Contains("data")) continue;
                            UnityEngine.Object[] holders;
                            try { holders = Resources.FindObjectsOfTypeAll(t); } catch { continue; }
                            foreach (var h in holders)
                            {
                                if (h == null) continue;
                                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                {
                                    if (f.FieldType.IsPrimitive || f.FieldType == typeof(string)) continue;
                                    if (typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType)) continue;
                                    if (f.FieldType.IsArray || f.FieldType.IsGenericType) continue;
                                    if (!f.FieldType.IsClass) continue;
                                    object nested;
                                    try { nested = f.GetValue(h); } catch { continue; }
                                    if (nested == null) continue;
                                    foreach (var nf in nested.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                    {
                                        if (!IsHullListField(nf, hullType)) continue;
                                        if (AppendToField(nested, nf, clone, hullType, out appendedAt))
                                        {
                                            if (nested.GetType().IsValueType)
                                            {
                                                try { f.SetValue(h, nested); } catch { }
                                            }
                                            holderName = t.Name + "." + f.Name + "." + nf.Name;
                                            break;
                                        }
                                    }
                                    if (appendedAt >= 0) break;
                                }
                                if (appendedAt >= 0) break;
                            }
                            if (appendedAt >= 0) break;
                        }
                        if (appendedAt >= 0) break;
                    }
                }

                if (appendedAt < 0)
                {
                    BoatModPlugin.Log.LogWarning("[BoatMod] hull list holder not found, will retry on scene load");
                    return;
                }

                OurHull = clone;
                OurIndex = appendedAt;
                Done = true;
                BoatModPlugin.Log.LogInfo($"[BoatMod] integrated '{OurName}' via {holderName} at hull index {OurIndex}");
            }
            catch (Exception e)
            {
                BoatModPlugin.Log.LogError($"[BoatMod] hull integration failed: {e.Message}");
            }
        }

        private static bool IsHullListField(FieldInfo f, Type hullType)
        {
            try
            {
                if (f.FieldType.IsArray && f.FieldType.GetElementType() == hullType) return true;
                if (f.FieldType.IsGenericType &&
                    f.FieldType.GetGenericTypeDefinition() == typeof(List<>) &&
                    f.FieldType.GetGenericArguments()[0] == hullType) return true;
            }
            catch { }
            return false;
        }

        private static bool AppendToField(object holder, FieldInfo f, UnityEngine.Object clone, Type hullType, out int index)
        {
            index = -1;
            try
            {
                if (f.FieldType.IsArray)
                {
                    var arr = (Array)f.GetValue(holder);
                    if (arr == null) return false;
                    for (int i = 0; i < arr.Length; i++)
                        if (ReferenceEquals(arr.GetValue(i), clone)) { index = i; return true; }
                    var grown = Array.CreateInstance(hullType, arr.Length + 1);
                    Array.Copy(arr, grown, arr.Length);
                    grown.SetValue(clone, arr.Length);
                    f.SetValue(holder, grown);
                    index = arr.Length;
                    return true;
                }
                else
                {
                    var list = f.GetValue(holder) as IList;
                    if (list == null) return false;
                    foreach (var e in list)
                        if (ReferenceEquals(e, clone)) return false;
                    list.Add(clone);
                    index = list.Count - 1;
                    return true;
                }
            }
            catch { return false; }
        }

        internal static void DumpBuilder(UnityEngine.SceneManagement.Scene scene)
        {
            try
            {
                var log = BoatModPlugin.Log;
                log.LogInfo($"[BoatMod] === builder dump for scene '{scene.name}' ===");
                var hullType = BoatModPlugin.FindType("HullPropertiesSO");
                if (hullType != null)
                {
                    try
                    {
                        foreach (var o in Resources.FindObjectsOfTypeAll(hullType))
                        {
                            if (o == null) continue;
                            if (o.name.IndexOf("Lana", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var parts = new List<string> { $"hullprops: '{o.name}'" };
                            foreach (var f in hullType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                try
                                {
                                    var v = f.GetValue(o);
                                    if (v == null) parts.Add($"{f.Name}=null");
                                    else if (v is string || v.GetType().IsPrimitive || v.GetType().IsEnum) parts.Add($"{f.Name}={v}");
                                    else if (v is UnityEngine.Object uo) parts.Add($"{f.Name}->'{uo.name}'");
                                    else if (v is Array a) parts.Add($"{f.Name}=[{a.Length}]");
                                    else parts.Add($"{f.Name}=<{v.GetType().Name}>");
                                }
                                catch { }
                            }
                            log.LogInfo("[BoatMod] " + string.Join(" | ", parts));
                        }
                    }
                    catch { }
                }
                var itemType = BoatModPlugin.FindType("HullShopItem");
                if (itemType != null)
                {
                    try
                    {
                        foreach (var o in Resources.FindObjectsOfTypeAll(itemType))
                        {
                            if (o == null) continue;
                            var parts = new List<string> { $"item: '{o.name}'" };
                            foreach (var f in itemType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                try
                                {
                                    var v = f.GetValue(o);
                                    if (v == null) parts.Add($"{f.Name}=null");
                                    else if (v is string || v.GetType().IsPrimitive || v.GetType().IsEnum) parts.Add($"{f.Name}={v}");
                                    else if (v is UnityEngine.Object uo) parts.Add($"{f.Name}->'{uo.name}'");
                                    else parts.Add($"{f.Name}=<{v.GetType().Name}>");
                                }
                                catch { }
                            }
                            log.LogInfo("[BoatMod] " + string.Join(" | ", parts));
                        }
                    }
                    catch (Exception e) { log.LogWarning($"[BoatMod] item dump failed: {e.Message}"); }
                }
                foreach (var root in scene.GetRootGameObjects())
                    DumpBuilderNode(root.transform, 0, log);
                log.LogInfo("[BoatMod] === builder dump end ===");
            }
            catch (Exception e)
            {
                BoatModPlugin.Log.LogError($"[BoatMod] builder dump failed: {e.Message}");
            }
        }

        private static void DumpBuilderNode(Transform t, int depth, BepInEx.Logging.ManualLogSource log)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return;
            if (depth > 2) return;
            try
            {
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var tn = c.GetType().Name;
                    var tl = tn.ToLowerInvariant();
                    bool match = tl.Contains("builder") || tl.Contains("shop") || tl.Contains("selector") ||
                                     tl.Contains("scroll") || tl.Contains("hull") || tl.Contains("garage") ||
                                     tl.Contains("preview") || tl.Contains("pool");
                    if (!match) continue;
                    log.LogInfo($"[BoatMod] [{t.name}] {c.GetType().FullName}");
                    foreach (var f in c.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        string val;
                        try
                        {
                            var v = f.GetValue(c);
                            if (v == null) val = "null";
                            else if (v is Array a) val = $"{f.FieldType.Name}[{a.Length}]";
                            else if (v is IList l) val = $"{f.FieldType.Name}(count={l.Count})";
                            else if (v is string || v.GetType().IsPrimitive || v.GetType().IsEnum) val = v.ToString();
                            else val = $"<{v.GetType().Name}>";
                        }
                        catch { val = "<err>"; }
                        log.LogInfo($"[BoatMod]     {f.Name} : {f.FieldType.Name} = {val}");
                    }
                    foreach (var m in c.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        var ml = m.Name.ToLowerInvariant();
                        if (ml.Contains("select") || ml.Contains("next") || ml.Contains("prev") || ml.Contains("hull") || ml.Contains("build") || ml.Contains("create"))
                            log.LogInfo($"[BoatMod]     method: {m}");
                    }
                }
            }
            catch { }
            foreach (Transform child in t)
                DumpBuilderNode(child, depth + 1, log);
        }

        internal static UnityEngine.Object OurItem;
        internal static int OurItemIndex = -1;
        internal static bool ShopDone;

        internal static void EnsureShopItem()
        {
            if (ShopDone) return;
            if (OurHull == null) return;
            try
            {
                var itemType = BoatModPlugin.FindType("HullShopItem");
                var hullType = BoatModPlugin.FindType("HullPropertiesSO");
                if (itemType == null || hullType == null) return;

                UnityEngine.Object[] items;
                try { items = Resources.FindObjectsOfTypeAll(itemType); }
                catch { return; }
                if (items == null || items.Length == 0) return;

                UnityEngine.Object template = null;
                foreach (var o in items)
                {
                    if (o == null) continue;
                    if (o.name.IndexOf("Lana", StringComparison.OrdinalIgnoreCase) >= 0) { template = o; break; }
                }
                template ??= items.FirstOrDefault(o => o != null);
                if (template == null) return;
                BoatModPlugin.Log.LogInfo($"[BoatMod] cloning shop item '{template.name}'");

                var clone = UnityEngine.Object.Instantiate(template);
                clone.name = OurName;
                foreach (var f in itemType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (f.FieldType == hullType || f.FieldType.Name == "HullPropertiesSO")
                        {
                            f.SetValue(clone, OurHull);
                            BoatModPlugin.Log.LogInfo($"[BoatMod] pointed shop item field '{f.Name}' at our hull");
                        }
                        else if (f.FieldType == typeof(string))
                        {
                            var ln = f.Name.ToLowerInvariant();
                            if (!ln.Contains("name") && !ln.Contains("title") && !ln.Contains("label") &&
                                !ln.Contains("desc") && !ln.Contains("info") && !ln.Contains("text")) continue;
                            if (ln.Contains("prefab") || ln.Contains("file") || ln.Contains("path") ||
                                ln.Contains("sprite") || ln.Contains("icon") || ln.Contains("key") ||
                                ln.Contains("guid") || ln.Contains("categor") || ln.Contains("asset")) continue;
                            f.SetValue(clone, OurName);
                            BoatModPlugin.Log.LogInfo($"[BoatMod] set shop item field '{f.Name}' = '{OurName}'");
                        }
                    }
                    catch { }
                }

                SetStringField(clone, itemType, new[] { "UniqueId", "uniqueId", "ID", "Id" }, "hull_powertrain");
                SetStringField(clone, itemType, new[] { "UnlockGroup", "unlockGroup" }, "");
                SetNumericField(clone, itemType, new[] { "UnlockPrice", "unlockPrice", "Price", "price" }, 0);

                int appendedAt = -1;
                string holderName = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.Contains("Assembly")) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (!IsHullListField(f, itemType)) continue;
                            if (f.IsStatic)
                            {
                                if (AppendToField(null, f, clone, itemType, out appendedAt))
                                { holderName = t.Name + "." + f.Name + " (static)"; break; }
                                continue;
                            }
                            if (!typeof(Component).IsAssignableFrom(t) && !typeof(ScriptableObject).IsAssignableFrom(t)) continue;
                            UnityEngine.Object[] holders;
                            try { holders = Resources.FindObjectsOfTypeAll(t); } catch { continue; }
                            foreach (var h in holders)
                            {
                                if (h == null) continue;
                                if (AppendToField(h, f, clone, itemType, out appendedAt))
                                { holderName = t.Name + "." + f.Name; break; }
                            }
                            if (appendedAt >= 0) break;
                        }
                        if (appendedAt >= 0) break;
                    }
                    if (appendedAt >= 0) break;
                }

                if (appendedAt < 0)
                {
                    BoatModPlugin.Log.LogWarning("[BoatMod] shop item list holder not found, will retry");
                    return;
                }
                OurItem = clone;
                OurItemIndex = appendedAt;
                ShopDone = true;
                BoatModPlugin.Log.LogInfo($"[BoatMod] shop item integrated via {holderName} at index {appendedAt}");
            }
            catch (Exception e)
            {
                BoatModPlugin.Log.LogError($"[BoatMod] shop item integration failed: {e.Message}");
            }
        }

        internal static GameObject OurVisual;

        internal static void InstallVisual()
        {
            var log0 = BoatModPlugin.Log;
            bool mNull = ReferenceEquals(BoatModPlugin.Instance, null);
            bool uNull = (UnityEngine.Object)BoatModPlugin.Instance == null;
            log0?.LogInfo($"[BoatMod] InstallVisual: hull={OurHull != null} item={OurItem != null} vis={OurVisual != null} done={Done} shop={ShopDone} instManagedNull={mNull} instUnityNull={uNull} instId={(mNull ? -1 : BoatModPlugin.Instance.GetInstanceID())}");
            if (OurVisual != null) return;
            if (OurHull == null || OurItem == null) { log0?.LogInfo("[BoatMod] InstallVisual: prerequisites missing, will retry"); return; }
            var log = BoatModPlugin.Log;
            try
            {
                var hullType = BoatModPlugin.FindType("HullPropertiesSO") ?? OurHull.GetType();
                SetNumericField(OurHull, hullType, new[] { "Mass", "mass" }, 150);
                bool linked = false;
                GameObject origVisual = null;
                foreach (var f in hullType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.Name == "_shopItem" || f.Name == "shopItem" || f.Name == "ShopItem")
                    {
                        try { f.SetValue(OurHull, OurItem); linked = true; log.LogInfo($"[BoatMod] hull '{f.Name}' -> our item"); }
                        catch { }
                    }
                    else if (f.Name == "_infoCardDetails" || f.Name == "infoCardDetails")
                    {
                        try { RenameDetails(f.GetValue(OurHull), OurHull, f, log); }
                        catch { }
                    }
                    else if ((f.Name == "Visual" || f.Name == "visual" || f.Name == "_visual") &&
                             typeof(GameObject).IsAssignableFrom(f.FieldType))
                    {
                        try { origVisual = f.GetValue(OurHull) as GameObject; } catch { }
                    }
                }
                if (!linked) log.LogWarning("[BoatMod] hull _shopItem field not found");
                if (origVisual != null)
                    log.LogInfo($"[BoatMod] original hull visual: '{origVisual.name}' active={origVisual.activeSelf} pos={origVisual.transform.localPosition} rot={origVisual.transform.localRotation.eulerAngles}");

                var go = new GameObject("POWERTRAIN visual");
                BoatModPlugin.BuildParts(go);
                int parts = go.GetComponentsInChildren<MeshFilter>().Length;
                if (parts == 0)
                {
                    log.LogError("[BoatMod] built visual has no meshes, not repointing");
                    UnityEngine.Object.Destroy(go);
                    return;
                }

                Vector3 origPos = Vector3.zero;
                Quaternion origRot = Quaternion.identity;
                float fitScale = 1f;
                if (origVisual != null)
                {
                    origPos = origVisual.transform.localPosition;
                    origRot = origVisual.transform.localRotation;
                    try
                    {
                        var tmp = UnityEngine.Object.Instantiate(origVisual);
                        tmp.SetActive(true);
                        var ob = new Bounds();
                        bool of = false;
                        foreach (var r in tmp.GetComponentsInChildren<MeshRenderer>(true))
                        {
                            if (!of) { ob = r.bounds; of = true; }
                            else ob.Encapsulate(r.bounds);
                        }
                        UnityEngine.Object.Destroy(tmp);
                        var mb = new Bounds();
                        bool mf = false;
                        foreach (var f2 in go.GetComponentsInChildren<MeshFilter>())
                        {
                            if (!mf) { mb = f2.sharedMesh.bounds; mf = true; }
                            else mb.Encapsulate(f2.sharedMesh.bounds);
                        }
                        float origFoot = Mathf.Max(ob.size.x, ob.size.z);
                        float ourFoot = Mathf.Max(0.001f, Mathf.Max(mb.size.x, mb.size.z));
                        if (of && origFoot > 0.001f) fitScale = origFoot / ourFoot;
                        log.LogInfo($"[BoatMod] fit: orig {ob.size:F2} our {mb.size:F2} scale {fitScale:F3}");
                    }
                    catch (Exception e) { log.LogWarning($"[BoatMod] fit failed: {e.Message}"); }
                }

                var inst = BoatModPlugin.Instance;
                float cfgScale = inst?._scale.Value ?? 1f;
                float rotY = inst?._rotY.Value ?? 0f;
                Vector3 cfgOffset = inst == null
                    ? Vector3.zero
                    : new Vector3(inst._offsetX.Value, inst._offsetY.Value, inst._offsetZ.Value);
                go.transform.localScale = Vector3.one * (fitScale * cfgScale);
                go.transform.localPosition = origPos + cfgOffset;
                go.transform.localRotation = origRot * Quaternion.Euler(0f, rotY, 0f);

                go.SetActive(false);
                try { GameObject.DontDestroyOnLoad(go); } catch { }
                AssignVisual(OurItem, go, log, "item");
                AssignVisual(OurHull, go, log, "hull");
                OurVisual = go;
                log.LogInfo($"[BoatMod] installed visual ({parts} parts, scale {fitScale * cfgScale:F3}) on our clones");
            }
            catch (Exception e)
            {
                log.LogError($"[BoatMod] visual install failed: {e}");
            }
        }

        private static void AssignVisual(UnityEngine.Object target, GameObject visual, BepInEx.Logging.ManualLogSource log, string tag)
        {
            foreach (var f in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.Name != "Visual" && f.Name != "visual" && f.Name != "_visual") continue;
                if (!typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType)) continue;
                try { f.SetValue(target, visual); log.LogInfo($"[BoatMod] {tag} Visual ({f.FieldType.Name}) -> our visual"); }
                catch (Exception e) { log.LogWarning($"[BoatMod] {tag} Visual set failed: {e.Message}"); }
            }
        }

        private static void RenameDetails(object det, object holder, FieldInfo holderField, BepInEx.Logging.ManualLogSource log)
        {
            if (det == null) return;
            var parts = new List<string>();
            foreach (var f in det.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    if (f.FieldType != typeof(string)) continue;
                    var v = (string)f.GetValue(det);
                    parts.Add($"{f.Name}='{v}'");
                    if (!string.IsNullOrEmpty(v) &&
                        (v.IndexOf("Lana", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         v.IndexOf("BME", StringComparison.OrdinalIgnoreCase) >= 0))
                        f.SetValue(det, OurName);
                }
                catch { }
            }
            log.LogInfo("[BoatMod] infoCard: " + string.Join(" | ", parts));
            if (det.GetType().IsValueType)
            {
                try { holderField.SetValue(holder, det); } catch { }
            }
        }

        private static void SetStringField(UnityEngine.Object target, Type t, string[] names, string value)
        {
            foreach (var n in names)
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null || f.FieldType != typeof(string)) continue;
                try { f.SetValue(target, value); BoatModPlugin.Log.LogInfo($"[BoatMod] set '{target.name}' field '{f.Name}' = '{value}'"); return; }
                catch { }
            }
            BoatModPlugin.Log.LogWarning($"[BoatMod] string field not found on '{target.name}'");
        }

        private static void SetNumericField(UnityEngine.Object target, Type t, string[] names, float value)
        {
            foreach (var n in names)
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) continue;
                try
                {
                    if (f.FieldType == typeof(float)) f.SetValue(target, value);
                    else if (f.FieldType == typeof(int)) f.SetValue(target, (int)value);
                    else if (f.FieldType == typeof(double)) f.SetValue(target, (double)value);
                    else continue;
                    BoatModPlugin.Log.LogInfo($"[BoatMod] set '{target.name}' field '{f.Name}' = {value}");
                    return;
                }
                catch { }
            }
            BoatModPlugin.Log.LogWarning($"[BoatMod] numeric field not found on '{target.name}'");
        }

        internal static void PatchSelectHull(Harmony harmony)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    MethodInfo sel = null;
                    try
                    {
                        sel = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                            .FirstOrDefault(m => m.Name == "SelectHull" && !m.IsGenericMethodDefinition &&
                                m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                    }
                    catch { continue; }
                    if (sel == null) continue;
                    try
                    {
                        var hook = typeof(HullIntegration).GetMethod(nameof(SelectHullHook), BindingFlags.Static | BindingFlags.NonPublic);
                        harmony.Patch(sel, postfix: new HarmonyMethod(hook));
                        BoatModPlugin.Log.LogInfo($"[BoatMod] hooked {t.Name}.SelectHull");
                        return;
                    }
                    catch (Exception e)
                    {
                        BoatModPlugin.Log.LogWarning($"[BoatMod] SelectHull hook failed: {e.Message}");
                    }
                }
            }
            PatchShopRefresh(harmony);
        }

        internal static void PatchShopRefresh(Harmony harmony)
        {
            var targets = new HashSet<string>
                { "EnableOrInstantiateHull", "SelectBoat", "CreateNewBoat", "HidePreview", "RefreshPreview", "UpdatePreview", "ShowPreview" };
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.Contains("Assembly")) continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }
                    foreach (var m in methods)
                    {
                        if (!targets.Contains(m.Name) || m.IsGenericMethodDefinition) continue;
                        try
                        {
                            string hookName = m.Name == "EnableOrInstantiateHull" ? nameof(VisualInstantiatedHook) : nameof(ShopRefreshHook);
                            var hook = typeof(HullIntegration).GetMethod(hookName, BindingFlags.Static | BindingFlags.NonPublic);
                            harmony.Patch(m, postfix: new HarmonyMethod(hook));
                            BoatModPlugin.Log.LogInfo($"[BoatMod] hooked {t.Name}.{m.Name}");
                        }
                        catch { }
                    }
                }
            }
        }

        private static string _instDumpKey;

        private static void VisualInstantiatedHook(object __instance)
        {
            try
            {
                var log = BoatModPlugin.Log;
                var comp = __instance as Component;
                if (comp == null) return;
                var root = comp.transform.root;
                Transform ours = null;
                foreach (Transform child in root)
                {
                    if (child.name != "hull_powertrain") continue;
                    ours = child;
                    if (!child.gameObject.activeSelf)
                    {
                        child.gameObject.SetActive(true);
                        log?.LogInfo("[BoatMod] activated our hull clone after instantiate");
                    }
                }
                var scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                var key = scn + "|" + root.name;
                if (_instDumpKey == key) return;
                _instDumpKey = key;
                log?.LogInfo($"[BoatMod] instantiate: scene='{scn}' root='{root.name}' self='{comp.gameObject.name}'");
                foreach (Transform child in root)
                {
                    var rs = child.GetComponentsInChildren<Renderer>(true);
                    if (rs.Length == 0)
                    {
                        log?.LogInfo($"[BoatMod] instantiate: child '{child.name}' ({child.childCount} kids, no renderers)");
                        continue;
                    }
                    foreach (var r in rs)
                    {
                        var m = r.sharedMaterial;
                        string col = "";
                        try
                        {
                            if (m != null)
                            {
                                if (m.HasProperty("_BaseColor")) col = m.GetColor("_BaseColor").ToString();
                                else if (m.HasProperty("_Color")) col = m.GetColor("_Color").ToString();
                            }
                        }
                        catch { }
                        log?.LogInfo($"[BoatMod] instantiate: '{child.name}/{r.gameObject.name}' enabled={r.enabled} mat='{(m != null ? m.name : "null")}'{col}");
                    }
                }
                if (ours != null)
                    log?.LogInfo($"[BoatMod] our clone '{ours.name}' active={ours.gameObject.activeSelf} localPos={ours.localPosition}");
            }
            catch { }
        }

        private static readonly HashSet<string> _rendDumped = new HashSet<string>();

        internal static void DumpBoatRenderers()
        {
            try
            {
                var log = BoatModPlugin.Log;
                var scn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                var cfgType = BoatModPlugin.FindType("BoatConfiguration") ?? BoatModPlugin.FindType("Boat");
                if (cfgType == null) return;
                foreach (var o in Resources.FindObjectsOfTypeAll(cfgType))
                {
                    var comp = o as Component;
                    if (comp == null || !comp.gameObject.scene.IsValid()) continue;
                    var key = scn + "|" + comp.GetInstanceID();
                    if (!_rendDumped.Add(key)) continue;
                    var root = comp.transform.root.gameObject;
                    var mats = new List<string>();
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null) continue;
                        var m = r.sharedMaterial;
                        string col = "";
                        try
                        {
                            if (m != null)
                            {
                                if (m.HasProperty("_BaseColor")) col = m.GetColor("_BaseColor").ToString();
                                else if (m.HasProperty("_Color")) col = m.GetColor("_Color").ToString();
                            }
                        }
                        catch { }
                        mats.Add($"{r.gameObject.name}:{(m != null ? m.name : "null-mat")}{col}");
                    }
                    log.LogInfo($"[BoatMod] renderers on '{root.name}' ({mats.Count}): [{string.Join(" | ", mats)}]");
                }
            }
            catch { }
        }

        private static void ShopRefreshHook()
        {
            try
            {
                BoatModPlugin.Instance?.HandleBuilderPreview();
            }
            catch { }
        }

        private static void SelectHullHook(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] is int idx)
                {
                    SelectedIndex = idx;
                    BoatModPlugin.Log?.LogInfo($"[BoatMod] SelectHull({idx})");
                    BoatModPlugin.Instance?.HandleBuilderPreview();
                }
            }
            catch { }
        }

        internal static int ReadSelectedIndexFallback()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (!typeof(UnityEngine.Object).IsAssignableFrom(t)) continue;
                        var tn = t.Name.ToLowerInvariant();
                        if (!tn.Contains("builder") && !tn.Contains("shop") && !tn.Contains("selector")) continue;
                        UnityEngine.Object[] holders = null;
                        try { holders = Resources.FindObjectsOfTypeAll(t); } catch { continue; }
                        if (holders == null) continue;
                        foreach (var h in holders)
                        {
                            if (h == null) continue;
                            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                if (f.FieldType != typeof(int)) continue;
                                var ln = f.Name.ToLowerInvariant();
                                if (!ln.Contains("hull") || !ln.Contains("index")) continue;
                                try { return (int)f.GetValue(h); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }
            return -1;
        }

        internal static bool LastDecisionByRef;

        internal static bool IsOurHull(GameObject root)
        {
            LastDecisionByRef = false;
            if (OurHull == null || OurIndex < 0) return true;
            try
            {
                foreach (var comp in root.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;
                    var t = comp.GetType();
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        try
                        {
                            var ft = f.FieldType;
                            if (ft.Name == "HullShopItem")
                            {
                                var v = f.GetValue(comp) as UnityEngine.Object;
                                if (v == null) continue;
                                if (OurItem != null && ReferenceEquals(v, OurItem)) { LastDecisionByRef = true; return true; }
                                LastDecisionByRef = true; return false;
                            }
                            if (ft.Name == "HullPropertiesSO")
                            {
                                var v = f.GetValue(comp) as UnityEngine.Object;
                                            if (v != null) { LastDecisionByRef = true; return ReferenceEquals(v, OurHull); }
                            }
                            else if (ft == typeof(int))
                            {
                                var ln = f.Name.ToLowerInvariant();
                                if (ln.Contains("hull") && (ln.Contains("index") || ln.Contains("id")))
                                {
                                    int v = (int)f.GetValue(comp);
                                    if (OurItemIndex >= 0 && v == OurItemIndex) { LastDecisionByRef = true; return true; }
                                    if (v == OurIndex) { LastDecisionByRef = true; return true; }
                                    LastDecisionByRef = true; return false;
                                }
                            }
                            else if (ft.IsClass && ft != typeof(string) && !ft.IsArray &&
                                     (ft.Name.ToLowerInvariant().Contains("config") ||
                                      ft.Name.ToLowerInvariant().Contains("data") ||
                                      ft.Name.ToLowerInvariant().Contains("setup")))
                            {
                                var nested = f.GetValue(comp);
                                if (nested == null) continue;
                                foreach (var nf in ft.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                {
                                    try
                                    {
                                        if (nf.FieldType.Name == "HullShopItem")
                                        {
                                            var v = nf.GetValue(nested) as UnityEngine.Object;
                                            if (v == null) continue;
                                            if (OurItem != null && ReferenceEquals(v, OurItem)) { LastDecisionByRef = true; return true; }
                                            LastDecisionByRef = true; return false;
                                        }
                                        if (nf.FieldType.Name == "HullPropertiesSO")
                                        {
                                            var v = nf.GetValue(nested) as UnityEngine.Object;
                                if (v != null) { LastDecisionByRef = true; return ReferenceEquals(v, OurHull); }
                                        }
                                        else if (nf.FieldType == typeof(int))
                                        {
                                            var ln = nf.Name.ToLowerInvariant();
                                            if (ln.Contains("hull") && (ln.Contains("index") || ln.Contains("id")))
                                            {
                                                int v = (int)nf.GetValue(nested);
                                                if (OurItemIndex >= 0 && v == OurItemIndex) { LastDecisionByRef = true; return true; }
                                                if (v == OurIndex) { LastDecisionByRef = true; return true; }
                                                LastDecisionByRef = true; return false;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            if (Done && ShopDone) return false;
            return true;
        }
    }
}
