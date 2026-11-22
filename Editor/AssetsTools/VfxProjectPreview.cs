using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class VfxProjectPreview
{
    const string PrefKey = "VfxProjectPreview.Enabled";
    const int MaxRenderPerPass = 48;   
    const int MaxCache = 80;          
    const double RepaintInterval = 1.0 / 30.0;
    const float MinLoop = 1.5f, MaxLoop = 8f;

    class Entry
    {
        public GameObject instance;
        public ParticleSystem[] roots;
        public float loop;
        public Vector3 center;
        public float radius;
        public int slot;
    }

    const float SlotSpacing = 1000f;

    static int FindFreeSlot()
    {
        var used = new HashSet<int>();
        foreach (var kv in cache) used.Add(kv.Value.slot);
        int s = 0;
        while (used.Contains(s)) s++;
        return s;
    }

    static bool enabled;
    static PreviewRenderUtility preview;
    static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();
    static readonly Dictionary<string, bool> isVfx = new Dictionary<string, bool>();
    static int renderedThisPass;
    static bool drewRecently;
    static double lastRepaint;

    static VfxProjectPreview()
    {
        enabled = EditorPrefs.GetBool(PrefKey, true);
        EditorApplication.projectWindowItemOnGUI += OnItemGUI;
        EditorApplication.update += OnUpdate;
        AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        EditorApplication.quitting += Cleanup;
    }

    [MenuItem("Meowzini/VFX Preview/Enabled")]
    static void Toggle()
    {
        enabled = !enabled;
        EditorPrefs.SetBool(PrefKey, enabled);
        if (!enabled) Cleanup();
        EditorApplication.RepaintProjectWindow();
    }

    [MenuItem("Meowzini/VFX Preview/Enabled", true)]
    static bool ToggleValidate()
    {
        Menu.SetChecked("Meowzini/VFX Preview/Enabled", enabled);
        return true;
    }

    [MenuItem("Meowzini/VFX Preview/Clear Cache")]
    static void ClearCache()
    {
        Cleanup();
        EditorApplication.RepaintProjectWindow();
    }

    static void OnUpdate()
    {
        renderedThisPass = 0;
        if (!enabled || !drewRecently) return;
        if (EditorApplication.timeSinceStartup - lastRepaint < RepaintInterval) return;
        lastRepaint = EditorApplication.timeSinceStartup;
        drewRecently = false;
        EditorApplication.RepaintProjectWindow();
    }

    static void OnItemGUI(string guid, Rect rect)
    {
        if (!enabled || Event.current.type != EventType.Repaint) return;
        if (rect.height <= 20f) return; 

        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab")) return;

        if (!isVfx.TryGetValue(guid, out bool vfx))
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            vfx = go != null && go.GetComponentInChildren<ParticleSystem>(true) != null;
            isVfx[guid] = vfx;
        }
        if (!vfx || renderedThisPass >= MaxRenderPerPass) return;

        if (!cache.TryGetValue(guid, out Entry entry))
        {
            entry = CreateEntry(path);
            if (entry == null) { isVfx[guid] = false; return; }
            if (cache.Count >= MaxCache) ResetCache();
            cache[guid] = entry;
        }
        if (entry.instance == null) { cache.Remove(guid); return; }

        float side = Mathf.Min(rect.width, rect.height - 16f);
        var icon = new Rect(rect.x + (rect.width - side) * 0.5f, rect.y, side, side);

        Texture tex = Render(entry, Mathf.Max(32, Mathf.RoundToInt(side)));
        if (tex != null)
        {
            GUI.DrawTexture(icon, tex, ScaleMode.StretchToFill, false);
            renderedThisPass++;
            drewRecently = true;
        }
    }

    static void EnsurePreview()
    {
        if (preview != null) return;
        preview = new PreviewRenderUtility();
        var cam = preview.camera;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);
        cam.fieldOfView = 30f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 400f; 
        cam.allowHDR = false;
    }

    static Entry CreateEntry(string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) return null;
        EnsurePreview();

        var inst = Object.Instantiate(prefab);
        inst.hideFlags = HideFlags.HideAndDontSave;
        int slot = FindFreeSlot();
        inst.transform.position = new Vector3(slot * SlotSpacing, 0f, 0f);
        inst.transform.rotation = Quaternion.identity;
        preview.AddSingleGO(inst);

        var all = inst.GetComponentsInChildren<ParticleSystem>(true);
        if (all.Length == 0) { Object.DestroyImmediate(inst); return null; }

        var roots = new List<ParticleSystem>();
        float loop = MinLoop;
        foreach (var ps in all)
        {
            var parent = ps.transform.parent;
            if (parent == null || parent.GetComponentInParent<ParticleSystem>() == null)
                roots.Add(ps);
            var m = ps.main;
            float len = m.duration + m.startDelay.constantMax + m.startLifetime.constantMax;
            loop = Mathf.Max(loop, len);
        }
        loop = Mathf.Clamp(loop, MinLoop, MaxLoop);

        var e = new Entry { instance = inst, roots = roots.ToArray(), loop = loop, slot = slot };

        Bounds b = new Bounds(inst.transform.position, Vector3.zero);
        bool has = false;
        for (int i = 1; i <= 4; i++)
        {
            SimulateAt(e, loop * i / 5f);
            foreach (var r in inst.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (r.bounds.size.sqrMagnitude < 1e-6f) continue;
                if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
            }
        }
        if (!has) b = new Bounds(inst.transform.position + Vector3.up, Vector3.one * 2f);
        e.center = b.center;
        e.radius = Mathf.Clamp(b.extents.magnitude, 0.5f, 30f);
        return e;
    }

    static void SimulateAt(Entry e, float t)
    {
        foreach (var ps in e.roots)
            if (ps != null) ps.Simulate(t, true, true, true);
    }

    static Texture Render(Entry e, int size)
    {
        EnsurePreview();
        float t = (float)(EditorApplication.timeSinceStartup % e.loop);
        SimulateAt(e, t);

        var cam = preview.camera;
        Vector3 dir = new Vector3(0f, 0.3f, -1f).normalized;
        cam.transform.position = e.center + dir * (e.radius * 3.2f);
        cam.transform.rotation = Quaternion.LookRotation(e.center - cam.transform.position);

        preview.BeginPreview(new Rect(0, 0, size, size), GUIStyle.none);
        cam.Render();
        return preview.EndPreview();
    }

    static void ResetCache()
    {
        foreach (var kv in cache)
            if (kv.Value.instance != null) Object.DestroyImmediate(kv.Value.instance);
        cache.Clear();
    }

    static void Cleanup()
    {
        ResetCache();
        isVfx.Clear();
        if (preview != null) { preview.Cleanup(); preview = null; }
    }

    class Watcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var p in imported)
            {
                if (!p.EndsWith(".prefab")) continue;
                string g = AssetDatabase.AssetPathToGUID(p);
                if (cache.TryGetValue(g, out var e))
                {
                    if (e.instance != null) Object.DestroyImmediate(e.instance);
                    cache.Remove(g);
                }
                isVfx.Remove(g);
            }
        }
    }
}