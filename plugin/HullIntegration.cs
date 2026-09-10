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
                        TryTransplantSticker(tmp, go, fitScale, log);
                        if (TryCopyColliders(tmp, go, fitScale, log) == 0)
                            AddFallbackCollider(go, log);
                        UnityEngine.Object.Destroy(tmp);
                    }
                    catch (Exception e) { log.LogWarning($"[BoatMod] fit failed: {e.Message}"); }
                }

                var inst = BoatModPlugin.Instance;
                float cfgScale = BoatModPlugin.s_scale;
                float rotY = BoatModPlugin.s_rotY;
                Vector3 cfgOffset = new Vector3(BoatModPlugin.s_offX, BoatModPlugin.s_offY, BoatModPlugin.s_offZ);
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

        private static void TryTransplantSticker(GameObject lanaVisual, GameObject target, float fitScale, BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                var stickerType = BoatModPlugin.FindType("Sticker");
                if (stickerType == null) { log.LogWarning("[BoatMod] Sticker type not found"); return; }
                Component src = null;
                foreach (var c in lanaVisual.GetComponentsInChildren(stickerType, true))
                {
                    src = c as Component;
                    if (src != null) break;
                }
                if (src == null) { log.LogWarning("[BoatMod] no Sticker found on original hull visual"); return; }
                var srcT = src.transform;
                var clone = UnityEngine.Object.Instantiate(srcT.gameObject, target.transform);
                float inv = 1f / Mathf.Max(0.001f, fitScale);
                clone.transform.localPosition = srcT.localPosition * inv;
                clone.transform.localRotation = srcT.localRotation;
                clone.transform.localScale = srcT.localScale * inv;
                clone.SetActive(true);
                log.LogInfo($"[BoatMod] transplanted Sticker '{clone.name}' local={clone.transform.localPosition} scale={clone.transform.localScale.x:F3}");
            }
            catch (Exception e) { log.LogWarning($"[BoatMod] sticker transplant failed: {e.Message}"); }
        }

        private static int TryCopyColliders(GameObject src, GameObject dst, float fitScale, BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                int n = 0;
                float inv = 1f / Mathf.Max(0.001f, fitScale);
                foreach (var col in src.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null) continue;
                    var srcT = col.transform;
                    Vector3 shift = srcT != src.transform ? srcT.localPosition * inv : Vector3.zero;
                    if (srcT != src.transform && Quaternion.Angle(srcT.localRotation, Quaternion.identity) > 1f)
                        log.LogWarning($"[BoatMod] collider '{srcT.name}' rotated, copying with identity rotation");
                    switch (col)
                    {
                        case BoxCollider b:
                            {
                                var nb = dst.AddComponent<BoxCollider>();
                                nb.center = b.center + shift;
                                nb.size = b.size;
                                nb.enabled = col.enabled;
                                n++;
                                break;
                            }
                        case SphereCollider s:
                            {
                                var ns = dst.AddComponent<SphereCollider>();
                                ns.center = s.center + shift;
                                ns.radius = s.radius;
                                ns.enabled = col.enabled;
                                n++;
                                break;
                            }
                        case CapsuleCollider c:
                            {
                                var nc = dst.AddComponent<CapsuleCollider>();
                                nc.center = c.center + shift;
                                nc.radius = c.radius;
                                nc.height = c.height;
                                nc.direction = c.direction;
                                nc.enabled = col.enabled;
                                n++;
                                break;
                            }
                        case MeshCollider m:
                            {
                                var nm = dst.AddComponent<MeshCollider>();
                                nm.sharedMesh = m.sharedMesh;
                                nm.convex = true;
                                nm.enabled = col.enabled;
                                n++;
                                break;
                            }
                        default:
                            log.LogWarning($"[BoatMod] unsupported collider '{col.GetType().Name}' on '{srcT.name}' skipped");
                            break;
                    }
                }
                log.LogInfo($"[BoatMod] copied {n} collider(s) from original hull visual onto our visual root");
                return n;
            }
            catch (Exception e)
            {
                log.LogWarning($"[BoatMod] collider copy failed: {e.Message}");
                return 0;
            }
        }

        private static void AddFallbackCollider(GameObject dst, BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                var verts = new List<Vector3>();
                var tris = new List<int>();
                var rtow = dst.transform.worldToLocalMatrix;
                foreach (var mf in dst.GetComponentsInChildren<MeshFilter>())
                {
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    var m = rtow * mf.transform.localToWorldMatrix;
                    int b = verts.Count;
                    foreach (var v in mesh.vertices) verts.Add(m.MultiplyPoint3x4(v));
                    foreach (var t in mesh.triangles) tris.Add(b + t);
                }
                if (verts.Count == 0) { log.LogWarning("[BoatMod] fallback collider: no meshes found"); return; }
                var combined = new Mesh { vertices = verts.ToArray(), triangles = tris.ToArray() };
                combined.RecalculateBounds();
                var mc = dst.AddComponent<MeshCollider>();
                mc.sharedMesh = combined;
                mc.convex = true;
                log.LogInfo($"[BoatMod] fallback: convex mesh collider on visual root ({verts.Count} verts, {tris.Count / 3} tris)");
            }
            catch (Exception e) { log.LogWarning($"[BoatMod] fallback collider failed: {e.Message}"); }
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

        internal static void PatchSetFlagSafety(Harmony harmony)
        {
            if (_setFlagPatched) return;
            try
            {
                var t = BoatModPlugin.FindType("ShopPreview");
                var m = t?.GetMethod("SetFlag", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) { BoatModPlugin.Log.LogWarning("[BoatMod] ShopPreview.SetFlag not found for safety patch"); return; }
                harmony.Patch(m, finalizer: new HarmonyMethod(typeof(HullIntegration).GetMethod(nameof(SetFlagFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
                _setFlagPatched = true;
                BoatModPlugin.Log.LogInfo("[BoatMod] hooked ShopPreview.SetFlag (safety finalizer)");
            }
            catch (Exception e)
            {
                BoatModPlugin.Log.LogWarning($"[BoatMod] SetFlag safety patch failed: {e.Message}");
            }
        }

        private static bool _setFlagPatched;

        private static Exception SetFlagFinalizer(Exception __exception)
        {
            if (__exception == null) return null;
            BoatModPlugin.Log?.LogWarning($"[BoatMod] SetFlag failed, swallowed: {__exception.GetType().Name}: {__exception.Message}");
            return null;
        }

        internal static void PatchShopRefresh(Harmony harmony)
        {
            var targets = new HashSet<string>
                { "EnableOrInstantiateHull", "SelectBoat", "CreateNewBoat", "HidePreview", "RefreshPreview", "UpdatePreview", "ShowPreview", "ShowItem" };
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
                            string hookName =
                                m.Name == "EnableOrInstantiateHull" ? nameof(VisualInstantiatedHook) :
                                t.Name == "PropellerShopItem" && m.Name == "ShowPreview" ? nameof(PropellerShowHook) :
                                m.Name == "ShowItem" ? nameof(ShowItemHook) :
                                nameof(ShopRefreshHook);
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
                    if (child.name == "hull_powertrain") ours = child;
                if (ours != null) ApplyMountOffset(ours);
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
                {
                    log?.LogInfo($"[BoatMod] our clone '{ours.name}' active={ours.gameObject.activeSelf} localPos={ours.localPosition}");
                    var cs = ours.GetComponentsInChildren<Collider>(true);
                    var cdesc = new List<string>();
                    foreach (var c in cs)
                        cdesc.Add($"{c.GetType().Name}@{c.gameObject.name}{(c.enabled ? "" : "(disabled)")}");
                    log?.LogInfo($"[BoatMod] colliders on our clone ({cs.Length}): [{string.Join(", ", cdesc)}]");
                }
            }
            catch { }
        }

        private static readonly HashSet<string> _rendDumped = new HashSet<string>();

        private static readonly Dictionary<int, int> _propLast = new Dictionary<int, int>();
        private static readonly HashSet<int> _pointLogged = new HashSet<int>();
        private static readonly Dictionary<int, Vector3> _cloneBase = new Dictionary<int, Vector3>();

        private static void ApplyMountOffset(Transform ours)
        {
            try
            {
                int id = ours.gameObject.GetInstanceID();
                if (!_cloneBase.TryGetValue(id, out var basePos))
                {
                    basePos = ours.localPosition;
                    _cloneBase[id] = basePos;
                    BoatModPlugin.Log?.LogInfo($"[BoatMod] mount base '{ours.name}': {basePos}");
                }
                var off = new Vector3(BoatModPlugin.s_offX, BoatModPlugin.s_offY, BoatModPlugin.s_offZ);
                ours.localPosition = basePos + off;
            }
            catch { }
        }

        private static void PropellerShowHook(object __instance, object[] __args)
        {
            try
            {
                var log = BoatModPlugin.Log;
                Component preview = null;
                if (__args != null)
                    foreach (var a in __args)
                        if (a is Component c && c.GetType().Name == "ShopPreview") { preview = c; break; }
                if (preview == null)
                {
                    var it = __instance as Component;
                    var pt = it != null ? BoatModPlugin.FindType("ShopPreview") : null;
                    if (it != null && pt != null)
                    {
                        var comps = it.GetComponentsInChildren(pt, true);
                        if (comps.Length > 0) preview = comps[0] as Component;
                    }
                }
                if (preview == null) return;
                var root = preview.transform.root;
                Transform ours = null;
                foreach (Transform child in root)
                    if (child.name == "hull_powertrain" && child.gameObject.activeSelf) ours = child;
                if (ours == null) return;

                int hidden = 0;
                var pp = FindChildTransform(root, "Propeller point");
                if (pp != null)
                    foreach (Transform ch in pp)
                        if (ch.gameObject.activeSelf) { ch.gameObject.SetActive(false); hidden++; }

                int key = root.GetInstanceID();
                _propLast.TryGetValue(key, out var last);
                if (hidden != last)
                {
                    log?.LogInfo($"[BoatMod] propeller hidden on '{root.name}' ({hidden} deactivated, point={(pp != null ? "found" : "missing")})");
                    _propLast[key] = hidden;
                }

                if (_pointLogged.Add(key))
                {
                    foreach (var pn in new[] { "Motor point", "Foil point" })
                    {
                        var t2 = FindChildTransform(root, pn);
                        if (t2 == null) continue;
                        var names = new List<string>();
                        foreach (Transform ch in t2)
                            names.Add($"{ch.name}(active={ch.gameObject.activeSelf})");
                        log?.LogInfo($"[BoatMod] '{pn}' children: [{string.Join(", ", names)}]");
                    }
                }
            }
            catch (Exception e) { BoatModPlugin.Log?.LogWarning($"[BoatMod] propeller hide failed: {e.Message}"); }
        }

        private static Transform FindChildTransform(Transform t, string name)
        {
            if (t == null) return null;
            if (t.name == name) return t;
            foreach (Transform child in t)
            {
                var r = FindChildTransform(child, name);
                if (r != null) return r;
            }
            return null;
        }

        private static readonly HashSet<int> _rootDumped = new HashSet<int>();
        private static readonly HashSet<int> _hiddenRenders = new HashSet<int>();
        private static readonly string[] _attachKw = { "prop", "motor", "rudder", "shaft", "skeg" };

        private static void ShowItemHook(object __instance, object[] __args)
        {
            try
            {
                var log = BoatModPlugin.Log;
                Component preview = null;
                if (__instance is Component ic && ic.GetType().Name == "ShopPreview") preview = ic;
                if (preview == null && __args != null)
                    foreach (var a in __args)
                        if (a is Component c && c.GetType().Name == "ShopPreview") { preview = c; break; }
                if (preview == null)
                {
                    var pt = BoatModPlugin.FindType("ShopPreview");
                    if (__instance is Component it && pt != null)
                    {
                        var comps = it.GetComponentsInChildren(pt, true);
                        if (comps.Length > 0) preview = comps[0] as Component;
                    }
                }
                if (preview == null) return;
                var root = preview.transform.root;
                Transform ours = null;
                foreach (Transform child in root)
                    if (child.name == "hull_powertrain" && child.gameObject.activeSelf) ours = child;
                if (ours == null) return;
                DumpRootRenderers(root, log);
                HideNativeAttachments(root);
            }
            catch (Exception e) { BoatModPlugin.Log?.LogWarning($"[BoatMod] showitem hook failed: {e.Message}"); }
        }

        private static void DumpRootRenderers(Transform root, BepInEx.Logging.ManualLogSource log)
        {
            if (!_rootDumped.Add(root.GetInstanceID())) return;
            var list = new List<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                var m = r.sharedMaterial;
                list.Add($"{PathOf(r.transform)} ({(m != null ? m.name : "null")})");
            }
            log.LogInfo($"[BoatMod] '{root.name}' active renderers ({list.Count}): {string.Join(" | ", list)}");
        }

        internal static void RehideNativeProps()
        {
            try
            {
                int scn = UnityEngine.SceneManagement.SceneManager.sceneCount;
                for (int s = 0; s < scn; s++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        bool ours = false;
                        foreach (Transform ch in root.transform)
                            if (ch.name == "hull_powertrain" && ch.gameObject.activeSelf) { ours = true; ApplyMountOffset(ch); break; }
                        if (ours) HideNativeAttachments(root.transform);
                        else RestoreHiddenUnder(root.transform);
                    }
                }
            }
            catch { }
        }

        private static void RestoreHiddenUnder(Transform root)
        {
            if (_hiddenRenders.Count == 0) return;
            int restored = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.gameObject == null) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                if (!_hiddenRenders.Contains(r.GetInstanceID())) continue;
                r.enabled = true;
                _hiddenRenders.Remove(r.GetInstanceID());
                restored++;
            }
            if (restored > 0)
                BoatModPlugin.Log?.LogInfo($"[BoatMod] restored {restored} native attachment renderer(s) on '{root.name}'");
        }

        private static void HideNativeAttachments(Transform root)
        {
            int newly = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || r.gameObject == null) continue;
                var n = r.gameObject.name.ToLowerInvariant();
                bool hit = false;
                foreach (var k in _attachKw) if (n.IndexOf(k, StringComparison.Ordinal) >= 0) { hit = true; break; }
                if (!hit) continue;
                r.enabled = false;
                if (_hiddenRenders.Add(r.GetInstanceID())) newly++;
            }
            if (newly > 0)
                BoatModPlugin.Log?.LogInfo($"[BoatMod] hidden {newly} native attachment renderer(s) on '{root.name}'");
        }

        private static string PathOf(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }

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
