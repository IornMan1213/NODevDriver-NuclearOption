using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NODevDriver
{
    /// <summary>Dev-only (never shipped): drives the game's UI from BepInEx/mig29pack_cmd.txt (its own file: other dev drivers may be installed beside it and read nodev_cmd.txt), one command per line, for unattended tests.
    /// Commands: buttons | click &lt;label&gt; | clickat &lt;x&gt; &lt;y&gt; (screen px, top-left origin) | wait &lt;s&gt; | shot &lt;file&gt; | packcheck | say | quit
    /// and the measuring commands below. run.sh feeds it a scenario and collects the "[TEST]" lines.</summary>
    // While a test flies the player's jet, it holds the stick and throttle: the game's player pilot (PilotPlayerState) writes the
    // real stick or mouse into the control inputs every physics step, and its own throttle (simulatedThrottle) every frame.
    [HarmonyLib.HarmonyPatch]
    static class StickHold
    {
        internal static Vector3? Held;   // the game's pitch, roll, yaw inputs (-1..1; pitch + is nose DOWN), or null to give the stick back
        internal static float? Throttle;   // 0..1; the player's throttle is left there when the hold ends
        internal static Vector2? Level;    // hold this pitch and roll (deg, + nose up, + right wing down) with the stick; null: off
        internal static float RollSign = 1f, RollGain = 1f, RollBias;   // the game's roll input direction, the roll loop's gain (0: off), a fixed extra roll input
        internal static Vector2 LastLevelInput;   // the pitch and roll inputs the hold last gave

        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "PlayerControls", "PlayerAxisControls" })
            {
                var m = HarmonyLib.AccessTools.Method(typeof(PilotPlayerState), name);
                if (m != null) yield return m;
            }
        }

        static readonly System.Reflection.FieldInfo inputsField = HarmonyLib.AccessTools.Field(typeof(PilotBaseState), "controlInputs");
        static readonly System.Reflection.FieldInfo throttleField = HarmonyLib.AccessTools.Field(typeof(PilotPlayerState), "simulatedThrottle");

        static void Postfix(PilotPlayerState __instance)
        {
            var inp = inputsField?.GetValue(__instance) as ControlInputs;
            if (Held is Vector3 s)
            {
                __instance.pitchInput = s.x; __instance.rollInput = s.y; __instance.yawInput = s.z;
                if (inp != null) { inp.pitch = s.x; inp.roll = s.y; inp.yaw = s.z; }
            }
            if (Level is Vector2 lv)
            {
                var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                if (ac != null && ac.rb != null)
                {
                    // pitch: as the takeoff's climb-out (proportional on the error, damped by the rate; the game's pitch input is + nose down)
                    var f = ac.transform.forward; float pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
                    float prate = -Vector3.Dot(ac.rb.angularVelocity, ac.transform.right) * Mathf.Rad2Deg;
                    float pin = -Mathf.Clamp((lv.x - pitch) * 0.12f - prate * 0.04f, -0.6f, 0.6f);
                    // roll: + right wing down, from the right wing's height; the rate about the forward axis, + right wing down
                    float roll = Mathf.Asin(Mathf.Clamp(-ac.transform.right.y, -1f, 1f)) * Mathf.Rad2Deg;
                    if (ac.transform.up.y < 0f) roll = (roll >= 0f ? 180f : -180f) - roll;
                    float rrate = -Vector3.Dot(ac.rb.angularVelocity, ac.transform.forward) * Mathf.Rad2Deg;
                    float rin = RollBias + RollSign * RollGain * Mathf.Clamp((lv.y - roll) * 0.05f - rrate * 0.02f, -0.8f, 0.8f);
                    rin = Mathf.Clamp(rin, -1f, 1f);
                    LastLevelInput = new Vector2(pin, rin);
                    __instance.pitchInput = pin; __instance.rollInput = rin; __instance.yawInput = 0f;
                    if (inp != null) { inp.pitch = pin; inp.roll = rin; inp.yaw = 0f; }
                }
            }
            if (Throttle is float t) { throttleField?.SetValue(__instance, t); if (inp != null) inp.throttle = t; }
        }
    }

    [BepInPlugin("iornman.nodevdriver", "NO Dev Driver", "0.1.0")]
    public class Driver : BaseUnityPlugin
    {
        string skinPrefix = "MiG29_skin";   // the renderers the skin texture commands act on (skinprefix)
        string CmdFile => Path.Combine(Paths.BepInExRootPath, "mig29pack_cmd.txt");
        readonly Queue<string> queue = new Queue<string>();
        float next;

        void Awake()
        {
            new HarmonyLib.Harmony("nodev.driver").PatchAll(typeof(StickHold));
            var go = new GameObject("NODevDriver"); DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>().owner = this;
        }

        class Runner : MonoBehaviour
        {
            internal Driver owner;
            void Update() => owner.Tick();
        }

        void Tick()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            if (queue.Count == 0 && File.Exists(CmdFile))
            {
                foreach (var l in File.ReadAllLines(CmdFile)) if (l.Trim().Length > 0) queue.Enqueue(l.Trim());
                File.Delete(CmdFile);
            }
            if (queue.Count == 0) return;
            var cmd = queue.Dequeue();
            try { Run(cmd); } catch (System.Exception e) { Logger.LogError($"{cmd}: {e}"); }
        }

        static string Label(Component b)
        {
            var tmp = b.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text.Trim();
            var t = b.GetComponentInChildren<Text>(true);
            return t != null ? t.text.Trim() : b.name;
        }

        System.Collections.IEnumerator Fps(float secs)
        {
            int frames = 0; float t0 = Time.unscaledTime, worst = 0f;
            while (Time.unscaledTime - t0 < secs) { yield return null; frames++; worst = Mathf.Max(worst, Time.unscaledDeltaTime); }
            float dt = (Time.unscaledTime - t0) / frames;
            Logger.LogInfo($"  fps {1f / dt:F1} ({dt * 1000f:F2} ms/frame, worst {worst * 1000f:F1} ms) over {frames} frames");
        }

        System.Collections.IEnumerator GearLog(float secs)
        {
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var gears = FindObjectsOfType<MonoBehaviour>().Where(m => m.GetType().Name == "LandingGear").ToList();
            var peak = gears.ToDictionary(g => g, g => 0f); var peakHinge = gears.ToDictionary(g => g, g => 0f);
            var was = gears.ToDictionary(g => g, g => g.enabled);
            float t0 = Time.time;
            while (Time.time - t0 < secs)
            {
                yield return new WaitForFixedUpdate();
                foreach (var g in gears)
                {
                    if (g == null) continue;
                    var ty = g.GetType();
                    float c = (float)ty.GetField("compressionDistance", bf).GetValue(g);
                    var hinge = ty.GetField("gearHinge", bf).GetValue(g) as Component;
                    float hx = hinge != null ? hinge.transform.localEulerAngles.x : 0f;
                    if (c > peak[g]) peak[g] = c;
                    if (Mathf.Abs(Mathf.DeltaAngle(0f, hx)) > Mathf.Abs(Mathf.DeltaAngle(0f, peakHinge[g]))) peakHinge[g] = hx;
                    if (was[g] && !g.enabled) { Logger.LogInfo($"  BROKE {g.name} at t={Time.time - t0:F2}: comp {c:F3} hingeX {hx:F2}"); was[g] = false; }
                }
            }
            foreach (var g in gears) if (g != null) Logger.LogInfo($"  {g.name}: peak comp {peak[g]:F3}, peak hingeX {peakHinge[g]:F2}, enabled {g.enabled}");
        }

        void Run(string cmd)
        {
            var parts = cmd.Split(new[] { ' ' }, 2);
            string verb = parts[0].ToLowerInvariant(), arg = parts.Length > 1 ? parts[1] : "";
            Logger.LogInfo($"> {cmd}");
            switch (verb)
            {
                case "buttons":
                    foreach (var b in FindObjectsOfType<Button>().Where(b => b.isActiveAndEnabled && b.interactable))
                    {
                        var p = RectTransformUtility.WorldToScreenPoint(null, b.transform.position);
                        Logger.LogInfo($"  button '{Label(b)}' ({b.name}) at {p.x:F0},{Screen.height - p.y:F0}");
                    }
                    break;
                case "click":
                    var hit = FindObjectsOfType<Button>().Where(b => b.isActiveAndEnabled && b.interactable)
                        .FirstOrDefault(b => Label(b).ToLowerInvariant().Contains(arg.ToLowerInvariant()) || b.name.ToLowerInvariant().Contains(arg.ToLowerInvariant()));
                    if (hit == null) { Logger.LogWarning($"  no button '{arg}'"); break; }
                    Logger.LogInfo($"  clicking '{Label(hit)}' ({hit.name})");
                    hit.onClick.Invoke();
                    break;
                case "clickat":
                    var xy = arg.Split(' ');
                    var ped = new PointerEventData(EventSystem.current) { position = new Vector2(float.Parse(xy[0]), Screen.height - float.Parse(xy[1])), button = PointerEventData.InputButton.Left };
                    var results = new List<RaycastResult>();
                    EventSystem.current.RaycastAll(ped, results);
                    if (results.Count == 0) { Logger.LogWarning("  nothing there"); break; }
                    var go = results[0].gameObject;
                    ped.pointerPressRaycast = results[0]; ped.pointerCurrentRaycast = results[0];
                    var down = ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                    var clicked = ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                    Logger.LogInfo($"  hit {go.name} (down {(down != null ? down.name : "-")}, click {(clicked != null ? clicked.name : "-")})");
                    break;
                case "find":   // active UI objects whose name contains arg, with screen positions and any label
                    foreach (var rt in FindObjectsOfType<RectTransform>().Where(r => r.gameObject.activeInHierarchy && r.name.ToLowerInvariant().Contains(arg.ToLowerInvariant())))
                    {
                        var p = RectTransformUtility.WorldToScreenPoint(null, rt.position);
                        var tmp = rt.GetComponentInChildren<TMPro.TMP_Text>();
                        Logger.LogInfo($"  {rt.name} at {p.x:F0},{Screen.height - p.y:F0} '{(tmp != null ? tmp.text : "")}'");
                    }
                    break;
                case "inspect":   // fields of every component on active objects named arg
                    foreach (var go2 in FindObjectsOfType<Transform>().Where(t => t.gameObject.activeInHierarchy && t.name.ToLowerInvariant().Contains(arg.ToLowerInvariant())).Select(t => t.gameObject))
                        foreach (var comp in go2.GetComponents<Component>())
                        {
                            var ty = comp.GetType();
                            if (ty.Namespace != null && ty.Namespace.StartsWith("UnityEngine")) continue;
                            var vals = ty.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                                .Select(fi => { object v = null; try { v = fi.GetValue(comp); } catch { } return $"{fi.Name}={(v is Object uo ? (uo != null ? uo.name : "null") : v)}"; });
                            Logger.LogInfo($"  {go2.name}/{ty.Name}: {string.Join(", ", vals)}");
                        }
                    break;
                case "base":   // click the map icon of the airbase whose name contains arg
                    foreach (var icon in FindObjectsOfType<MonoBehaviour>().Where(m => m.GetType().Name == "AirbaseMapIcon"))
                    {
                        var fi = icon.GetType().GetField("<airbase>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        var ab = fi?.GetValue(icon) as Object;
                        Logger.LogInfo($"  icon {(ab != null ? ab.name : "?")}");
                        if (ab == null || !ab.name.ToLowerInvariant().Contains(arg.ToLowerInvariant())) continue;
                        var e = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
                        var r = ExecuteEvents.ExecuteHierarchy(icon.gameObject, e, ExecuteEvents.pointerClickHandler);
                        Logger.LogInfo($"  clicked {ab.name} ({(r != null ? r.name : "no handler")})");
                        break;
                    }
                    break;
                case "configmgr":   // open (true) or close BepInEx Configuration Manager's window
                    foreach (var pl in BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Where(i => i.Metadata.GUID == "com.bepis.bepinex.configurationmanager").Select(i => (object)i.Instance))
                    {
                        var prop = pl.GetType().GetProperty("DisplayingWindow");
                        prop?.SetValue(pl, arg.Trim() != "false", null);
                        Logger.LogInfo($"  ConfigurationManager.DisplayingWindow = {prop?.GetValue(pl, null)}");
                    }
                    break;
                case "cmsearch":   // type into Configuration Manager's search box
                    foreach (var pl in BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Where(i => i.Metadata.GUID == "com.bepis.bepinex.configurationmanager").Select(i => (object)i.Instance))
                    {
                        var prop = pl.GetType().GetProperty("SearchString", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (prop != null) prop.SetValue(pl, arg, null);
                        Logger.LogInfo($"  search = {(prop != null ? prop.GetValue(pl, null) : "no SearchString property")}");
                    }
                    break;
                case "mouse":   // where Unity thinks the cursor is
                    Logger.LogInfo($"  mouse {Input.mousePosition}");
                    break;
                case "renderers":   // renderers whose name or parent's name contains arg: state and bounds
                    foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>().Where(r => r.gameObject.scene.IsValid() &&
                             (r.name.ToLowerInvariant().Contains(arg.ToLowerInvariant()) || (r.transform.parent != null && r.transform.parent.name.ToLowerInvariant().Contains(arg.ToLowerInvariant())))))
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        Logger.LogInfo($"  {r.transform.parent?.parent?.name}/{r.transform.parent?.name}/{r.name} active={r.gameObject.activeInHierarchy} enabled={r.enabled} mesh={(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "-")} c={r.bounds.center:F2} s={r.bounds.size:F2} layer={r.gameObject.layer}");
                    }
                    break;
                case "view":   // view <x y z> <lx ly lz> <fov> <file>: camera at a point in the player's aircraft frame, looking at another (negative fov: orthographic half-height)
                {
                    var a = arg.Split(' ');
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                    var fr = ac.transform;
                    var from = fr.TransformPoint(new Vector3(F(0), F(1), F(2))); var to = fr.TransformPoint(new Vector3(F(3), F(4), F(5)));
                    var go3 = new GameObject("devcam"); var cam = go3.AddComponent<Camera>();
                    cam.transform.position = from; cam.transform.LookAt(to, fr.up);
                    cam.fieldOfView = Mathf.Abs(F(6)); cam.nearClipPlane = 0.05f; cam.farClipPlane = 500f;
                    if (F(6) < 0f) { cam.orthographic = true; cam.orthographicSize = -F(6); cam.farClipPlane = 1.5f * Vector3.Distance(from, to); }
                    var main = Camera.main; if (main != null) cam.cullingMask = main.cullingMask | (1 << 3) | (1 << 14);   // + the cockpit interior layers
                    var rt = new RenderTexture(1600, 1000, 24); cam.targetTexture = rt; cam.Render();
                    RenderTexture.active = rt; var tex = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0); tex.Apply(); RenderTexture.active = null;
                    File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, a[7]), tex.EncodeToPNG());
                    Destroy(tex); rt.Release(); Destroy(rt); Destroy(go3);
                    Logger.LogInfo($"  view -> {a[7]}");
                    break;
                }
                case "pos":   // pos <substring>: aircraft-frame positions of the player's aircraft's transforms whose name contains it
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var all = FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).SelectMany(p => p.GetComponentsInChildren<Transform>(true)).Concat(ac.GetComponentsInChildren<Transform>(true)).Distinct();
                    foreach (var t in all.Where(x => x.name.ToLowerInvariant().Contains(arg.ToLowerInvariant())))
                        Logger.LogInfo($"  {t.name}: {ac.transform.InverseTransformPoint(t.position):F3} active={t.gameObject.activeInHierarchy}");
                    break;
                }
                case "camat":   // camat <transform|~object|@ac> <dx> <dy> <dz> <fov> <file>: render from offset (aircraft frame) looking at the transform (~: the centre of the object's rendered bounds; @ac: the player's jet)
                {
                    var a = arg.Split(' ');
                    var cHud = SceneSingleton<CombatHUD>.i;
                    string tname = a[0].TrimStart('~');
                    // a name: not a UI element (the damage display's images carry the part names too; the parts themselves are
                    // scene roots at runtime, not under the jet)
                    var t = a[0] == "@ac" ? (cHud != null && cHud.aircraft != null ? cHud.aircraft.transform : null)
                          : Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(x => x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy && x.name == tname && !(x is RectTransform));
                    if (t == null) { Logger.LogWarning("  no transform " + a[0]); break; }
                    var frame = t.root;
                    float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                    var go3 = new GameObject("devcam"); var cam = go3.AddComponent<Camera>();
                    var aim = t.position;
                    var rb = a[0].StartsWith("~") ? t.GetComponentInChildren<Renderer>() : null;
                    if (rb != null) aim = rb.bounds.center;
                    cam.transform.position = aim + frame.TransformDirection(new Vector3(F(1), F(2), F(3)));
                    cam.transform.LookAt(aim, frame.up);
                    cam.fieldOfView = Mathf.Abs(F(4)); cam.nearClipPlane = 0.05f; cam.farClipPlane = 500f;
                    if (F(4) < 0f) { cam.orthographic = true; cam.orthographicSize = -F(4); cam.farClipPlane = 1.2f * new Vector3(F(1), F(2), F(3)).magnitude; }   // negative fov: orthographic, half-height in m, sees only to just past the target
                    var main = Camera.main; if (main != null) cam.cullingMask = main.cullingMask;
                    var rt = new RenderTexture(1600, 1000, 24); cam.targetTexture = rt; cam.Render();
                    RenderTexture.active = rt; var tex = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0); tex.Apply(); RenderTexture.active = null;
                    File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, a[5]), tex.EncodeToPNG());
                    Destroy(tex); rt.Release(); Destroy(rt); Destroy(go3);
                    Logger.LogInfo($"  camat {t.name} at {t.position:F3} (root {frame.name}) -> {a[5]}");
                    break;
                }
                case "ddopen":   // ddopen <index>: open that dropdown's list (indices as "dropdowns" prints them)
                {
                    var dds = FindObjectsOfType<TMPro.TMP_Dropdown>().Where(d => d.isActiveAndEnabled).OrderBy(d => d.transform.position.x).ToList();
                    if (!int.TryParse(arg, out var k) || k < 0 || k >= dds.Count) { Logger.LogWarning($"  ddopen: no dropdown {arg}"); break; }
                    dds[k].Show();
                    Logger.LogInfo($"  ddopen: {dds[k].name} ({dds[k].options.Count} options)");
                    break;
                }
                case "ddclose":   // close every open dropdown list
                    foreach (var d in FindObjectsOfType<TMPro.TMP_Dropdown>()) if (d.IsExpanded) d.Hide();
                    break;
                case "type":   // type <text>: set the text of the active input field named "Search" (Quartermaster's), as if typed
                {
                    var f = FindObjectsOfType<TMPro.TMP_InputField>().FirstOrDefault(x => x.isActiveAndEnabled && x.name == "Search");
                    if (f == null) { Test(false, "type: no search box open"); break; }
                    f.text = arg == "-" ? "" : arg;
                    Logger.LogInfo($"  type: '{f.text}'");
                    break;
                }
                case "submit":   // press Enter in the search box (Quartermaster takes the first match)
                {
                    var f = FindObjectsOfType<TMPro.TMP_InputField>().FirstOrDefault(x => x.isActiveAndEnabled && x.name == "Search");
                    if (f == null) { Test(false, "submit: no search box open"); break; }
                    f.onSubmit.Invoke(f.text);
                    break;
                }
                case "svinfo":   // Quartermaster's station view: its texts, and the dots shown; "svinfo <n>" checks there are n dots
                {
                    var sv = FindObjectsOfType<RectTransform>().FirstOrDefault(t => t.name == "Quartermaster Stations" && t.gameObject.activeInHierarchy);
                    if (sv == null) { Test(false, "svinfo: no station view"); break; }
                    foreach (var row in sv.GetComponentsInChildren<RectTransform>().Where(t => t.name.StartsWith("Station ")))
                        Logger.LogInfo($"  station: {string.Join(" | ", row.GetComponentsInChildren<TMPro.TMP_Text>().Select(x => x.text))}");
                    foreach (var t in sv.GetComponentsInChildren<TMPro.TMP_Text>().Where(x => x.transform.parent == sv)) Logger.LogInfo($"  text: {t.text}");
                    var dots = sv.GetComponentsInChildren<RectTransform>().Where(t => t.name.StartsWith("Dot ")).ToList();
                    var inside = dots.Count(d => d.anchoredPosition.x > 0 && d.anchoredPosition.y > 0 && d.anchoredPosition.x < ((RectTransform)d.parent).rect.width && d.anchoredPosition.y < ((RectTransform)d.parent).rect.height);
                    Logger.LogInfo($"  svinfo: {dots.Count} dots, {inside} inside the picture");
                    if (int.TryParse(arg, out var want)) Test(dots.Count == want && inside == want, $"svinfo: {dots.Count} dots, {inside} in the picture (expected {want})");
                    else Test(dots.Count > 0 && inside == dots.Count, $"svinfo: {dots.Count} dots, {inside} in the picture");
                    break;
                }
                case "svsave":   // svsave <name>: save the current loadout in Quartermaster's station view
                case "svload":   // svload <name>: apply a saved loadout
                case "svstatus":   // svstatus <text>: check the station view's status line contains the text
                {
                    var t = System.Type.GetType("Quartermaster.StationView, Quartermaster");
                    var sv = t?.GetField("Current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(null);
                    if (sv == null) { Test(false, $"{verb}: no station view"); break; }
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    if (verb == "svsave") t.GetMethod("SaveNamed", flags).Invoke(sv, new object[] { arg });
                    else if (verb == "svload") t.GetMethod("ApplyLoadout", flags, null, new[] { typeof(string) }, null).Invoke(sv, new object[] { arg });
                    var status = (string)t.GetProperty("StatusText", flags).GetValue(sv);
                    if (verb == "svstatus") Test(status.IndexOf(arg, System.StringComparison.OrdinalIgnoreCase) >= 0, $"svstatus: '{status}' (expected '{arg}')");
                    else Logger.LogInfo($"  {verb}: {status}");
                    break;
                }
                case "acsearch":   // acsearch <text|->: type in Quartermaster's aircraft search
                case "acfilter":   // acfilter All|Air|Ground|Mods|Favourites
                case "acstar":     // acstar <aircraft name or key>: star or unstar it
                case "acshown":    // acshown <n>: check n aircraft buttons are shown
                {
                    var t = System.Type.GetType("Quartermaster.AircraftFinder, Quartermaster");
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    var af = t?.GetField("Current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(null);
                    if (af == null) { Test(false, $"{verb}: no aircraft search"); break; }
                    if (verb == "acsearch") t.GetMethod("SetSearch", flags).Invoke(af, new object[] { arg == "-" ? "" : arg });
                    else if (verb == "acfilter") { var et = System.Type.GetType("Quartermaster.AircraftFilter, Quartermaster"); t.GetMethod("SetFilter", flags).Invoke(af, new[] { System.Enum.Parse(et, arg) }); }
                    else if (verb == "acstar") t.GetMethod("ToggleFavourite", flags, null, new[] { typeof(string) }, null).Invoke(af, new object[] { arg });
                    var shown = FindObjectsOfType<AircraftSelectionButton>().Where(b => b.gameObject.activeInHierarchy && (b.GetComponent<CanvasGroup>()?.interactable ?? true)).OrderBy(b => b.transform.GetSiblingIndex())
                        .Select(b => HarmonyLib.Traverse.Create(b).Field("definition").GetValue<AircraftDefinition>()?.unitName).ToList();
                    if (verb == "acshown") Test(shown.Count == int.Parse(arg), $"acshown: {shown.Count} shown (expected {arg}): {string.Join(", ", shown)}");
                    else Logger.LogInfo($"  {verb}: {shown.Count} shown: {string.Join(", ", shown)}");
                    break;
                }
                case "qmrows":   // Quartermaster's open list: the rows shown, top to bottom (headers and weapons); "qmrows <n>" checks there are n weapons
                {
                    var list = FindObjectsOfType<Transform>().FirstOrDefault(t => t.name == "Quartermaster" && t.gameObject.activeInHierarchy)?.parent;
                    if (list == null) { Test(false, "qmrows: no Quartermaster list open"); break; }
                    var content = list.GetComponentInChildren<ScrollRect>(true).content;
                    var rows = content.Cast<Transform>().Where(t => t.gameObject.activeSelf && t.name != "Item")
                        .OrderByDescending(t => ((RectTransform)t).anchoredPosition.y).ToList();
                    foreach (var r in rows)
                        Logger.LogInfo($"  row: {string.Join(" | ", r.GetComponentsInChildren<TMPro.TMP_Text>().Where(x => x.isActiveAndEnabled).Select(x => x.text))}");
                    int weapons = rows.Count(r => !r.name.StartsWith("Header"));
                    if (int.TryParse(arg, out var want)) Test(weapons == want, $"qmrows: {weapons} weapons shown (expected {want})");
                    else Logger.LogInfo($"  qmrows: {weapons} weapons, {rows.Count - weapons} headers");
                    break;
                }
                case "uploaddump":   // the Workshop upload panel's third page (name, description, preview, toggles): every field, to see what is set
                {
                    var p3 = UploadPage3();
                    if (p3 == null) { Test(false, "uploaddump: no upload page open"); break; }
                    foreach (var f in HarmonyLib.Traverse.Create(p3).Fields())
                    {
                        var v = HarmonyLib.Traverse.Create(p3).Field(f).GetValue();
                        string s = v is TMPro.TMP_InputField inp ? $"input '{inp.text}'" : v is Toggle tg ? $"toggle {tg.isOn} ({tg.GetComponentInChildren<TMPro.TMP_Text>()?.text})"
                                 : v is TMPro.TMP_Text tt ? $"text '{tt.text}'" : Fmt(v);
                        Logger.LogInfo($"  page3.{f} = {s}");
                    }
                    break;
                }
                case "uploadprep":   // uploadprep <preview image> <description file>: fill the upload page as a player would (description, Public, Mod Required, preview); uploads nothing
                {
                    var a = arg.Split(' ');
                    var p3 = UploadPage3();
                    if (p3 == null || a.Length < 2) { Test(false, "uploadprep: no upload page open, or no image and description given"); break; }
                    var t = HarmonyLib.Traverse.Create(p3);
                    t.Field("descriptionInput").GetValue<TMPro.TMP_InputField>().text = File.ReadAllText(a[1]).Trim();
                    foreach (var f in t.Fields())
                        if (t.Field(f).GetValue() is Toggle tg && (f.IndexOf("public", System.StringComparison.OrdinalIgnoreCase) >= 0 || f.IndexOf("required", System.StringComparison.OrdinalIgnoreCase) >= 0))
                            tg.isOn = true;
                    // what the game does once its file dialog returns a file (Page3.OpenFileSelector): size check, load, show it, keep the path
                    var type = p3.GetType();
                    var args = new object[] { a[0], null };
                    bool loaded = (bool)HarmonyLib.AccessTools.Method(type, "TryLoadImage").Invoke(null, args);
                    bool small = new FileInfo(a[0]).Length <= 1048576;
                    HarmonyLib.AccessTools.Method(type, "SetPreviewTexture").Invoke(p3, new[] { loaded ? args[1] : null });
                    t.Field("imagePath").SetValue(loaded && small ? a[0] : null);
                    t.Field("needPreviewWarning").GetValue<TMPro.TMP_Text>().text = loaded && small ? "" : "Preview not set";
                    bool can = (bool)HarmonyLib.AccessTools.Method(type, "CanUpload").Invoke(p3, null);
                    Test(loaded && small && can, $"uploadprep: preview {(loaded ? "loaded" : "not loaded")}, {new FileInfo(a[0]).Length / 1024} KB, ready to upload {can}");
                    break;
                }
                case "uploadstatus":   // after Create New: the upload page's result ("Uploading", "Success", "Failed: ...") and the item's Workshop id
                {
                    var type = HarmonyLib.AccessTools.TypeByName("UploadWorkshopPanel");
                    var panel = type == null ? null : FindObjectsOfType(type).FirstOrDefault(p => ((Component)p).gameObject.activeInHierarchy);
                    if (panel == null) { Test(false, "uploadstatus: no upload panel open"); break; }
                    var label = HarmonyLib.Traverse.Create(panel).Field("page4").Field("uploadingLabel").GetValue<TMPro.TMP_Text>()?.text;
                    var item = HarmonyLib.Traverse.Create(panel).Field("page3").Field("item").GetValue();
                    var id = item == null ? "?" : HarmonyLib.Traverse.Create(item).Property("WorkshopId").GetValue()?.ToString();
                    Test(label == "Success", $"uploadstatus: '{label}', Workshop id {id}");
                    break;
                }
                case "liveryupload":   // liveryupload <Skins folder>: the game's own upload check for that livery (what Workshop > Upload > Aircraft Livery runs), its tags; uploads nothing
                {
                    var mod = new NuclearOption.ModScripts.Impl.AircraftLiveryMod();
                    Logger.LogInfo($"  livery folders: {string.Join(", ", mod.ListItems())}");
                    bool ok = mod.TryGetItem(arg, out var item, out var err);
                    var tags = item == null ? "" : string.Join(", ", HarmonyLib.Traverse.Create(item).Fields().Select(f => f + "=" + Fmt(HarmonyLib.Traverse.Create(item).Field(f).GetValue())));
                    Test(ok, $"liveryupload {arg}: {(ok ? tags : err)}");
                    break;
                }
                case "dropdowns":   // list TMP dropdowns and their options
                {
                    var dds = FindObjectsOfType<TMPro.TMP_Dropdown>().Where(d => d.isActiveAndEnabled).OrderBy(d => d.transform.position.x).ToList();
                    for (int i = 0; i < dds.Count; i++)
                        Logger.LogInfo($"  dropdown {i} {dds[i].name} value={dds[i].value}: {string.Join(" | ", dds[i].options.Select((o, k) => k + "=" + o.text))}");
                    break;
                }
                case "liveryshots":   // liveryshots <object> <prefix> [<option text>]: every option of the livery dropdown (the one offering that, default "Desert"), the preview jet from above, the side and the tail
                {
                    var a = arg.Split(' ');
                    var offer = a.Length > 2 ? a[2] : "Desert";
                    var dd = FindObjectsOfType<TMPro.TMP_Dropdown>().FirstOrDefault(d => d.isActiveAndEnabled && d.options.Any(o => o.text.IndexOf(offer, System.StringComparison.OrdinalIgnoreCase) >= 0));
                    var obj = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(x => x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy && x.name == a[0]);
                    if (dd == null || obj == null) { Logger.LogWarning($"  liveryshots: {(dd == null ? "no livery dropdown" : "no object " + a[0])}"); break; }
                    StartCoroutine(LiveryShots(dd, obj, a[1]));
                    break;
                }
                case "pickopt":   // pickopt <option text>: in whichever active dropdown offers it, select the option whose text contains that
                {
                    var dd = FindObjectsOfType<TMPro.TMP_Dropdown>().FirstOrDefault(d => d.isActiveAndEnabled && d.options.Any(o => o.text.IndexOf(arg, System.StringComparison.OrdinalIgnoreCase) >= 0));
                    if (dd == null) { Logger.LogWarning($"  pickopt: no dropdown offers '{arg}'"); break; }
                    int k = dd.options.FindIndex(o => o.text.IndexOf(arg, System.StringComparison.OrdinalIgnoreCase) >= 0);
                    dd.value = k; dd.RefreshShownValue();
                    Logger.LogInfo($"  pickopt: {dd.name} -> {dd.options[k].text}");
                    break;
                }
                case "pickin":   // pickin <dropdown index> <option text>: in that dropdown (ordered as "dropdowns"), the option named exactly that, else the first containing it
                {
                    var sp = arg.IndexOf(' '); int di = int.Parse(arg.Substring(0, sp)); var txt = arg.Substring(sp + 1).Trim();
                    var dds = FindObjectsOfType<TMPro.TMP_Dropdown>().Where(d => d.isActiveAndEnabled).OrderBy(d => d.transform.position.x).ToList();
                    var dd = dds[di];
                    int k = dd.options.FindIndex(o => string.Equals(o.text.Trim(), txt, System.StringComparison.OrdinalIgnoreCase));
                    if (k < 0) k = dd.options.FindIndex(o => o.text.IndexOf(txt, System.StringComparison.OrdinalIgnoreCase) >= 0);
                    if (k < 0) { Test(false, $"pickin: dropdown {di} has no '{txt}'"); break; }
                    dd.value = k; dd.RefreshShownValue();
                    Logger.LogInfo($"  pickin: dropdown {di} -> {dd.options[k].text}");
                    break;
                }
                case "pickfree":   // pickfree <option text>: as pickopt, but in a dropdown no earlier pickfree used (several stores on one jet)
                {
                    var dd = FindObjectsOfType<TMPro.TMP_Dropdown>().Where(d => d.isActiveAndEnabled && !pickedDropdowns.Contains(d.GetInstanceID()))
                        .OrderBy(d => d.transform.position.x).FirstOrDefault(d => d.options.Any(o => o.text.IndexOf(arg, System.StringComparison.OrdinalIgnoreCase) >= 0));
                    if (dd == null) { Test(false, $"pickfree: no free dropdown offers '{arg}'"); break; }
                    // an exact name first, so "Kh-29T" does not take the Kh-29TD
                    int k = dd.options.FindIndex(o => string.Equals(o.text.Trim(), arg, System.StringComparison.OrdinalIgnoreCase));
                    if (k < 0) k = dd.options.FindIndex(o => o.text.IndexOf(arg, System.StringComparison.OrdinalIgnoreCase) >= 0);
                    dd.value = k; dd.RefreshShownValue(); pickedDropdowns.Add(dd.GetInstanceID());
                    Test(true, $"pickfree: {dd.name} -> {dd.options[k].text}");
                    break;
                }
                case "firetest":   // firetest <name part>, <name part>, ...: at 1,500 m and 220 m/s, fire each weapon in turn (guns for 0.6 s)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    StartCoroutine(FireTest(ac, arg.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList()));
                    break;
                }
                case "weps":   // the player's weapon stations: weapon name and ammo
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    foreach (var w in ac.weaponStations) if (w?.WeaponInfo != null) Logger.LogInfo($"  station: {w.WeaponInfo.weaponName} ({w.WeaponInfo.shortName}) ammo {w.Ammo}");
                    break;
                }
                case "cm":   // cm <station name> [expected]: the player's countermeasure station's ammo (IR Flares, Radar Chaff, Radar ECM...)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    var cm = typeof(Aircraft).GetFields(bf).FirstOrDefault(f => f.FieldType == typeof(CountermeasureManager))?.GetValue(ac) as CountermeasureManager;
                    var stations = cm != null ? typeof(CountermeasureManager).GetField("countermeasureStations", bf)?.GetValue(cm) as System.Collections.IList : null;
                    var a = arg.Split(' ').ToList(); int expect = -1;
                    if (a.Count > 1 && int.TryParse(a[a.Count - 1], out expect)) a.RemoveAt(a.Count - 1); else expect = -1;
                    string name = string.Join(" ", a);
                    int ammo = -1;
                    if (stations != null)
                        foreach (var st in stations)
                        {
                            var n = (string)st.GetType().GetField("displayName").GetValue(st); var am = (int)st.GetType().GetField("ammo").GetValue(st);
                            var list = st.GetType().GetField("countermeasures", bf)?.GetValue(st) as System.Collections.IList;
                            Logger.LogInfo($"  cm station {n}: {am} ({list?.Count} dispensers)");
                            if (n == name) ammo = am;
                        }
                    if (expect >= 0) Test(ammo == expect, $"{name}: {ammo} (expected {expect})");
                    break;
                }
                case "podfire":   // podfire <n>: fire every countermeasure carried on the wingtip pods n times (the ejection point fix, the pods' own rounds)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    // the game re-parents parts out of the aircraft's hierarchy: look through the scene for the pods under this jet's wingtip hardpoints
                    var pods = FindObjectsOfType<Countermeasure>().Where(c => c.transform.GetComponentsInParent<Transform>().Any(t => t.name.StartsWith("hardpoint_wingtip"))
                                                                          && (c.aircraft == ac || c.GetComponentInParent<UnitPart>()?.parentUnit == ac)).ToList();
                    int n = int.TryParse(arg, out var nn) ? nn : 1;
                    foreach (var c in pods)
                    {
                        int before = c.ammo;
                        try { for (int i = 0; i < n; i++) c.Fire(); Test(true, $"podfire {c.GetType().Name} on {c.transform.parent?.name}: {before} -> {c.ammo}"); }
                        catch (System.Exception e) { Test(false, $"podfire {c.GetType().Name}: {e.GetType().Name} {e.Message}"); }
                    }
                    if (pods.Count == 0) Test(false, "podfire: no countermeasures on the wingtip hardpoints");
                    break;
                }
                case "pick":   // pick <dropdown index> <option index>
                {
                    var a = arg.Split(' ');
                    var dds = FindObjectsOfType<TMPro.TMP_Dropdown>().Where(d => d.isActiveAndEnabled).OrderBy(d => d.transform.position.x).ToList();
                    var dd = dds[int.Parse(a[0])]; dd.value = int.Parse(a[1]);
                    Logger.LogInfo($"  {dd.name} = {dd.options[dd.value].text}");
                    break;
                }
                case "mounts":   // what hangs under each hardpoint: renderer bounds relative to the hardpoint, in its frame
                    foreach (var hp in Resources.FindObjectsOfTypeAll<Transform>().Where(t => t.gameObject.scene.IsValid() && t.gameObject.activeInHierarchy && t.name.StartsWith("hardpoint_")))
                        foreach (var r in hp.GetComponentsInChildren<Renderer>().Where(r => r.enabled))
                        {
                            var mf = r.GetComponent<MeshFilter>();
                            if (mf == null || mf.sharedMesh == null) continue;
                            var b = mf.sharedMesh.bounds; float top = float.MinValue;
                            foreach (var c in new[] { new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(-1, 1, -1), new Vector3(1, 1, -1), new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(-1, 1, 1), new Vector3(1, 1, 1) })
                                top = Mathf.Max(top, hp.InverseTransformPoint(r.transform.TransformPoint(b.center + Vector3.Scale(b.extents, c))).y);
                            Logger.LogInfo($"  {hp.name} / {r.transform.parent?.name}/{r.name}: top {top:F3} m from the hardpoint");
                        }
                    break;
                case "fps":   // average frame time over the next N seconds (default 3)
                    StartCoroutine(Fps(arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 3f));
                    break;
                case "config":   // config <plugin guid>|<section>|<key>|<value>
                {
                    var a = arg.Split('|');
                    var info = BepInEx.Bootstrap.Chainloader.PluginInfos[a[0]];
                    var entry = info.Instance.Config[a[1], a[2]];
                    entry.BoxedValue = TomlTypeConverter.ConvertToValue(a[3], entry.SettingType);
                    Logger.LogInfo($"  {a[0]} {a[1]}/{a[2]} = {entry.BoxedValue}");
                    break;
                }
                case "gear":   // each LandingGear: compression, max, hinge local euler, enabled
                {
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    foreach (var lg in FindObjectsOfType<MonoBehaviour>().Where(m => m.GetType().Name == "LandingGear"))
                    {
                        var ty = lg.GetType();
                        object G(string n) { var fi = ty.GetField(n, bf); return fi != null ? fi.GetValue(lg) : "?"; }
                        var hinge = G("gearHinge") as Component;
                        Logger.LogInfo($"  {lg.name} enabled={lg.enabled} comp={G("compressionDistance")} max={G("maxCompression")} fold={G("foldAmount")} travel={G("suspensionTravel")} force={G("compressionForce")} damp={G("dampingForce")} hinge={(hinge != null ? hinge.name + " " + hinge.transform.localEulerAngles.ToString("F2") : "null")}");
                    }
                    break;
                }
                case "drop":   // drop <height m> <sink m/s> [forward m/s]: lift the player's jet and drop it, logging the gear for 4 s
                {
                    var a = arg.Split(' ');
                    float h = float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture), vz = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
                    float fwd = a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var rbs = FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).Select(p => p.rb).Where(r => r != null).Distinct().ToList();
                    foreach (var r in rbs) { r.isKinematic = false; r.position += Vector3.up * h; r.velocity = Vector3.down * vz + ac.transform.forward * fwd; r.angularVelocity = Vector3.zero; }
                    Logger.LogInfo($"  dropped {rbs.Count} bodies {h} m at {vz} m/s");
                    StartCoroutine(GearLog(4f));
                    break;
                }
                case "hold":   // hold <height m>: lift the player's jet and freeze it (kinematic) there; "hold 0" releases it
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    float h = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    foreach (var r in FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).Select(p => p.rb).Where(r => r != null).Distinct())
                    {
                        if (h > 0f) { r.isKinematic = true; r.position += Vector3.up * h; r.transform.position += Vector3.up * h; }
                        else { r.isKinematic = false; r.velocity = Vector3.zero; }
                    }
                    Logger.LogInfo($"  hold {h}");
                    break;
                }
                case "move":   // move <dy m> [forward m]: shift the (held) player's jet up or down (and along its heading), staying kinematic
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var mv = arg.Split(' ');
                    float dy = float.Parse(mv[0], System.Globalization.CultureInfo.InvariantCulture);
                    float dz = mv.Length > 1 ? float.Parse(mv[1], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                    var fw = Vector3.ProjectOnPlane(ac.transform.forward, Vector3.up).normalized;
                    var d = Vector3.up * dy + fw * dz;
                    foreach (var r in FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).Select(p => p.rb).Where(r => r != null).Distinct())
                    { r.position += d; r.transform.position += d; }
                    Logger.LogInfo($"  moved {dy}, radar alt {ac.radarAlt:F1}");
                    break;
                }
                case "skinprefix":   // skinprefix <renderer name prefix>: which renderers dmgtex / skinhp / skinprop / skintex act on (default MiG29_skin)
                    skinPrefix = arg; Logger.LogInfo($"  skin commands now act on {arg}*"); break;
                case "dmgtex":   // dmgtex <basecolorDmg png|-> [normalDmg png|-]: replace the damage textures on the player's jet's skin materials (runtime only)
                case "skinhp":   // skinhp <v>: set _HitPoints on the player's jet's skin materials (look only, no damage)
                case "skinprop":   // skinprop <name> <float>: any float on those materials
                case "skintex":    // skintex <texture property> <png|@white|@flatnormal>: any texture on those materials (normal maps load linear)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(' ');
                    Texture2D Load(string p, bool lin)
                    {
                        if (p == "-" ) return null;
                        if (p == "@white" || p == "@flatnormal")   // built in: a plain light grey paint, a flat normal map
                        {
                            var c = p == "@white" ? new Color32(235, 235, 235, 255) : new Color32(128, 128, 255, 255);
                            var flat = new Texture2D(4, 4, TextureFormat.RGBA32, false, lin);
                            flat.SetPixels32(Enumerable.Repeat(c, 16).ToArray()); flat.Apply();
                            return flat;
                        }
                        var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, lin);
                        ImageConversion.LoadImage(t, System.IO.File.ReadAllBytes(p));
                        return t;
                    }
                    Texture2D b = null, n = null;
                    if (verb == "dmgtex") { b = Load(a[0], false); if (a.Length > 1) n = Load(a[1], true); }
                    if (verb == "skintex") b = Load(a[1], a[0].Contains("Normal"));
                    int count = 0;
                    foreach (var r in FindObjectsOfType<Renderer>())   // complex physics re-parents the parts
                    {
                        var m = r.sharedMaterial;
                        if (m == null || !m.HasProperty("_HitPoints") || !r.name.StartsWith(skinPrefix)) continue;
                        m = r.material; count++;
                        if (verb == "dmgtex") { if (b != null) m.SetTexture("_BasecolorDmg", b); if (n != null) m.SetTexture("_NormalDmg", n); }
                        else if (verb == "skintex") m.SetTexture(a[0], b);
                        else if (verb == "skinhp") m.SetFloat("_HitPoints", float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture));
                        else m.SetFloat(a[0], float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    Logger.LogInfo($"  {verb} {arg}: {count} skin renderers");
                    break;
                }
                case "hurt":      // hurt <part substring|all> <amount>: ApplyDamage on the player's jet's parts (shading/effects, no detaching)
                case "burn":      // burn <part> <amount>: the same as fire damage (a leaking fuel tank catches)
                case "partvis":   // partvis <part substring> on|off: show/hide every renderer of the matching parts (to see the seams they leave)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(' ');
                    var sel = FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac && (a[0] == "all" || p.name == a[0] || (a[0].EndsWith("*") && p.name.StartsWith(a[0].TrimEnd('*'))))).ToList();
                    foreach (var p in sel)
                    {
                        if (verb == "hurt") p.ApplyDamage(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), 0f, 0f, 0f);
                        else if (verb == "burn") p.ApplyDamage(0f, 0f, float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), 0f);
                        else foreach (var r in p.GetComponentsInChildren<Renderer>(true).Where(r => r.GetComponentInParent<UnitPart>() == p)) r.enabled = a[1] == "on";
                    }
                    Logger.LogInfo($"  {verb} {arg}: {sel.Count} parts ({string.Join(",", sel.Select(p => p.name))})");
                    break;
                }
                case "fire":   // fire <from x y z> <to x y z> [damage]: a ray in the aircraft frame; the first collider it hits goes to the MiG plugin's Holes.Hit (as a round would), and its part takes the damage
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(' ');
                    float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                    var from = ac.transform.TransformPoint(new Vector3(F(0), F(1), F(2))); var to = ac.transform.TransformPoint(new Vector3(F(3), F(4), F(5)));
                    var dir = (to - from).normalized;
                    if (!Physics.Raycast(from, dir, out var fhit, (to - from).magnitude + 5f)) { Logger.LogWarning("  fire: no hit"); break; }
                    var holes = System.AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("MiG29Instruments.Holes")).FirstOrDefault(x => x != null);
                    var m = holes?.GetMethod("Hit", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    m?.Invoke(null, new object[] { fhit.collider, fhit.point, dir, 0.045f });
                    var part = fhit.collider.GetComponentInParent<UnitPart>();
                    if (part != null && a.Length > 6) part.ApplyDamage(F(6), 0f, 0f, 0f);
                    Logger.LogInfo($"  fire: hit {fhit.collider.name} part {part?.name} at {ac.transform.InverseTransformPoint(fhit.point):F2} holes {(m != null)}");
                    break;
                }
                case "rlayer":   // rlayer <name prefix> <layer>: move the player's jet's renderers whose name starts with the prefix to that layer
                {
                    var a = arg.Split(' ');
                    int nr = 0;
                    foreach (var r in FindObjectsOfType<Renderer>(true)) if (r.name.StartsWith(a[0]) && r.GetComponentInParent<UnitPart>() != null) { r.gameObject.layer = int.Parse(a[1]); nr++; }
                    Logger.LogInfo($"  rlayer {arg}: {nr} renderers");
                    break;
                }
                case "rvis":   // rvis <name prefix> on|off: show/hide the player's jet's renderers whose name starts with the prefix
                {
                    var a = arg.Split(' ');
                    int nr = 0;
                    foreach (var r in FindObjectsOfType<Renderer>(true)) if (r.name.StartsWith(a[0]) && r.GetComponentInParent<UnitPart>() != null) { r.enabled = a[1] == "on"; nr++; }
                    Logger.LogInfo($"  rvis {arg}: {nr} renderers");
                    break;
                }
                case "matinfo":   // matinfo <name prefix>: the player's jet's renderers whose name starts with it: material, shader, textures and floats
                {
                    foreach (var r in FindObjectsOfType<Renderer>(true))
                    {
                        if (!r.name.StartsWith(arg) || r.GetComponentInParent<UnitPart>() == null) continue;
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) continue;
                            var props = string.Join(", ", m.GetTexturePropertyNames().Select(n => $"{n}={(m.GetTexture(n) != null ? $"{m.GetTexture(n).name} {m.GetTexture(n).width}x{m.GetTexture(n).height}" : "-")}"));
                            var mesh = r.GetComponent<MeshFilter>()?.sharedMesh; var uv = new List<Vector2>(); mesh?.GetUVs(0, uv);
                            string uvr = uv.Count == 0 ? "none" : $"u {uv.Min(x => x.x):F2}..{uv.Max(x => x.x):F2} v {uv.Min(x => x.y):F2}..{uv.Max(x => x.y):F2}";
                            Logger.LogInfo($"  {r.name} enabled={r.enabled} mat={m.name} shader={m.shader.name} hp={(m.HasProperty("_HitPoints") ? m.GetFloat("_HitPoints").ToString("F0") : "-")} verts={mesh?.vertexCount} uv0 {uvr} readable={mesh?.isReadable} {props}");
                        }
                    }
                    break;
                }
                case "rnear":   // rnear <x y z> <radius>: the player's jet's renderers whose bounds come within radius of that point (aircraft frame)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(' ');
                    float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                    var w = ac.transform.TransformPoint(new Vector3(F(0), F(1), F(2)));
                    foreach (var r in FindObjectsOfType<Renderer>().Where(r => r.enabled && r.GetComponentInParent<UnitPart>()?.parentUnit == ac && r.bounds.SqrDistance(w) < F(3) * F(3)))
                        Logger.LogInfo($"    {r.name} (part {r.GetComponentInParent<UnitPart>().name}) mat {r.sharedMaterial?.name} size {r.bounds.size:F2}");
                    break;
                }
                case "rabove":   // rabove <y>: the player's jet's enabled renderers reaching above that height (aircraft frame), with their extents
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    float y0 = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    foreach (var r in FindObjectsOfType<Renderer>().Where(r => r.enabled && (r.GetComponentInParent<UnitPart>()?.parentUnit == ac || r.GetComponentInParent<Unit>() == ac)))
                    {
                        var b = r.bounds; var lo = ac.transform.InverseTransformPoint(b.min); var hi = ac.transform.InverseTransformPoint(b.max);
                        var top = Mathf.Max(lo.y, hi.y);
                        if (top > y0) Logger.LogInfo($"    {r.name} parent {r.transform.parent?.name} top {top:F2} centre {ac.transform.InverseTransformPoint(b.center):F2} size {b.size:F2} mat {r.sharedMaterial?.name}");
                    }
                    break;
                }
                case "wview":   // wview <object name> <dx dy dz> <fov> <file>: camera at the object's position + a world offset, looking at it (works on wrecks)
                {
                    var a = arg.Split(' ');
                    float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                    var target = FindObjectsOfType<Transform>().FirstOrDefault(x => x.name == a[0] && x.gameObject.activeInHierarchy);
                    if (target == null) { Logger.LogWarning($"  no object {a[0]}"); break; }
                    var go3 = new GameObject("devcam"); var cam = go3.AddComponent<Camera>();
                    cam.transform.position = target.position + new Vector3(F(1), F(2), F(3)); cam.transform.LookAt(target.position, Vector3.up);
                    cam.fieldOfView = F(4); cam.nearClipPlane = 0.05f; cam.farClipPlane = 2000f;
                    var main = Camera.main; if (main != null) cam.cullingMask = main.cullingMask;
                    var rt = new RenderTexture(1600, 1000, 24); cam.targetTexture = rt; cam.Render();
                    RenderTexture.active = rt; var tex = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0); tex.Apply(); RenderTexture.active = null;
                    File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, a[5]), tex.EncodeToPNG());
                    Destroy(tex); rt.Release(); Destroy(rt); Destroy(go3);
                    Logger.LogInfo($"  wview {a[0]} at {target.position} -> {a[5]}");
                    break;
                }
                case "shaderprops":   // shaderprops: the skin shader's properties
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var r = ac.GetComponentsInChildren<Renderer>(true).First(x => x.name.StartsWith("MiG29_skin"));
                    var s = r.sharedMaterial.shader;
                    Logger.LogInfo($"  shader {s.name} keywords {string.Join(",", r.sharedMaterial.shaderKeywords)}");
                    for (int i = 0; i < s.GetPropertyCount(); i++) Logger.LogInfo($"  prop {s.GetPropertyName(i)} {s.GetPropertyType(i)} {s.GetPropertyDescription(i)}");
                    break;
                }
                case "cfgset":   // cfgset <plugin GUID>|<section>|<key>|<value>: sets a plugin's config entry live (SettingChanged fires)
                {
                    var a = arg.Split('|');
                    if (a.Length < 4) { Logger.LogWarning("  cfgset guid|section|key|value"); break; }
                    if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(a[0], out var pi)) { Logger.LogWarning("  no plugin " + a[0]); break; }
                    var cfg = pi.Instance.Config;
                    var def = new BepInEx.Configuration.ConfigDefinition(a[1], a[2]);
                    if (!cfg.TryGetEntry<string>(def, out var e)) { Logger.LogWarning($"  no string entry {a[1]}/{a[2]}"); break; }
                    try { e.Value = a[3]; Logger.LogInfo($"  {a[1]}/{a[2]} = {e.Value}"); } catch (System.Exception ex) { Logger.LogWarning("  " + ex.Message); }
                    break;
                }
                case "texlimit":   // texlimit <n>: QualitySettings.globalTextureMipmapLimit (0 full, 1 half, 2 quarter: the game's Texture Quality)
                    QualitySettings.globalTextureMipmapLimit = int.Parse(arg);
                    Logger.LogInfo($"  globalTextureMipmapLimit {QualitySettings.globalTextureMipmapLimit}");
                    break;
                case "lodbias":   // lodbias <v>: QualitySettings.lodBias (small values force the distance levels up close)
                    QualitySettings.lodBias = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    Logger.LogInfo($"  lodBias {QualitySettings.lodBias}");
                    break;
                case "blastlines":   // blastlines <yield> <dx dy dz>: for each part, the blast's line to its centre (as the game traces it) and to 9 more points: the centres of its hull's eight octants and the hull point nearest the jet's centre (pulled 20% in); each with the distance it meets the part and the shielding on the way
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    float range = Mathf.Pow(a[0], 0.3333f) * 20f; var o = ac.transform.TransformPoint(new Vector3(a[1], a[2], a[3]));
                    int mask = ~((int)PhysicsLayers.ExclusionZonesMask | (int)PhysicsLayers.IgnoreCollisionsMask);
                    string Trace(Collider c, Vector3 to)
                    {
                        var dir = (to - o).normalized * range; var from = o; float dist = 0f, shield = 0f;
                        for (int n = 0; n < 10 && Physics.Linecast(from, from + dir, out var h, mask); n++)
                        {
                            dist += h.distance;
                            if (h.collider == c) return $"{dist:F2}:{shield:F1}";
                            var ap = h.collider.GetComponent<IDamageable>()?.GetArmorProperties();
                            if (ap == null) { if (h.collider.sharedMaterial == null) break; from = h.point + dir.normalized * 0.1f; continue; }
                            shield += ap.blastArmor * 0.1f; from = h.point + dir.normalized * 0.1f;
                        }
                        return "miss";
                    }
                    foreach (var c in Physics.OverlapSphere(o, range).Where(c => c.GetComponent<UnitPart>() is UnitPart up && up.parentUnit == ac).OrderBy(c => c.name))
                    {
                        var pts = new List<Vector3> { c.transform.position };
                        var mc = c as MeshCollider; var lb = mc != null && mc.sharedMesh != null ? mc.sharedMesh.bounds : new Bounds(c.transform.InverseTransformPoint(c.bounds.center), Vector3.one * 0.2f);
                        for (int i = 0; i < 8; i++) pts.Add(c.transform.TransformPoint(lb.center + Vector3.Scale(lb.extents * 0.5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                        var near = c.ClosestPoint(ac.rb.worldCenterOfMass); pts.Add(Vector3.Lerp(near, c.bounds.center, 0.2f));
                        Logger.LogInfo($"    lines to {c.name}: {string.Join(" ", pts.Select(p => Trace(c, p)))} hull {c.bounds.size.x:F2} {c.bounds.size.y:F2} {c.bounds.size.z:F2}");
                    }
                    break;
                }
                case "blastrays":   // blastrays <yield> <dx dy dz>: the lines a missile's blast traces to each part (as DamageEffects.FragTrace), with every part each passes through on the way; no damage
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    float bp = Mathf.Pow(a[0], 0.3333f), range = bp * 20f; var o = ac.transform.TransformPoint(new Vector3(a[1], a[2], a[3]));
                    var mask = ~(LayerMask.GetMask() | (int)PhysicsLayers.ExclusionZonesMask | (int)PhysicsLayers.IgnoreCollisionsMask);
                    foreach (var c in Physics.OverlapSphere(o, range).Where(c => c.GetComponent<UnitPart>() is UnitPart up && up.parentUnit == ac).OrderBy(c => c.name))
                    {
                        var dir = (c.transform.position - o).normalized * range; var from = o; float dist = 0f, shield = 0f; var path = new List<string>(); string got = "missed";
                        for (int n = 0; n < 10 && Physics.Linecast(from, from + dir, out var h, mask); n++)
                        {
                            dist += h.distance;
                            if (h.collider == c) { got = $"hit at {dist:F1} m"; break; }
                            var dm = h.collider.GetComponent<IDamageable>(); var ap = dm?.GetArmorProperties();
                            if (dm == null || ap == null) { path.Add(h.collider.name + "(not a part)"); if (h.collider.sharedMaterial == null) break; from = h.point + dir.normalized * 0.1f; continue; }
                            shield += ap.blastArmor * 0.1f; path.Add(h.collider.name); from = h.point + dir.normalized * 0.1f;
                        }
                        Logger.LogInfo($"    ray to {c.name}: {got}, shielded by {shield:F1} m through [{string.Join(", ", path)}], hull {c.bounds.size:F2}");
                    }
                    break;
                }
                case "blasttest":   // blasttest <yield> <dx dy dz>: jet to 1,500 m at 220 m/s, then a missile's blast (DamageEffects.BlastFrag, as Missile detonates) at that point in the aircraft frame; logs every joint that breaks, frame by frame, and each part's health
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    StartCoroutine(BlastTest(ac, a[0], new Vector3(a[1], a[2], a[3])));
                    break;
                }
                case "tanktest":   // tanktest <drain kg> <cruise s> [speed m/s] [nodrop] [snap] [lift=<m>]: at 300 m (lift) and 220 m/s (speed), empty that much internal fuel, cruise (the MiG plugin moves drop-tank fuel in), then drop the PTB-1500 and log the pylon parts' mass each physics step
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(' ');
                    StartCoroutine(TankTest(ac, float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 220f, a.Contains("nodrop"), a.Contains("snap"), a.FirstOrDefault(x => x.StartsWith("lift="))?.Substring(5) is string lf ? float.Parse(lf, System.Globalization.CultureInfo.InvariantCulture) : 300f));
                    break;
                }
                case "parts":   // parts: every part of the player's jet: joints intact / total, hit points, own rigidbody or merged
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    int broken = 0, total = 0;
                    foreach (var p in FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).OrderBy(p => p.name))
                    {
                        var joints = p.GetType().GetField("joints", bf)?.GetValue(p) as System.Array;
                        int n = joints?.Length ?? 0, ok = 0;
                        if (joints != null) foreach (var j in joints) { var jj = j.GetType().GetField("joint", bf)?.GetValue(j) as Joint; if (jj != null) ok++; }
                        total += n; broken += n - ok;
                        var hp = p.GetType().GetField("hitPoints", bf)?.GetValue(p);
                        var mp = p.transform.Find("MiG29_massprops"); Logger.LogInfo($"  {p.name}: joints {ok}/{n} hp {hp} {(p.rb != null && p.rb != ac.rb ? "own rb" : "merged")} pos {ac.transform.InverseTransformPoint(p.transform.position):F2} com {(p.rb != null ? p.rb.centerOfMass.ToString("F3") : "-")} tuned {(mp != null ? mp.localPosition.ToString("F3") : "-")}");
                    }
                    Logger.LogInfo($"  parts summary: {broken} of {total} joints broken, alt {ac.radarAlt:F1} speed {ac.speed:F1}");
                    Test(broken == 0, $"parts: {broken} of {total} joints broken");
                    break;
                }
                case "gtest":   // gtest <pitch deg>: player's jet to 1,500 m up at 220 m/s, then nose up by that much; logs g and how far each part moves off the root
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    StartCoroutine(GTest(ac, float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture)));   // on the driver: the SMT's glass HUD hides the CombatHUD, which stops a coroutine on it
                    break;
                }
                case "pickac":   // pickac <definition name>: clicks the aircraft selection button for that aircraft (exact name, e.g. MiG29SMT)
                {
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    bool done = false;
                    foreach (var b in FindObjectsOfType<MonoBehaviour>().Where(m => m.GetType().Name == "AircraftSelectionButton"))
                    {
                        var def = b.GetType().GetField("definition", bf)?.GetValue(b) as UnityEngine.Object;
                        if (def == null || def.name != arg) continue;
                        var btn = b.GetType().GetField("button", bf)?.GetValue(b) as UnityEngine.UI.Button;
                        btn?.onClick.Invoke();
                        Logger.LogInfo($"  picked {def.name}"); done = true; break;
                    }
                    if (!done) Logger.LogWarning("  no aircraft button for " + arg + "; have: " + string.Join(", ", FindObjectsOfType<MonoBehaviour>().Where(m => m.GetType().Name == "AircraftSelectionButton").Select(m => (m.GetType().GetField("definition", bf)?.GetValue(m) as UnityEngine.Object)?.name)));
                    break;
                }
                case "mkgauge":   // mkgauge <object name>: a small cube under the player's cockpit with that name, then NO Gauges' AttachAll
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var parent = ac.cockpit != null ? ((Component)ac.cockpit).transform : ac.transform;
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(cube.GetComponent<Collider>());
                    cube.name = arg; cube.transform.SetParent(parent, false); cube.transform.localScale = Vector3.one * 0.05f;
                    var gs = System.Type.GetType("NOGauges.GaugeSystem, NOGauges");
                    var n = gs?.GetMethod("AttachAll")?.Invoke(null, new object[] { ac });
                    Logger.LogInfo($"  made {arg}; AttachAll -> {n}");
                    break;
                }
                case "gauges":   // every NO Gauges element on the player's aircraft: spec, current value, pose
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var gt = System.Type.GetType("NOGauges.Gauge, NOGauges");
                    if (gt == null) { Logger.LogWarning("  NO Gauges not loaded"); break; }
                    foreach (var g in FindObjectsOfType(gt).Cast<Component>())
                    {
                        var mv = (Transform)gt.GetProperty("Moving").GetValue(g);
                        var r = g.GetComponent<Renderer>();
                        Logger.LogInfo($"  {g.name}: {gt.GetProperty("Spec").GetValue(g)} current {gt.GetProperty("Current").GetValue(g)} euler {mv.localEulerAngles:F1} pos {mv.localPosition:F3} renderer {(r != null ? r.enabled.ToString() : "-")}");
                    }
                    break;
                }
                case "state":   // speed, radar alt, throttle, gear of the player's jet
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var inp = ac.GetInputs();
                    var eu = ac.transform.eulerAngles; float Sg(float d) => d > 180f ? d - 360f : d;
                    var w = ac.rb != null ? ac.transform.InverseTransformDirection(ac.rb.angularVelocity) * Mathf.Rad2Deg : Vector3.zero;
                    Logger.LogInfo($"  speed {ac.speed:F1} m/s, radar alt {ac.radarAlt:F2}, throttle {(inp != null ? inp.throttle : -1f):F2}, gear {ac.gearState}, chute {(GameObject.Find("MiG29_brake_chute") != null)}, "
                        + $"pitch {Mathf.Asin(Mathf.Clamp(ac.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg:F1} roll {RollOf(ac):F1} hdg {eu.y:F0}, rates pitch {-w.x:F1} roll {-w.z:F1} yaw {w.y:F1} deg/s"
                        + (StickHold.Level != null ? $", hold stick pitch {StickHold.LastLevelInput.x:F2} roll {StickHold.LastLevelInput.y:F2}" : ""));
                    break;
                }
                case "setgear":   // setgear true|false
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    ac.SetGear(bool.Parse(arg));
                    Logger.LogInfo($"  gear {arg}, state {ac.gearState}");
                    break;
                }
                case "under":   // under <hardpoint>: every enabled renderer of the aircraft whose bounds cover the hardpoint's x/z, with its bottom height relative to the hardpoint (hardpoint frame)
                {
                    var hp = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(x => x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy && x.name == arg);
                    if (hp == null) { Logger.LogWarning("  no " + arg); break; }
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    foreach (var r in FindObjectsOfType<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy && r.transform.IsChildOf(hp.root) && !r.transform.IsChildOf(hp)))
                    {
                        var mf = r.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue;
                        var b = mf.sharedMesh.bounds; float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                        foreach (var c in new[] { new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(-1, 1, -1), new Vector3(1, 1, -1), new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(-1, 1, 1), new Vector3(1, 1, 1) })
                        {
                            var p = hp.InverseTransformPoint(r.transform.TransformPoint(b.center + Vector3.Scale(b.extents, c)));
                            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y); minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
                        }
                        if (minX > 0.15f || maxX < -0.15f || minZ > 0.5f || maxZ < -0.5f || minY > 0.6f) continue;
                        Logger.LogInfo($"  {arg} / {r.transform.parent?.name}/{r.name}: y {minY:F3}..{maxY:F3} x {minX:F2}..{maxX:F2} z {minZ:F2}..{maxZ:F2}");
                    }
                    break;
                }
                case "hps":   // every hardpoint: forward direction and euler in the aircraft's frame
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    Transform rootT = ac != null ? ac.transform : null;
                    foreach (var t in Resources.FindObjectsOfTypeAll<Transform>().Where(x => x.gameObject.scene.IsValid() && x.name.StartsWith("hardpoint_")).OrderBy(x => x.name))
                    {
                        var r = rootT != null ? rootT : t.root;
                        var rel = Quaternion.Inverse(r.rotation) * t.rotation;
                        Logger.LogInfo($"  {t.name}: fwd {r.InverseTransformDirection(t.forward):F3} up {r.InverseTransformDirection(t.up):F3} euler {rel.eulerAngles:F2} pos {r.InverseTransformPoint(t.position):F3}");
                    }
                    break;
                }
                case "stores":   // each store's mesh under a hardpoint: pitch of its forward axis relative to the aircraft (deg, + = nose up)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    var root = ac != null ? ac.transform : null;
                    foreach (var hp in Resources.FindObjectsOfTypeAll<Transform>().Where(x => x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy && x.name.StartsWith("hardpoint_pylon")).OrderBy(x => x.name))
                        foreach (var r in hp.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled))
                        {
                            var f = (root != null ? root : hp.root).InverseTransformDirection(r.transform.forward);
                            Logger.LogInfo($"  {hp.name}/{r.transform.parent?.name}/{r.name}: pitch {Mathf.Atan2(f.y, f.z) * Mathf.Rad2Deg:F2} yaw {Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg:F2}");
                        }
                    break;
                }
                case "cmkeytest":   // cmkeytest <IR Flares|Radar Chaff>: the plugin's optional flare / chaff keys (CmKeys), driven as if the key were held
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    StartCoroutine(CmKeyTest(ac, arg));
                    break;
                }
                case "kcheck":   // the MiG-29K airframe jets in the pack: K, KUB, MiG-35UB (KCheck.cs)
                    KCheck.Run(Logger);
                    break;
                case "tvtest":   // the TV seeker screen: select the jet's Kh-29T station, then the plugin's TvSeeker must be active with a picture
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    StartCoroutine(TvTest(ac));
                    break;
                }
                case "tvpoint":   // the TV point lock: at 1,500 m, lock a bare ground spot 4 km ahead, fire the Kh-29T with no target, where it lands
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    StartCoroutine(TvPointTest(ac));
                    break;
                }
                case "probe":   // the K airframe's refuelling probe: swung out as the key would, it reaches the extended pose, then folds back
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    StartCoroutine(ProbeTest(ac));
                    break;
                }
                case "wingfold":   // the carrier jets' folding wings: folded as the key would, the outboard pieces go up and the rest stays; spread back exactly
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    StartCoroutine(WingFoldTest(ac));
                    break;
                }
                case "rearseat":   // the two-seaters' rear-seat view: the view point moves to the instructor's eye and back
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    StartCoroutine(RearSeatTest(ac));
                    break;
                }
                case "modcheck":   // the released MiG-29 / MiG-29SMT mods, installed instead of the pack (ModCheck.cs)
                    ModCheck.Run(Logger);
                    break;
                case "packcheck":   // MiG-29 Pack checks (PackCheck.cs): "[TEST] PASS/FAIL" lines and a summary
                    PackCheck.Run(Logger);
                    break;
                case "stick":   // stick <pitch> <roll> <yaw> | stick off: hold the game's inputs there (-1..1, pitch + = nose down) until "stick off"
                {
                    var a = arg.Split(' ');
                    float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                    StickHold.Held = arg == "off" ? (Vector3?)null : new Vector3(F(0), F(1), F(2));
                    break;
                }
                case "skinshadow":   // skinshadow cast|receive|both|none: which shadows the player's jet's skin casts and receives
                {
                    int count = 0;
                    foreach (var r in FindObjectsOfType<Renderer>())
                    {
                        if (!r.name.StartsWith("MiG29_skin")) continue;
                        r.shadowCastingMode = arg == "cast" || arg == "both" ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                        r.receiveShadows = arg == "receive" || arg == "both"; count++;
                    }
                    Logger.LogInfo($"  skinshadow {arg}: {count} skin renderers");
                    break;
                }
                case "sun":   // where the sun is: elevation and azimuth relative to the player's jet's nose
                {
                    var sun = FindObjectsOfType<Light>().Where(l => l.type == LightType.Directional).OrderByDescending(l => l.intensity).FirstOrDefault();
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (sun == null) { Logger.LogWarning("  no directional light"); break; }
                    var toSun = -sun.transform.forward;
                    float el = Mathf.Asin(toSun.y) * Mathf.Rad2Deg;
                    float az = ac != null ? Vector3.SignedAngle(Vector3.ProjectOnPlane(ac.transform.forward, Vector3.up), Vector3.ProjectOnPlane(toSun, Vector3.up), Vector3.up) : float.NaN;
                    Logger.LogInfo($"  sun {sun.name}: elevation {el:F1} deg, {az:F0} deg right of the nose, intensity {sun.intensity:F2}, shadows {sun.shadows}");
                    break;
                }
                case "release":   // give the stick and throttle back to the player (run.sh ends every run with it)
                    ++flightGen; StickHold.Held = null; StickHold.Throttle = null;
                    break;
                case "expect":   // expect chute|nochute: whether the MiG's brake chute is out now
                    Test((GameObject.Find("MiG29_brake_chute") != null) == (arg == "chute"), $"brake chute {(arg == "chute" ? "deployed" : "stowed")}");
                    break;
                case "say":   // say <text>: a marker line for run.sh
                    Logger.LogInfo($"[TEST] {arg}");
                    break;
                case "quit":   // quit the game (end of an unattended test run)
                    Logger.LogInfo("[TEST] quitting");
                    Application.Quit();
                    break;
                case "wait":
                    next = Time.unscaledTime + float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "runway":   // runway: put the player's jet on the nearest takeoff runway's start, facing down it, at rest
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    Airbase.Runway best = null; float bd = float.MaxValue;
                    foreach (var ab in FindObjectsOfType<Airbase>())
                        if (ab.runways != null) foreach (var rw in ab.runways)
                            if (rw != null && rw.Takeoff && rw.Start != null && rw.End != null) { float d = (rw.Start.position - ac.transform.position).sqrMagnitude; if (d < bd) { bd = d; best = rw; } }
                    if (best == null) { Logger.LogWarning("  no runway"); break; }
                    var dir = best.End.position - best.Start.position; float len = dir.magnitude; dir.y = 0f; dir.Normalize();
                    var at = best.Start.position + dir * 60f;
                    var rot = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(Vector3.ProjectOnPlane(ac.transform.forward, Vector3.up).normalized, Vector3.up));
                    var pivot = ac.transform.position;
                    // keep the jet's height over the ground (its root sits well above the wheels)
                    float Ground(Vector3 from, float range)   // distance down to the first collider that is not part of this jet
                    {
                        var hits = Physics.RaycastAll(from, Vector3.down, range, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance);
                        foreach (var h in hits) { var up = h.collider.GetComponentInParent<UnitPart>(); if (up == null || up.parentUnit != ac) return h.distance; }
                        return -1f;
                    }
                    // in the air (after a takeoff), reuse the height this jet had standing on the ground
                    float g0 = Ground(pivot + Vector3.up * 0.5f, 50f), h0 = g0 > 0f && g0 < 6f ? g0 - 0.5f : (runwayAc == ac && runwayH0 > 0f ? runwayH0 : 2f);
                    if (g0 > 0f && g0 < 6f) { runwayAc = ac; runwayH0 = h0; }
                    float g1 = Ground(at + Vector3.up * 30f, 100f); if (g1 > 0f) at = at + Vector3.up * (30f - g1);
                    at += Vector3.up * (h0 + 0.05f);
                    foreach (var rb in FindObjectsOfType<UnitPart>().Where(q => q.parentUnit == ac).Select(q => q.rb).Where(r => r != null).Distinct())
                    {
                        var np = at + rot * (rb.position - pivot);
                        rb.isKinematic = false; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                        rb.position = np; rb.rotation = rot * rb.rotation; rb.transform.SetPositionAndRotation(np, rb.rotation);
                    }
                    // the pilot and aircraft compute g from the change in velocity: stopping a flying jet must not read as a crash
                    var vbf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    var pvp = typeof(Pilot).GetField("velocityPrev", vbf);
                    foreach (var pl in ac.pilots) if (pl != null) pvp?.SetValue(pl, Vector3.zero);
                    typeof(Aircraft).GetField("velocityPrev", vbf)?.SetValue(ac, Vector3.zero);
                    StartCoroutine(Idle(++flightGen));   // stop any flight step and leave the jet at idle
                    Logger.LogInfo($"  runway: {len:F0} m long, at start + 60 m, heading {Quaternion.LookRotation(dir).eulerAngles.y:F0}");
                    break;
                }
                case "takeoff":   // takeoff <throttle 0..1> <rotate km/h> [pull 0..1, nose up; default 0.6]: full run down the runway, rotate, 10 s climb-out, logs the takeoff numbers
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(' ');
                    float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                    StartCoroutine(Takeoff(ac, F(0), F(1), a.Length > 2 ? F(2) : 0.6f));
                    break;
                }
                case "rollout":   // rollout <km/h>: on the runway, roll at that speed, then idle and full brakes; logs the stopping distance
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    StartCoroutine(Rollout(ac, float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture)));
                    break;
                }
                case "spawnac":   // spawnac <jsonKey> <count> <distance m>: AI aircraft of that type in a ring ahead of the player, same height, flying
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(' ');
                    var def = Resources.FindObjectsOfTypeAll<AircraftDefinition>().FirstOrDefault(d => d.jsonKey == a[0]);
                    if (def == null) { Logger.LogWarning("  no aircraft definition " + a[0] + "; have: " + string.Join(", ", Resources.FindObjectsOfTypeAll<AircraftDefinition>().Select(d => d.jsonKey))); break; }
                    int n = int.Parse(a[1]); float dist = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                    var prm = def.aircraftParameters;
                    var loadout = prm.StandardLoadouts != null && prm.StandardLoadouts.Length > 0 ? prm.StandardLoadouts[0].loadout : prm.loadouts[1];
                    var fwd = Vector3.ProjectOnPlane(ac.transform.forward, Vector3.up).normalized;
                    var sp = NetworkSceneSingleton<Spawner>.i;
                    for (int i = 0; i < n; i++)
                    {
                        float ang = (i - (n - 1) / 2f) * 12f;
                        var pos = ac.transform.position + Quaternion.Euler(0f, ang, 0f) * fwd * dist + Vector3.up * (40f + 15f * i);
                        sp.SpawnAircraft(null, def.unitPrefab, loadout, 0.6f, default(LiveryKey), pos.ToGlobalPosition(), Quaternion.LookRotation(fwd), fwd * 200f, null, ac.NetworkHQ, $"perf_{a[0]}_{i}", 0.5f, 0.5f);
                    }
                    Logger.LogInfo($"  spawned {n} x {a[0]} at {dist} m");
                    break;
                }
                case "camstate":   // camstate orbit|cockpit: switch the game camera as its view keys do (the cockpit's renderer swap runs)
                {
                    var cams = SceneSingleton<CameraStateManager>.i;
                    if (cams == null) { Logger.LogWarning("  no camera manager"); break; }
                    cams.SwitchState(arg == "orbit" ? (CameraBaseState)cams.orbitState : cams.cockpitState);
                    Logger.LogInfo($"  camera: {arg}");
                    break;
                }
                case "partshots": case "partbreak":   // break-every-part pictures (PartShots.cs)
                    PartShots.Command(this, Logger, verb, arg);
                    break;
                case "hld":   // every HighLiftDevice (slats, flaps) on the player's jet: its aero part, and each moving part's transform (null = broken)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    if (ac == null) { Logger.LogWarning("  no aircraft"); break; }
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    int bad = 0;
                    foreach (var h in Resources.FindObjectsOfTypeAll<HighLiftDevice>().Where(x => x != null && x.gameObject.scene.IsValid() && (x.GetComponentInParent<Aircraft>() == ac || (x.GetComponentInParent<UnitPart>()?.parentUnit == ac))))
                    {
                        var aero = typeof(HighLiftDevice).GetField("aeroPart", bf)?.GetValue(h) as Object;
                        var mps = typeof(HighLiftDevice).GetField("movingParts", bf)?.GetValue(h) as System.Array;
                        var mpNames = new List<string>();
                        if (mps != null) foreach (var mp in mps)
                        {
                            var t = mp?.GetType().GetField("transform", bf)?.GetValue(mp) as Transform;
                            bool missing = mp == null || t == null;
                            if (missing) bad++;
                            mpNames.Add(missing ? "NULL" : t.name);
                        }
                        Logger.LogInfo($"  hld {h.name} (on {h.transform.parent?.name}, enabled {h.enabled && h.gameObject.activeInHierarchy}): aero {(aero != null ? aero.name : "null")}; moving [{string.Join(", ", mpNames)}]");
                    }
                    Test(bad == 0, $"hld: {bad} moving parts without a transform");
                    break;
                }
                case "cmpress": case "nottkey": case "nottinfo":   // countermeasures with NO Tactitools installed (NottTest.cs)
                    NottTest.Command(this, Logger, verb, arg);
                    break;
                case "foe": case "flyvec": case "shoot": case "chaffat": case "mslwatch": case "mslreport":   // chaff against real radar missiles (ChaffTest.cs)
                    ChaffTest.Command(this, Logger, verb, arg);
                    break;
                case "pylons":   // every hardpoint of the player's jet: what it carries and which pylon renderers show; "pylons check" adds PASS/FAIL lines
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    var wm = ac != null ? ac.weaponManager : null;   // in flight the parts are their own roots, not under the aircraft
                    if (wm == null) { Logger.LogWarning("  no weapon manager"); break; }
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    var fSpawned = typeof(Hardpoint).GetField("spawnedPrefab", bf); var fOpts = typeof(Hardpoint).GetField("pylonOptions", bf);
                    foreach (var set in wm.hardpointSets)
                        foreach (var hp in set.hardpoints)
                        {
                            if (hp == null || hp.transform == null) continue;
                            var spawned = fSpawned.GetValue(hp) as GameObject;
                            bool loaded = spawned != null;
                            var rends = ((System.Array)fOpts.GetValue(hp) ?? new object[0]).Cast<object>().Select(o => o?.GetType().GetField("renderer", bf)?.GetValue(o) as Renderer).Where(r => r != null).ToList();
                            Logger.LogInfo($"  {set.name} / {hp.transform.name}: {(loaded ? "loaded " + spawned.name : "empty")}; pylons {string.Join(", ", rends.Select(r => $"{r.name}={(r.enabled ? "on" : "off")}"))}");
                            if (arg == "check" && rends.Count > 0)
                                Test(rends[0].enabled == loaded, $"{hp.transform.name}: pylon {(loaded ? "shown with a store" : "hidden when empty")} (enabled {rends[0].enabled})");
                        }
                    break;
                }
                case "unload":   // unload <hardpoint set name>: the game's own RemoveMounts on that station of the player's jet (it empties)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    var set = ac != null && ac.weaponManager != null ? ac.weaponManager.hardpointSets.FirstOrDefault(x => x.name == arg) : null;
                    if (set == null) { Logger.LogWarning("  no station " + arg); break; }
                    set.RemoveMounts();
                    Logger.LogInfo("  unloaded " + arg);
                    break;
                }
                case "radarinfo":   // the player's radar settings
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    var radar = ac != null ? ac.radar as Radar : null;
                    if (radar == null) { Logger.LogWarning("  no radar"); break; }
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    var p = radar.RadarParameters;
                    Logger.LogInfo($"  radar: range {p.maxRange / 1000f:0} km, cone {typeof(Radar).GetField("radarCone", bf)?.GetValue(radar)}, clutter {p.clutterFactor}, doppler {p.dopplerFactor}, min {p.minSignal}, max {p.maxSignal}, jam tolerance {typeof(Radar).GetField("jamTolerance", bf)?.GetValue(radar)}");
                    break;
                }
                case "cobra":   // cobra [seconds]: start the MiG plugins' Cobra as its key does (assist off, the CobraSwitch's window open)
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    // the plugins' own objects are hidden from FindObjectsOfType; with both mods installed, the flown jet's plugin
                    string ns = ac != null && ac.definition != null && ac.definition.jsonKey.StartsWith("mig29smt") ? "MiG29SMTInstruments" : "MiG29Instruments";
                    var cs = Resources.FindObjectsOfTypeAll<MonoBehaviour>().FirstOrDefault(m => m != null && m.GetType().Name == "CobraSwitch" && m.GetType().Namespace == ns);
                    if (ac == null || cs == null) { Logger.LogWarning($"  cobra: aircraft {ac != null}, CobraSwitch {cs != null}"); break; }
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    float secs = arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 3f;
                    ac.SetFlightAssist(false);
                    var t = cs.GetType();
                    t.GetField("active", bf).SetValue(cs, ac);
                    t.GetField("started", bf).SetValue(cs, Time.time);
                    t.GetField("peak", bf).SetValue(cs, 0f);
                    t.GetField("until", bf).SetValue(cs, Time.time + secs);
                    Logger.LogInfo($"  cobra: assist off for up to {secs} s ({cs.GetType().FullName})");
                    break;
                }
                case "level":   // level <pitch deg> <roll deg> [roll sign] [roll gain] [roll bias] | level off: hold that attitude with the stick
                {                // (a damping autopilot for tests; it takes over from the takeoff's cruise)
                    var a = arg.Split(' ');
                    float F(int i, float d) => a.Length > i ? float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture) : d;
                    flightGen++;
                    StickHold.Held = null;
                    StickHold.Level = arg == "off" ? (Vector2?)null : new Vector2(F(0, 0f), F(1, 0f));
                    StickHold.RollSign = F(2, StickHold.RollSign); StickHold.RollGain = F(3, 1f); StickHold.RollBias = F(4, 0f);
                    if (arg != "off") StickHold.Throttle = StickHold.Throttle ?? 0.85f;
                    break;
                }
                case "throttle":   // throttle <0..1>: hold the player's throttle there (1 = full afterburner)
                    StickHold.Throttle = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "assist":   // assist true|false: check the player's flight assist
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    Test(ac != null && ac.flightAssist == bool.Parse(arg), $"flight assist {arg} (is {ac?.flightAssist})");
                    break;
                }
                case "caminfo":   // every enabled camera: position in the player's aircraft frame, near/far clip, FOV, depth, layers drawn
                {
                    var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
                    foreach (var c in Camera.allCameras)
                    {
                        var p = ac != null ? ac.transform.InverseTransformPoint(c.transform.position) : c.transform.position;
                        Logger.LogInfo($"  cam {c.name}: at {p:F2} near {c.nearClipPlane:F3} far {c.farClipPlane:F0} fov {c.fieldOfView:F1} depth {c.depth} clear {c.clearFlags} mask 0x{c.cullingMask:X8} target {(c.targetTexture != null ? c.targetTexture.name : "screen")}");
                    }
                    break;
                }
                case "shot":
                    ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, arg));
                    break;
            }
        }

        static string Fmt(object v) => v is System.Collections.IEnumerable e && !(v is string) ? "[" + string.Join(", ", e.Cast<object>()) + "]" : v?.ToString() ?? "null";

        void Test(bool ok, string what)   // a check line for run.sh
        {
            if (ok) Logger.LogInfo($"[TEST] PASS {what}"); else Logger.LogWarning($"[TEST] FAIL {what}");
        }

        static Aircraft runwayAc; static float runwayH0;   // the "runway" command's last on-ground root height

        static float RollOf(Aircraft ac)   // + right wing down, -180..180
        {
            float roll = Mathf.Asin(Mathf.Clamp(-ac.transform.right.y, -1f, 1f)) * Mathf.Rad2Deg;
            return ac.transform.up.y < 0f ? (roll >= 0f ? 180f : -180f) - roll : roll;
        }
        static float Pitch(Aircraft ac) { var f = ac.transform.forward; return Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg; }

        System.Collections.IEnumerator Takeoff(Aircraft ac, float throttle, float rotateKmh, float pitchIn)
        {
            int gen = ++flightGen;
            var inp = ac.GetInputs(); var start = ac.GlobalPosition(); float t0 = Time.time, p0 = Pitch(ac);
            bool rotating = false, noseUp = false, liftedOff = false; float alt0 = ac.radarAlt, nextTrace = 0f;
            var track = Vector3.ProjectOnPlane(ac.transform.forward, Vector3.up).normalized;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var parts = FindObjectsOfType<UnitPart>().Where(q => q.parentUnit == ac).ToList();
            int Intact() => parts.Count(q => q != null && q.GetType().GetField("joints", bf)?.GetValue(q) is System.Array js
                                            && js.Cast<object>().All(j => j.GetType().GetField("joint", bf)?.GetValue(j) as Joint != null));
            int intact0 = Intact();
            void Trace(string why)   // one line of the run: along/across the runway axis, heading off the axis, attitude, height, inputs, parts
            {
                var d = ac.GlobalPosition() - start; var f = Vector3.ProjectOnPlane(ac.transform.forward, Vector3.up).normalized;
                Logger.LogInfo($"  {why} t {Time.time - t0:F1} s: {ac.speed * 3.6f:F0} km/h, along {Vector3.Dot(d, track):F0} m, across {Vector3.Cross(track, d).y:F1} m, "
                             + $"heading off {Vector3.SignedAngle(track, f, Vector3.up):F1}, pitch {Pitch(ac) - p0:F1}, roll {Vector3.SignedAngle(Vector3.ProjectOnPlane(ac.transform.up, ac.transform.forward), Vector3.ProjectOnPlane(Vector3.up, ac.transform.forward), ac.transform.forward):F1}, "
                             + $"radar alt {ac.radarAlt:F2}, pitch in {(inp != null ? inp.pitch : 0f):F2}, parts intact {Intact()}/{intact0}, gear {ac.gearState}");
            }
            Logger.LogInfo($"  takeoff: throttle {throttle}, rotate at {rotateKmh} km/h; nose attitude at rest {p0:F2} deg, radar alt {alt0:F2}");
            while (Time.time - t0 < 90f)
            {
                yield return null;
                if (inp == null) inp = ac.GetInputs();
                if (ac == null || ac.disabled) { StickHold.Held = null; StickHold.Throttle = null; Test(false, $"takeoff: the aircraft was destroyed at {Time.time - t0:F1} s"); yield break; }
                if (inp == null) continue;
                if (Time.time >= nextTrace) { nextTrace = Time.time + 1f; Trace("roll"); }
                float kmh = ac.speed * 3.6f, dist = Vector3.ProjectOnPlane(ac.GlobalPosition() - start, Vector3.up).magnitude;
                StickHold.Throttle = throttle; inp.brake = 0f;
                if (!rotating && kmh >= rotateKmh) { rotating = true; Logger.LogInfo($"  rotate: {kmh:F0} km/h at {dist:F0} m, {Time.time - t0:F1} s"); }
                // rotate to 10 deg, easing off over the last 4 (the game's pitch input is + nose down)
                StickHold.Held = new Vector3(rotating ? -pitchIn * Mathf.Clamp01((10f - (Pitch(ac) - p0)) / 4f) : 0f, 0f, 0f);
                if (rotating && !noseUp && Pitch(ac) - p0 > 2f) { noseUp = true; Logger.LogInfo($"  nose up 2 deg: {kmh:F0} km/h at {dist:F0} m"); }
                if (ac.radarAlt - alt0 > 1.5f)
                {
                    liftedOff = true; Trace("lift-off");
                    Test(Pitch(ac) - p0 > 0f && Intact() == intact0, $"takeoff: lift-off at {kmh:F0} km/h after {dist:F0} m, {Time.time - t0:F1} s, nose up {Pitch(ac) - p0:F1} deg");
                    break;
                }
            }
            if (!liftedOff) { StickHold.Held = null; StickHold.Throttle = null; Test(false, $"takeoff: no lift-off in {Time.time - t0:F0} s"); yield break; }
            // climb out: hold 10 deg nose up, gear up at 50 m, for 10 s, then level the inputs and leave the jet flying
            float t1 = Time.time; bool gearUp = false;
            while (Time.time - t1 < 10f && ac != null && !ac.disabled && inp != null)
            {
                yield return null;
                // proportional on the pitch error, damped by the pitch rate (nose up +): without the damping a hitch could set it
                // swinging nose up and down at full input and the climb-out fell short
                float rate = ac.rb != null ? -Vector3.Dot(ac.rb.angularVelocity, ac.transform.right) * Mathf.Rad2Deg : 0f;
                StickHold.Held = new Vector3(-Mathf.Clamp((10f - Pitch(ac)) * 0.12f - rate * 0.04f, -0.6f, 0.6f), 0f, 0f);
                if (!gearUp && ac.radarAlt > 30f) { gearUp = true; ac.SetGear(false); }
                if (Time.time >= nextTrace) { nextTrace = Time.time + 1f; Trace("climb"); }
            }
            if (ac == null || ac.disabled) { StickHold.Held = null; StickHold.Throttle = null; Test(false, "climb-out: the aircraft was destroyed"); }
            else
            {
                Trace("done"); Test(ac.radarAlt > 60f && Intact() == intact0, $"climb-out: {ac.radarAlt:F0} m up at {ac.speed * 3.6f:F0} km/h after 10 s, gear {ac.gearState}");
                StartCoroutine(Cruise(ac, gen));
            }
        }

        // Stick back to the player, throttle to idle (held for a moment so the player's own throttle is left there too).
        System.Collections.IEnumerator Idle(int gen)
        {
            StickHold.Held = null; StickHold.Throttle = 0f;
            yield return new WaitForSeconds(0.5f);
            if (gen == flightGen) StickHold.Throttle = null;
        }

        // Between test steps the jet flies on: wings as they are, the nose 5 deg up, 85 % throttle, until the next flight step starts.
        int flightGen;
        System.Collections.IEnumerator Cruise(Aircraft ac, int gen)
        {
            StickHold.Throttle = 0.85f;
            while (gen == flightGen && ac != null && !ac.disabled)
            {
                // damped by the pitch rate, as the climb-out: proportional alone set the lighter MiG parts (2026-11-11) swinging into the ground
                float rate = ac.rb != null ? -Vector3.Dot(ac.rb.angularVelocity, ac.transform.right) * Mathf.Rad2Deg : 0f;
                // the stick's authority grows with dynamic pressure: gains tuned at 220 m/s, scaled down for faster flight (2026-11-18)
                float v = ac.rb != null ? ac.rb.velocity.magnitude : 220f;
                float k = Mathf.Clamp((220f / Mathf.Max(v, 1f)) * (220f / Mathf.Max(v, 1f)), 0.15f, 1f);
                float target = v > 260f ? 1f : 5f;
                StickHold.Held = new Vector3(-Mathf.Clamp(k * ((target - Pitch(ac)) * 0.1f - rate * 0.04f), -0.5f, 0.5f), 0f, 0f);
                yield return null;
            }
            if (gen == flightGen) { StickHold.Held = null; StickHold.Throttle = null; }
        }

        void Render(Vector3 pos, Vector3 at, Vector3 up, float fov, string file)
        {
            var go3 = new GameObject("devcam"); var cam = go3.AddComponent<Camera>();
            cam.transform.position = pos; cam.transform.LookAt(at, up);
            cam.fieldOfView = fov; cam.nearClipPlane = 0.05f; cam.farClipPlane = 500f;
            var main = Camera.main; if (main != null) cam.cullingMask = main.cullingMask;
            var rt = new RenderTexture(1600, 1000, 24); cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt; var tex = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, file), tex.EncodeToPNG());
            Destroy(tex); rt.Release(); Destroy(rt); Destroy(go3);
        }

        // the open Workshop upload panel's page 3 (UploadWorkshopPanel.page3), or null
        static object UploadPage3()
        {
            var type = HarmonyLib.AccessTools.TypeByName("UploadWorkshopPanel");
            var panel = type == null ? null : FindObjectsOfType(type).FirstOrDefault(p => ((Component)p).gameObject.activeInHierarchy);
            return panel == null ? null : HarmonyLib.Traverse.Create(panel).Field("page3").GetValue();
        }

        System.Collections.IEnumerator LiveryShots(TMPro.TMP_Dropdown dd, Transform obj, string prefix)
        {
            for (int i = 0; i < dd.options.Count; i++)
            {
                dd.value = i; dd.RefreshShownValue();
                yield return new WaitForSecondsRealtime(2f);   // the preview repaints
                var unit = obj.GetComponentInParent<Unit>();   // any object of the jet: the jet itself (the preview may hang under a stand)
                var root = unit != null ? unit.transform : obj.root;
                var c = root.position + root.up * 1.5f; var f = root.forward; var u = root.up; var right = root.right;   // the jet's origin, mid-airframe
                string name = new string(dd.options[i].text.Where(char.IsLetterOrDigit).ToArray());
                Render(c + u * 30f, c, f, 45f, $"{prefix}_{i}_{name}_top.png");
                Render(c + right * 17f + u * 3f, c, u, 50f, $"{prefix}_{i}_{name}_side.png");
                Render(c - f * 13f + u * 6f + right * 7f, c - f * 5f + u * 1.5f, u, 40f, $"{prefix}_{i}_{name}_fins.png");
                Logger.LogInfo($"  liveryshots {i}: {dd.options[i].text}");
            }
            Logger.LogInfo($"[TEST] liveryshots: {dd.options.Count} liveries");
        }

        System.Collections.IEnumerator CmKeyTest(Aircraft ac, string station)
        {
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var type = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetTypes().FirstOrDefault(t => t.Name == "CmKeys")).FirstOrDefault(t => t != null);
            var comp = type != null ? Resources.FindObjectsOfTypeAll(type).FirstOrDefault() : null;   // the plugin hides its scanner object (HideAndDontSave), which FindObjectOfType skips
            if (comp == null) { Test(false, "cmkeytest: the plugin's CmKeys component is not running"); yield break; }
            var cm = typeof(Aircraft).GetFields(bf).FirstOrDefault(f => f.FieldType == typeof(CountermeasureManager))?.GetValue(ac) as CountermeasureManager;
            var stations = typeof(CountermeasureManager).GetField("countermeasureStations", bf)?.GetValue(cm) as System.Collections.IList;
            int idx = -1;
            for (int i = 0; stations != null && i < stations.Count; i++) if ((string)stations[i].GetType().GetField("displayName").GetValue(stations[i]) == station) idx = i;
            if (idx < 0) { Test(false, $"cmkeytest: no {station} station"); yield break; }
            int Ammo() => (int)stations[idx].GetType().GetField("ammo").GetValue(stations[idx]);
            bool Trigger() => (bool)typeof(Aircraft).GetField("countermeasureTrigger", bf).GetValue(ac);
            byte before = cm.activeIndex; int ammo0 = Ammo();
            // the component would see no key held and let go at once: it stands aside and this plays its part (the key reading is
            // BepInEx's KeyboardShortcut.IsPressed, as the plugin's other keys)
            ((Behaviour)comp).enabled = false;
            // what CmKeys.Update does when the key goes down
            type.GetField("selected", bf).SetValue(comp, before);
            type.GetField("Driving", bf).SetValue(null, ac);
            ac.Countermeasures(true, (byte)idx);
            yield return new WaitForSeconds(1.5f);
            Test(Trigger() && cm.activeIndex == idx, $"cmkeytest {station}: trigger held on its station for 1.5 s (the game's controls did not switch it off)");
            Test(Ammo() < ammo0, $"cmkeytest {station}: {ammo0 - Ammo()} fired ({ammo0} -> {Ammo()})");
            type.GetMethod("Release", bf).Invoke(comp, null);   // the key comes up
            yield return new WaitForSeconds(0.5f);
            Test(!Trigger() && cm.activeIndex == before, $"cmkeytest {station}: released, trigger off, the selected station ({before}) back");
            ((Behaviour)comp).enabled = true;
        }

        static System.Type PluginType(string name) =>
            System.AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetTypes().FirstOrDefault(t => t.Name == name && t.Namespace == "MiG29Pack"); } catch { return null; } }).FirstOrDefault(t => t != null);

        System.Collections.IEnumerator TvTest(Aircraft ac)
        {
            var type = PluginType("TvSeeker");
            if (type == null) { Test(false, "tvtest: the plugin's TvSeeker is not loaded"); yield break; }
            var st = ac.weaponStations.FirstOrDefault(w => w?.WeaponInfo != null && (w.WeaponInfo.weaponName == "Kh-29T" || w.WeaponInfo.shortName == "Kh-29T"));
            if (st == null) { Test(false, "tvtest: no Kh-29T station (load the TV Strike preset)"); yield break; }
            var wm = ac.weaponManager;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var prev = wm.currentWeaponStation;
            typeof(WeaponManager).GetField("currentWeaponStation", bf)?.SetValue(wm, st);
            yield return new WaitForSeconds(1.5f);
            var sbf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
            bool active = (bool)type.GetField("Active", sbf).GetValue(null);
            var frame = (byte[])type.GetField("Frame", sbf).GetValue(null);
            int lo = frame.Min(b => (int)b), hi = frame.Max(b => (int)b);
            float fov = (float)type.GetField("FovDeg", sbf).GetValue(null);
            Test(active, "tvtest: the TV seeker is active with the Kh-29T selected");
            Test(hi - lo > 10, $"tvtest: the TV picture is not blank (grey levels {lo}..{hi})");
            Test(Mathf.Abs(fov - 12f) < 0.1f || Mathf.Abs(fov - 3f) < 0.1f, $"tvtest: field of view {fov} deg");
            // the IR looks (normally picked after dark, or with the mode key): still running, with a picture
            var comp = Resources.FindObjectsOfTypeAll(type).FirstOrDefault();
            var modeField = type.GetField("Mode", sbf);
            if (comp != null && modeField != null)
            {
                type.GetField("modeChosen", bf)?.SetValue(comp, true);
                foreach (var look in new[] { "IrWhite", "IrBlack" })
                {
                    modeField.SetValue(null, System.Enum.Parse(modeField.FieldType, look));
                    yield return new WaitForSeconds(1.0f);
                    frame = (byte[])type.GetField("Frame", sbf).GetValue(null);
                    lo = frame.Min(b => (int)b); hi = frame.Max(b => (int)b);
                    Test((bool)type.GetField("Active", sbf).GetValue(null) && hi - lo > 10, $"tvtest: the {look} picture is running (grey levels {lo}..{hi})");
                }
                type.GetField("modeChosen", bf)?.SetValue(comp, false);
            }
            else Test(false, "tvtest: the TV seeker's mode is not reachable");
            typeof(WeaponManager).GetField("currentWeaponStation", bf)?.SetValue(wm, prev);
            yield return new WaitForSeconds(0.5f);
            Test(!(bool)type.GetField("Active", sbf).GetValue(null), "tvtest: the TV seeker is off with another station selected");
        }

        readonly HashSet<int> pickedDropdowns = new HashSet<int>();

        // Fires each named weapon from level flight at 1,500 m: the station's ammo must drop, and the jet must survive the release
        // (a store spawning inside its rack, or a bomb going off under the jet, would show here).
        System.Collections.IEnumerator FireTest(Aircraft ac, List<string> names)
        {
            int gen = ++flightGen; StickHold.Held = Vector3.zero;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var fwd = ac.transform.forward; fwd.y = 0f; fwd.Normalize();
            List<Rigidbody> Bodies() => FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).Select(p => p.rb).Where(r => r != null).Distinct().ToList();
            foreach (var rb in Bodies()) { rb.isKinematic = false; rb.transform.position += Vector3.up * 1500f; rb.position = rb.transform.position; rb.velocity = fwd * 220f; rb.angularVelocity = Vector3.zero; }
            foreach (var pl in ac.pilots) if (pl != null) typeof(Pilot).GetField("velocityPrev", bf)?.SetValue(pl, fwd * 220f);
            typeof(Aircraft).GetField("velocityPrev", bf)?.SetValue(ac, fwd * 220f);
            StartCoroutine(Cruise(ac, gen));
            for (int i = 0; i < 50; i++) { yield return new WaitForFixedUpdate(); if (ac == null || ac.disabled) { Test(false, "firetest: the jet survives the lift"); yield break; } }
            // gear up first: stores with gear safety will not release with the gear down
            ac.SetGear(false);
            for (float t = 0f; t < 10f && ac != null && !ac.disabled && ac.gearState.ToString() != "LockedRetracted"; t += 0.5f) yield return new WaitForSeconds(0.5f);
            Logger.LogInfo($"  firetest: gear {ac.gearState}");
            foreach (var name in names)
            {
                var st = ac.weaponStations.FirstOrDefault(w => w?.WeaponInfo != null && w.Ammo > 0 && (w.WeaponInfo.weaponName.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0
                                                                                                     || w.WeaponInfo.shortName.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0));
                if (st == null) { Test(false, $"firetest {name}: no loaded station (stations: {string.Join(", ", ac.weaponStations.Where(w => w?.WeaponInfo != null).Select(w => w.WeaponInfo.weaponName + " x" + w.Ammo))})"); continue; }
                typeof(WeaponManager).GetField("currentWeaponStation", bf)?.SetValue(ac.weaponManager, st);
                yield return null;
                var existing = new HashSet<Missile>(FindObjectsOfType<Missile>());
                int before = st.Ammo; string error = null;
                if (st.WeaponInfo.gun)
                    for (float t = 0f; t < 0.6f && error == null; t += Time.deltaTime) { try { ac.weaponManager.Fire(); } catch (System.Exception e) { error = e.GetType().Name + " " + e.Message; } yield return null; }
                else { try { ac.weaponManager.Fire(); } catch (System.Exception e) { error = e.GetType().Name + " " + e.Message; } }
                yield return new WaitForSeconds(1.5f);
                var fresh = FindObjectsOfType<Missile>().Where(m => !existing.Contains(m)).ToList();
                Test(error == null && st.Ammo < before, $"firetest {st.WeaponInfo.weaponName}: ammo {before} -> {st.Ammo}, {fresh.Count} new in flight{(error != null ? ", " + error : "")}");
                foreach (var m in fresh.Take(3))
                    Logger.LogInfo($"    {m.name}: {(m.transform.position - ac.transform.position).magnitude:F0} m from the jet, {(m.GetComponent<Rigidbody>() is Rigidbody mrb ? mrb.velocity.magnitude : 0f):F0} m/s");
                yield return new WaitForSeconds(2.5f);
                Test(ac != null && !ac.disabled, $"firetest {name}: the jet is fine after the release");
                if (ac == null || ac.disabled) yield break;
            }
            Logger.LogInfo("  firetest done");
        }

        System.Collections.IEnumerator TvPointTest(Aircraft ac)
        {
            var type = PluginType("TvSeeker");
            var comp = type != null ? Resources.FindObjectsOfTypeAll(type).FirstOrDefault() : null;
            if (comp == null) { Test(false, "tvpoint: the plugin's TvSeeker is not running"); yield break; }
            var st = ac.weaponStations.FirstOrDefault(w => w?.WeaponInfo != null && w.WeaponInfo.weaponName == "Kh-29T" && w.Ammo > 0);
            if (st == null) { Test(false, "tvpoint: no loaded Kh-29T station (load the TV Strike preset)"); yield break; }
            int gen = ++flightGen; StickHold.Held = Vector3.zero;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var sbf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
            // level at 1,500 m and 220 m/s, as the g-test does
            var fwd = ac.transform.forward; fwd.y = 0f; fwd.Normalize();
            List<Rigidbody> Bodies() => FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).Select(p => p.rb).Where(r => r != null).Distinct().ToList();
            foreach (var rb in Bodies()) { rb.isKinematic = false; rb.transform.position += Vector3.up * 1500f; rb.position = rb.transform.position; rb.velocity = fwd * 220f; rb.angularVelocity = Vector3.zero; }
            foreach (var pl in ac.pilots) if (pl != null) typeof(Pilot).GetField("velocityPrev", bf)?.SetValue(pl, fwd * 220f);
            typeof(Aircraft).GetField("velocityPrev", bf)?.SetValue(ac, fwd * 220f);
            for (int i = 0; i < 50; i++) { yield return new WaitForFixedUpdate(); if (ac == null || ac.disabled) { Test(false, "tvpoint: the jet survives the lift"); yield break; } }
            // a bare spot on the ground, 20 deg below the horizon ahead
            var dir = (fwd * Mathf.Cos(20f * Mathf.Deg2Rad) + Vector3.down * Mathf.Sin(20f * Mathf.Deg2Rad)).normalized;
            if (!Physics.Raycast(ac.transform.position + dir * 20f, dir, out var hit, 15000f, ~0, QueryTriggerInteraction.Ignore))
            { Test(false, "tvpoint: no ground 20 deg below the nose"); yield break; }
            if (hit.collider.GetComponentInParent<Unit>() != null) Logger.LogWarning($"  tvpoint: the spot is on a unit ({hit.collider.GetComponentInParent<Unit>().name}); testing anyway");
            var spot = hit.point.ToGlobalPosition();
            Logger.LogInfo($"  tvpoint: spot {hit.distance:F0} m away");
            // the Kh-29T selected, nothing targeted, the point locked as the lock key would
            typeof(WeaponManager).GetField("currentWeaponStation", bf)?.SetValue(ac.weaponManager, st);
            SceneSingleton<CombatHUD>.i.DeselectAll(false);
            yield return null;
            type.GetField("point", bf).SetValue(comp, spot);
            type.GetField("slewed", bf)?.SetValue(comp, false);
            yield return new WaitForSeconds(0.3f);
            Test((bool)type.GetField("PointLocked", sbf).GetValue(null), "tvpoint: the seeker holds the point lock");
            var aim = (Vector3)type.GetField("Aim", sbf).GetValue(null);
            float aimOff = (aim - spot.ToLocalPosition()).magnitude;
            Test((bool)type.GetField("AimValid", sbf).GetValue(null) && aimOff < 25f, $"tvpoint: the HUD marker is on the locked spot ({aimOff:F0} m off)");
            int before = st.Ammo;
            ac.weaponManager.Fire();
            Missile m = null;
            for (float t = 0f; t < 2f && m == null; t += Time.deltaTime) { yield return null; m = type.GetField("followed", bf).GetValue(comp) as Missile; }
            Test(m != null && st.Ammo < before, "tvpoint: the Kh-29T is fired and the screen follows it");
            if (m == null) { StartCoroutine(Cruise(ac, gen)); yield break; }
            Test(type.GetField("followedSeeker", bf).GetValue(comp) != null, "tvpoint: the plugin steers its optical seeker");
            var last = m.transform.position; float minD = float.MaxValue, t0 = Time.time;
            while (m != null && !m.disabled && m.gameObject.activeInHierarchy && Time.time - t0 < 60f)
            {
                last = m.transform.position;
                minD = Mathf.Min(minD, (last - spot.ToLocalPosition()).magnitude);
                yield return new WaitForFixedUpdate();
            }
            float miss = (last - spot.ToLocalPosition()).magnitude;
            Logger.LogInfo($"  tvpoint: weapon gone after {Time.time - t0:F1} s, {miss:F0} m from the spot (closest {minD:F0} m)");
            Test(miss < 30f || minD < 15f, $"tvpoint: the Kh-29T lands on the locked point ({miss:F0} m off, closest {minD:F0} m)");
            // steered in flight: a second Kh-29T, the slew held right for 1.5 s a few seconds after launch; it must land on the moved point
            if (st.Ammo > 0 && ac != null && !ac.disabled)
            {
                yield return new WaitForSeconds(2f);
                type.GetField("point", bf).SetValue(comp, spot);
                yield return new WaitForSeconds(0.3f);
                before = st.Ammo;
                ac.weaponManager.Fire();
                Missile m2 = null;
                for (float t = 0f; t < 2f && m2 == null; t += Time.deltaTime) { yield return null; m2 = type.GetField("followed", bf).GetValue(comp) as Missile; }
                if (m2 == null) Test(false, "tvsteer: a second Kh-29T is fired and followed");
                else
                {
                    yield return new WaitForSeconds(3f);
                    var nudge = type.GetField("SlewNudge", sbf);
                    nudge.SetValue(null, new Vector2(1f, 0f));
                    yield return new WaitForSeconds(1.5f);
                    nudge.SetValue(null, Vector2.zero);
                    var moved = (GlobalPosition?)type.GetField("point", bf).GetValue(comp);
                    float shift = moved.HasValue ? (moved.Value.ToLocalPosition() - spot.ToLocalPosition()).magnitude : 0f;
                    Logger.LogInfo($"  tvsteer: the aim point moved {shift:F0} m with the slew");
                    Test(moved.HasValue && shift > 30f, $"tvsteer: the slew keys move the aim point in flight ({shift:F0} m)");
                    var last2 = m2.transform.position; float t2 = Time.time;
                    while (m2 != null && !m2.disabled && m2.gameObject.activeInHierarchy && Time.time - t2 < 60f) { last2 = m2.transform.position; yield return new WaitForFixedUpdate(); }
                    if (moved.HasValue)
                    {
                        float miss2 = (last2 - moved.Value.ToLocalPosition()).magnitude, old2 = (last2 - spot.ToLocalPosition()).magnitude;
                        Test(miss2 < 30f && miss2 < old2, $"tvsteer: the Kh-29T lands on the steered point ({miss2:F0} m off it, {old2:F0} m from the first)");
                    }
                }
            }
            type.GetField("point", bf).SetValue(comp, null);
            if (ac != null && !ac.disabled) StartCoroutine(Cruise(ac, gen));
        }

        System.Collections.IEnumerator ProbeTest(Aircraft ac)
        {
            var type = PluginType("RefuelProbe");
            var comp = type != null ? Resources.FindObjectsOfTypeAll(type).FirstOrDefault() : null;
            if (comp == null) { Test(false, "probe: the plugin's RefuelProbe is not running"); yield break; }
            var pivot = ac.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "MiG29_probe_pivot");
            var outT = pivot != null ? pivot.parent.Find("MiG29_probe_out") : null;
            var arm = pivot != null ? pivot.Find("MiG29_probe_arm")?.GetComponent<Renderer>() : null;
            Test(pivot != null && outT != null && arm != null, "probe: the jet has the probe arm on its hinge, with its extended pose");
            if (pivot == null || outT == null || arm == null) yield break;
            yield return null;   // let the component see this aircraft
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var folded = pivot.localRotation;
            float tipOut0 = ac.transform.InverseTransformPoint(arm.bounds.center).x;
            type.GetField("target", bf).SetValue(comp, 1f);
            yield return new WaitForSeconds(3.6f);
            float off = Quaternion.Angle(pivot.localRotation, outT.localRotation);
            float tipOut1 = ac.transform.InverseTransformPoint(arm.bounds.center).x;
            Logger.LogInfo($"  probe: {off:F1} deg from the extended pose, arm centre x {tipOut0:F2} -> {tipOut1:F2} m");
            Test(off < 0.5f, $"probe: extended ({off:F1} deg off)");
            Test(tipOut1 < tipOut0 - 0.05f, $"probe: the arm swings out to the left (centre x {tipOut0:F2} -> {tipOut1:F2} m)");
            yield return new WaitForSeconds(3f);   // held out for the scenario's screenshot
            type.GetField("target", bf).SetValue(comp, 0f);
            yield return new WaitForSeconds(3.6f);
            Test(Quaternion.Angle(pivot.localRotation, folded) < 0.5f, "probe: folded back into the fairing");
        }

        System.Collections.IEnumerator WingFoldTest(Aircraft ac)
        {
            var type = PluginType("WingFold");
            var comp = type != null ? Resources.FindObjectsOfTypeAll(type).FirstOrDefault() : null;
            if (comp == null) { Test(false, "wingfold: the plugin's WingFold is not running"); yield break; }
            var all = ac.GetComponentsInChildren<Transform>(true);
            Transform Named(string n) => all.FirstOrDefault(t => t.name == n);
            if (Named("MiG29_wingfold_L") == null) { Logger.LogInfo($"[TEST] (no folding wings on {ac.definition.unitName}: skipped)"); yield break; }
            var root = ac.transform;
            var foldSkin = all.FirstOrDefault(t => t.name.StartsWith("MiG29_skin_fold_L_"))?.GetComponent<Renderer>();
            var outboard = Named("hardpoint_pylon_L4"); var outer = Named("hardpoint_pylon_L3"); var tip = Named("hardpoint_wingtip_L");
            var ail = Named("MiG29_aileron_L_pivot"); var ailIn = Named("MiG29_aileron_in_L_pivot");
            Test(foldSkin != null && outboard != null && outer != null && ail != null && ailIn != null, "wingfold: the folding skin, the outer aileron and the stations are there");
            if (foldSkin == null || outboard == null || outer == null || ail == null) yield break;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var sbf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
            while (!(bool)type.GetField("bound", bf).GetValue(comp)) yield return null;
            Vector3 At(Transform t) => root.InverseTransformPoint(t.position);
            var skin0 = root.InverseTransformPoint(foldSkin.bounds.center); var ob0 = At(outboard); var ou0 = At(outer); var ail0 = At(ail);
            var tip0 = tip != null ? At(tip) : Vector3.zero; var ailIn0 = ailIn != null ? At(ailIn) : Vector3.zero;
            type.GetField("target", bf).SetValue(comp, 1f);
            yield return new WaitForSeconds(7f);
            float pos = (float)type.GetField("Pos", sbf).GetValue(null);
            var skin1 = root.InverseTransformPoint(foldSkin.bounds.center);
            Logger.LogInfo($"  wingfold: folded {pos:F2}; outboard skin centre {skin0:F2} -> {skin1:F2}, L4 {ob0:F2} -> {At(outboard):F2}, aileron {ail0:F2} -> {At(ail):F2}");
            Test(pos >= 1f, "wingfold: folded");
            Test(skin1.y - skin0.y > 1f, $"wingfold: the outboard skin stands up ({skin0.y:F2} -> {skin1.y:F2} m)");
            Test(At(outboard).y - ob0.y > 0.4f, $"wingfold: the outboard station goes up with it ({ob0.y:F2} -> {At(outboard).y:F2} m)");
            if (tip != null) Test(At(tip).y - tip0.y > 1f, $"wingfold: the wingtip station goes up with it ({tip0.y:F2} -> {At(tip).y:F2} m)");
            Test(At(ail).y - ail0.y > 0.4f, "wingfold: the outer aileron folds");
            Test((At(outer) - ou0).magnitude < 0.001f && (ailIn == null || (At(ailIn) - ailIn0).magnitude < 0.001f), "wingfold: the outer pylon and the inner aileron stay");
            var cs = ail.GetComponent("ControlSurface") as Behaviour;
            Test(cs != null && !cs.enabled, "wingfold: the folded aileron's control surface is held");
            type.GetField("target", bf).SetValue(comp, 0f);
            yield return new WaitForSeconds(7f);
            float back = Mathf.Max((root.InverseTransformPoint(foldSkin.bounds.center) - skin0).magnitude, (At(outboard) - ob0).magnitude, (At(ail) - ail0).magnitude);
            Test((float)type.GetField("Pos", sbf).GetValue(null) <= 0f && back < 0.001f, $"wingfold: spread exactly back ({back * 1000f:F2} mm)");
            Test(cs != null && cs.enabled, "wingfold: the aileron works again");
        }

        System.Collections.IEnumerator RearSeatTest(Aircraft ac)
        {
            var type = PluginType("RearSeat");
            var comp = type != null ? Resources.FindObjectsOfTypeAll(type).FirstOrDefault() : null;
            if (comp == null) { Test(false, "rearseat: the plugin's RearSeat is not running"); yield break; }
            var vp = ac.cockpitViewPoint;
            if (vp == null) { Test(false, "rearseat: the aircraft has no cockpitViewPoint"); yield break; }
            yield return null;   // let the component see this aircraft
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var front = ac.transform.InverseTransformPoint(vp.position);
            type.GetMethod("ToRear", bf).Invoke(comp, null);
            var rear = ac.transform.InverseTransformPoint(vp.position);
            var d = rear - front;
            Test(Mathf.Abs(d.z + 1.10f) < 0.05f && Mathf.Abs(d.y - 0.20f) < 0.05f, $"rearseat: the view moved to the instructor's seat ({d.x:F2}, {d.y:F2}, {d.z:F2})");
            // the instructor's mirrors (on the moving canopy) and the periscope's eyepiece, live while in the rear seat
            var all = ac.GetComponentsInChildren<Transform>(true);
            var mirrors = all.Where(t => t.name.StartsWith("NOMirror_r")).ToList();
            var eyepiece = all.FirstOrDefault(t => t.name == "MiG29_periscope_eyepiece")?.GetComponent<Renderer>();
            Test(mirrors.Count == 2 && mirrors.All(t => t.GetComponentsInParent<Transform>().Any(x => x.name == "MiG29_canopyHinge")),
                $"rearseat: two rear mirrors on the moving canopy ({string.Join(", ", mirrors.Select(t => t.name))})");
            yield return new WaitForSeconds(1f);
            Test(eyepiece != null && eyepiece.sharedMaterial != null && eyepiece.sharedMaterial.name.StartsWith("MiG29_periscope") && eyepiece.sharedMaterial.GetTexture("_BaseMap") is RenderTexture,
                $"rearseat: the periscope shows its camera in the rear seat ({eyepiece?.sharedMaterial?.name})");
            type.GetMethod("Restore", bf).Invoke(comp, null);
            var back = ac.transform.InverseTransformPoint(vp.position);
            Test((back - front).magnitude < 0.01f, "rearseat: the view is back in the front seat");
            yield return null; yield return null;
            Test(eyepiece != null && !(eyepiece.sharedMaterial?.GetTexture("_BaseMap") is RenderTexture), "rearseat: the periscope's glass is dark again out of the rear seat");
        }

        System.Collections.IEnumerator Rollout(Aircraft ac, float kmh)
        {
            var bodies = FindObjectsOfType<UnitPart>().Where(q => q.parentUnit == ac).Select(q => q.rb).Where(r => r != null).Distinct().ToList();
            var fwd = Vector3.ProjectOnPlane(ac.transform.forward, Vector3.up).normalized;
            foreach (var rb in bodies) rb.velocity = fwd * kmh / 3.6f;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            typeof(Aircraft).GetField("velocityPrev", bf)?.SetValue(ac, fwd * kmh / 3.6f);
            var pvp = typeof(Pilot).GetField("velocityPrev", bf);
            foreach (var pl in ac.pilots) if (pl != null) pvp?.SetValue(pl, fwd * kmh / 3.6f);
            ++flightGen;
            var inp = ac.GetInputs(); var start = ac.GlobalPosition(); float t0 = Time.time, minPitch = 0f, p0 = Pitch(ac);
            while (Time.time - t0 < 60f)
            {
                yield return null;
                if (inp == null) inp = ac.GetInputs();
                StickHold.Held = Vector3.zero; StickHold.Throttle = 0f;
                if (inp != null) inp.brake = 1f;
                minPitch = Mathf.Min(minPitch, Pitch(ac) - p0);
                if (ac.speed < 1f && Time.time - t0 > 1f) break;
            }
            float dist = Vector3.ProjectOnPlane(ac.GlobalPosition() - start, Vector3.up).magnitude;
            Logger.LogInfo($"  rollout from {kmh} km/h: stopped in {dist:F0} m, {Time.time - t0:F1} s; nose dipped {-minPitch:F2} deg under braking");
            Test(ac != null && !ac.disabled && ac.speed < 1f, $"rollout from {kmh} km/h: stopped in {dist:F0} m, {Time.time - t0:F1} s");
            if (inp != null) inp.brake = 0f;
            StickHold.Held = null; StickHold.Throttle = null;
        }
System.Collections.IEnumerator BlastTest(Aircraft ac, float yield, Vector3 at)
        {
            ++flightGen; StickHold.Held = Vector3.zero;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var fwd = ac.transform.forward; fwd.y = 0f; fwd.Normalize();
            var all = FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).OrderBy(p => p.name).ToList();
            foreach (var rb in all.Select(p => p.rb).Where(r => r != null).Distinct()) { rb.isKinematic = false; rb.transform.position += Vector3.up * 1500f; rb.position = rb.transform.position; rb.velocity = fwd * 220f; rb.angularVelocity = Vector3.zero; }
            var vp = typeof(Pilot).GetField("velocityPrev", bf);
            foreach (var pl in ac.pilots) if (pl != null) vp?.SetValue(pl, fwd * 220f);
            typeof(Aircraft).GetField("velocityPrev", bf)?.SetValue(ac, fwd * 220f);
            for (int i = 0; i < 50; i++) yield return new WaitForFixedUpdate();
            // every joint of every part: its Unity joint, what it holds on to and what it lets go with
            var rows = new List<(UnitPart p, int i, object pj)>();
            foreach (var p in all) if (p != null && p.GetType().GetField("joints", bf)?.GetValue(p) is System.Array js) for (int i = 0; i < js.Length; i++) rows.Add((p, i, js.GetValue(i)));
            Joint J(object pj) => pj.GetType().GetField("joint", bf)?.GetValue(pj) as Joint;
            string N(object pj, string f) => (pj.GetType().GetField(f, bf)?.GetValue(pj) as Component)?.name ?? "-";
            var alive = rows.Select(r => J(r.pj) != null).ToArray();
            float Hp(UnitPart p) => (float)(p.GetType().GetField("hitPoints", bf)?.GetValue(p) ?? 0f);
            bool Off(UnitPart p) => (bool)(typeof(UnitPart).GetField("detachedFromUnit", bf)?.GetValue(p) ?? false);
            var point = ac.transform.TransformPoint(at);
            Logger.LogInfo($"  blasttest: {ac.definition.unitName}, yield {yield}, at {at} (aircraft frame), {all.Count} parts, {rows.Count} joints ({alive.Count(x => x)} intact), {rows.Count(r => N(r.pj, "tensor") != "-")} with a tensor");
            DamageEffects.BlastFrag(yield, point, PersistentID.None, PersistentID.None);
            yield return null;
            foreach (var p in all.Where(p => p != null)) Logger.LogInfo($"    hp after blast {p.name}: {Hp(p):F0}");
            int first = -1, cascade = 0, direct = 0;
            for (int f = 0; f < 150; f++)
            {
                yield return new WaitForFixedUpdate();
                for (int k = 0; k < rows.Count; k++)
                {
                    if (!alive[k] || rows[k].p == null) continue;
                    if (J(rows[k].pj) != null) continue;
                    alive[k] = false; if (first < 0) first = f;
                    var r = rows[k]; string t = N(r.pj, "tensor");
                    Logger.LogInfo($"    frame {f}: {r.p.name} joint {r.i} to {N(r.pj, "connectedPart")} broke (tensor {t}, hp {Hp(r.p):F0})");
                    if (t != "-") cascade++; else direct++;
                }
            }
            var off = all.Where(p => p != null && Off(p)).Select(p => p.name).ToList();
            Logger.LogInfo($"  blasttest result: {off.Count} of {all.Count} parts off ({string.Join(", ", off)}); joints broken: {direct} plain, {cascade} with a tensor; first break at frame {first}; jet {(ac == null ? "destroyed" : ac.disabled ? "disabled" : "flying")}");
            Test(true, $"blasttest {yield} at {at}: {off.Count} of {all.Count} parts off");
        }

        System.Collections.IEnumerator TankTest(Aircraft ac, float drain, float cruise, float speed, bool noDrop = false, bool snap = false, float lift = 300f)
        {
            int gen = ++flightGen; StickHold.Held = Vector3.zero;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var fwd = ac.transform.forward; fwd.y = 0f; fwd.Normalize();
            List<Rigidbody> Bodies() => FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).Select(p => p.rb).Where(r => r != null).Distinct().ToList();
            foreach (var rb in Bodies()) { rb.isKinematic = false; rb.transform.position += Vector3.up * lift; rb.position = rb.transform.position; rb.velocity = fwd * speed; rb.angularVelocity = Vector3.zero; }
            foreach (var pl in ac.pilots) if (pl != null) typeof(Pilot).GetField("velocityPrev", bf)?.SetValue(pl, fwd * speed);
            typeof(Aircraft).GetField("velocityPrev", bf)?.SetValue(ac, fwd * speed);
            StartCoroutine(Cruise(ac, gen)); if (speed > 250f) StickHold.Throttle = 1f;
            for (int i = 0; i < 50; i++) { yield return new WaitForFixedUpdate(); if (ac == null || ac.disabled) { Test(false, "tanktest: the jet survives the lift"); yield break; } }
            ac.SetGear(false);
            for (float t = 0f; t < 10f && ac != null && !ac.disabled && ac.gearState.ToString() != "LockedRetracted"; t += 0.5f) yield return new WaitForSeconds(0.5f);
            // empty some internal fuel so the plugin has room to move drop-tank fuel in
            var partF = typeof(FuelTank).GetField("part", bf);
            float left = drain;
            foreach (var ft in ac.GetFuelTanks())
            {
                if (ft == null || left <= 0f) continue;
                float x = Mathf.Min(left, ft.fuelMass); ft.fuelMass -= x; left -= x;
                (partF?.GetValue(ft) as UnitPart)?.ModifyMass(-x);
            }
            Logger.LogInfo($"  tanktest: drained {drain - left:F0} kg of internal fuel");
            var tankSt = ac.weaponStations.Where(w => w?.WeaponInfo != null && w.WeaponInfo.weaponName.StartsWith("PTB")).ToList();
            var hps = new List<Hardpoint>();
            var hpF = typeof(Weapon).GetField("hardpoint", bf);
            foreach (var st in tankSt) foreach (var w in st.Weapons) if (w != null && hpF?.GetValue(w) is Hardpoint hp && !hps.Contains(hp)) hps.Add(hp);
            string Masses() => string.Join(", ", hps.Select(h => h.part == null ? "?" : $"{h.part.name} part {h.part.mass:F0} rb {(h.part.rb != null ? h.part.rb.mass.ToString("F0") : "-")}"));
            string Cg()   // whole jet: mass, centre of mass along the jet (z, + forward) and up (y), pitch, angle of attack, pitch rate
            {
                if (ac == null || ac.rb == null) return "";
                float m = 0f; Vector3 c = Vector3.zero;
                foreach (var rb in Bodies()) { m += rb.mass; c += rb.worldCenterOfMass * rb.mass; }
                var lc = ac.transform.InverseTransformPoint(c / Mathf.Max(m, 1f));
                var lv = ac.transform.InverseTransformDirection(ac.rb.velocity);
                float aoa = Mathf.Atan2(-lv.y, lv.z) * Mathf.Rad2Deg;
                float pitch = Mathf.Asin(Mathf.Clamp(ac.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
                float q = ac.transform.InverseTransformDirection(ac.rb.angularVelocity).x * Mathf.Rad2Deg;
                return $"; mass {m:F0} kg, cg z {lc.z:F2} y {lc.y:F2}, pitch {pitch:F1}, aoa {aoa:F1}, q {q:F0} deg/s";
            }
            Logger.LogInfo($"  tanktest: stations {string.Join(", ", tankSt.Select(w => w.WeaponInfo.weaponName + " x" + w.Ammo))}; pylon parts {Masses()}");
            for (float t = 0f; t < cruise && ac != null && !ac.disabled; t += 5f)
            {
                yield return new WaitForSeconds(5f);
                Logger.LogInfo($"  tanktest t+{t + 5f:F0}s: {Masses()}; internal fuel {ac.GetFuelTanks().Where(f => f != null).Sum(f => f.fuelMass):F0} kg");
            }
            var st1500 = tankSt.FirstOrDefault(w => w.WeaponInfo.weaponName.StartsWith("PTB-1500") && w.Ammo > 0) ?? tankSt.FirstOrDefault(w => w.Ammo > 0);
            if (st1500 == null) { Test(false, "tanktest: no loaded drop tank"); yield break; }
            typeof(WeaponManager).GetField("currentWeaponStation", bf)?.SetValue(ac.weaponManager, st1500);
            yield return null;
            if (speed > 250f && snap)   // drag has slowed it during the cruise: back to the asked speed along its path (upsets the trim: a jolt)
            {
                var dir = ac.rb.velocity.normalized;
                foreach (var rb in Bodies()) rb.velocity = dir * speed;
                yield return new WaitForFixedUpdate();
            }
            Logger.LogInfo($"  tanktest: {(noDrop ? "NOT dropping (control)" : "dropping " + st1500.WeaponInfo.weaponName)}, speed {ac.rb.velocity.magnitude:F0} m/s, alt {ac.transform.position.y - Datum.LocalSeaY:F0} m{Cg()}");
            var before = new HashSet<Missile>(FindObjectsOfType<Missile>());
            if (!noDrop) ac.weaponManager.Fire();
            float minMass = float.MaxValue, maxSpeed = 0f; bool bad = false; Missile tank = null; bool wasTangible = false;
            for (int i = 0; i < 150; i++)
            {
                yield return new WaitForFixedUpdate();
                if (ac == null) { bad = true; break; }
                if (tank == null) tank = FindObjectsOfType<Missile>().FirstOrDefault(m => !before.Contains(m));
                if (tank != null && !wasTangible && tank.IsTangible())
                {
                    wasTangible = true;
                    Logger.LogInfo($"    step {i}: the tank turned solid {(tank.transform.position - ac.transform.position).magnitude:F1} m from the jet ({ac.transform.InverseTransformPoint(tank.transform.position):F1} in the jet's frame)");
                }
                maxSpeed = Mathf.Max(maxSpeed, ac.rb != null ? ac.rb.velocity.magnitude : 0f);
                foreach (var h in hps) if (h.part != null && h.part.rb != null) minMass = Mathf.Min(minMass, h.part.rb.mass);
                var v = ac.rb != null ? ac.rb.velocity.magnitude : -1f;
                if (float.IsNaN(v) || v > 1000f) bad = true;
                if (i < 3 || i % 5 == 0 || v > 600f) Logger.LogInfo($"    step {i}: {Masses()}; speed {v:F0} m/s, alt {ac.transform.position.y - Datum.LocalSeaY:F0} m{Cg()}{(tank != null ? $", tank {(tank.transform.position - ac.transform.position).magnitude:F1} m away, {(tank.IsTangible() ? "solid" : "not solid")}" : "")}");
            }
            yield return new WaitForSeconds(2f);
            Test(!bad && ac != null && !ac.disabled && minMass > 0f, $"tanktest: the jet is fine after the drop (lowest pylon part mass {minMass:F0} kg, top speed {maxSpeed:F0} m/s, tank {(wasTangible ? "turned solid" : "never solid in 3 s")}, {(ac == null ? "jet gone" : ac.disabled ? "jet destroyed" : "flying")})");
        }

        System.Collections.IEnumerator GTest(Aircraft ac, float pitch)
        {
            int gen = ++flightGen; StickHold.Held = Vector3.zero;
            var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var fwd = ac.transform.forward; fwd.y = 0f; fwd.Normalize();
            List<Rigidbody> Bodies() => FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).Select(p => p.rb).Where(r => r != null).Distinct().ToList();
            foreach (var rb in Bodies()) { rb.isKinematic = false; rb.transform.position += Vector3.up * 1500f; rb.position = rb.transform.position; rb.velocity = fwd * 220f; rb.angularVelocity = Vector3.zero; }
            var vp = typeof(Pilot).GetField("velocityPrev", bf);
            foreach (var pl in ac.pilots) if (pl != null) vp?.SetValue(pl, fwd * 220f);
            typeof(Aircraft).GetField("velocityPrev", bf)?.SetValue(ac, fwd * 220f);
            for (int i = 0; i < 50; i++)   // settle at 1 g
            {
                yield return new WaitForFixedUpdate();
                if (ac == null || ac.disabled || SceneSingleton<CombatHUD>.i?.aircraft != ac)
                {
                    Logger.LogWarning($"  gtest: jet lost {i} frames after the lift: {(ac == null ? "destroyed" : $"disabled {ac.disabled}, alt {ac.radarAlt:F0}, speed {ac.speed:F0}, intact {FindObjectsOfType<UnitPart>().Count(p => p.parentUnit == ac)}")}");
                    Test(false, "g-test: the jet survives the lift to 1,500 m"); yield break;
                }
            }
            var root = ac.rb;
            var parts = FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac && p.rb != null && p.rb != root).ToList();
            var rest = parts.ToDictionary(p => p, p => root.transform.InverseTransformPoint(p.transform.position));
            // nose up about the root: rigid, so the parts keep their relative poses
            var q = Quaternion.AngleAxis(-pitch, ac.transform.right);
            var pivot = root.position;
            foreach (var rb in Bodies()) { rb.position = pivot + q * (rb.position - pivot); rb.rotation = q * rb.rotation; rb.transform.SetPositionAndRotation(rb.position, rb.rotation); }
            float maxG = 0f; var worst = parts.ToDictionary(p => p, p => 0f);
            for (int i = 0; i < 100; i++)
            {
                yield return new WaitForFixedUpdate();
                maxG = Mathf.Max(maxG, ac.gForce);
                foreach (var p in parts) if (p != null && p.rb != null) worst[p] = Mathf.Max(worst[p], (root.transform.InverseTransformPoint(p.transform.position) - rest[p]).magnitude);
            }
            Logger.LogInfo($"  gtest {pitch} deg: max g {maxG:F1}, iterations {root.solverIterations}/{root.solverVelocityIterations}");
            Test(maxG > 2f && maxG < 12f, $"g-test {pitch} deg pull: max {maxG:F1} g (2 to 12 expected)");
            if (ac != null && !ac.disabled) StartCoroutine(Cruise(ac, gen));
            foreach (var kv in worst.OrderByDescending(kv => kv.Value).Take(10)) Logger.LogInfo($"    {kv.Key.name}: {kv.Value * 100f:F1} cm");
        }

    }
}
