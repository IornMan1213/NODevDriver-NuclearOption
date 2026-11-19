using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace NODevDriver
{
    // Countermeasures with NO Tactitools (NOTT) installed (dev only). NOTT is another author's mod: these commands only press its keys
    // through its own handlers, as a player would, and read the game's state; they copy nothing from it.
    //   cmpress <station name> [seconds]   the game's own Countermeasures button held that long on that station (the call the game's
    //                                      player controls make every frame the button is down); PASS if cartridges come out
    //   nottkey <n> [seconds]              NOTT's countermeasure key n held that long (NOTT plus: its "Countermeasure Controls - n" key;
    //                                      original NOTT: 0 = its flare key, 1 = its jammer key, which only select a station)
    //   nottinfo                           which NOTT is loaded, and how many errors it has logged since the last nottinfo
    public static class NottTest
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static ManualLogSource log;
        static int errors, errorsSeen;
        static string lastError;
        static bool hooked;

        static void Pass(bool ok, string what) { if (ok) log.LogInfo($"[TEST] PASS {what}"); else log.LogWarning($"[TEST] FAIL {what}"); }

        static Aircraft Player() { var h = SceneSingleton<CombatHUD>.i; return h != null ? h.aircraft : null; }

        static IList Stations(Aircraft ac, out CountermeasureManager cm)
        {
            cm = typeof(Aircraft).GetField("countermeasureManager", BF)?.GetValue(ac) as CountermeasureManager;
            return cm != null ? typeof(CountermeasureManager).GetField("countermeasureStations", BF)?.GetValue(cm) as IList : null;
        }
        static string Name(object st) => st.GetType().GetField("displayName")?.GetValue(st) as string;
        static int Ammo(object st) => (int)st.GetType().GetField("ammo").GetValue(st);
        static bool Trigger(Aircraft ac) => (bool)typeof(Aircraft).GetField("countermeasureTrigger", BF).GetValue(ac);
        static string Summary(IList list) => string.Join(", ", list.Cast<object>().Select((s, i) => $"{i} {Name(s)} {Ammo(s)}"));

        static System.Reflection.Assembly Nott() => System.AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "NO_Tactitools");

        public static void Command(MonoBehaviour host, ManualLogSource logger, string verb, string arg)
        {
            log = logger;
            if (!hooked)
            {
                hooked = true;
                Application.logMessageReceived += (msg, trace, type) =>
                {
                    if ((type == LogType.Exception || type == LogType.Error) && (msg + trace).Contains("NO_Tactitools")) { errors++; lastError = msg; }
                };
            }
            var ac = Player();
            switch (verb)
            {
                case "nottinfo":
                {
                    var a = Nott();
                    log.LogInfo($"  nott: {(a == null ? "not loaded" : a.GetName().Version + " " + (a.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).FirstOrDefault() as AssemblyInformationalVersionAttribute)?.InformationalVersion)}");
                    log.LogInfo($"  nott errors since last check: {errors - errorsSeen}{(lastError != null ? " (last: " + lastError + ")" : "")}");
                    errorsSeen = errors;
                    if (ac != null) { var l = Stations(ac, out var cm); if (l != null) log.LogInfo($"  cm stations: {Summary(l)}; selected {cm.activeIndex}, trigger {Trigger(ac)}"); }
                    break;
                }
                case "cmpress":
                {
                    if (ac == null) { log.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(' ').ToList(); float secs = 1.5f;
                    if (a.Count > 1 && float.TryParse(a[a.Count - 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) { secs = s; a.RemoveAt(a.Count - 1); }
                    host.StartCoroutine(Press(ac, string.Join(" ", a), secs));
                    break;
                }
                case "nottkey":
                {
                    if (ac == null) { log.LogWarning("  no aircraft"); break; }
                    var a = arg.Split(' '); int n = int.Parse(a[0]);
                    float secs = a.Length > 1 ? float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) : 1.5f;
                    host.StartCoroutine(Key(ac, n, secs));
                    break;
                }
            }
        }

        static IEnumerator Press(Aircraft ac, string station, float secs)
        {
            var list = Stations(ac, out var cm);
            int idx = -1;
            for (int i = 0; list != null && i < list.Count; i++) if (Name(list[i]) == station) idx = i;
            if (idx < 0) { Pass(false, $"cmpress: no {station} station ({(list != null ? Summary(list) : "none")})"); yield break; }
            if (cm.activeIndex != idx) { cm.activeIndex = (byte)idx; cm.UpdateHUD(); }   // as the Next Countermeasure button leaves it
            int before = Ammo(list[idx]); int heldFrames = 0, frames = 0;
            for (float t = 0f; t < secs; t += Time.deltaTime)
            {
                // PilotPlayerState.PlayerControls with the button down: "if the trigger is off, Countermeasures(true, selected)"
                if (!Trigger(ac)) ac.Countermeasures(true, cm.activeIndex);
                yield return null;
                frames++; if (Trigger(ac)) heldFrames++;
            }
            if (Trigger(ac)) ac.Countermeasures(false, cm.activeIndex);   // the button comes up
            yield return new WaitForSeconds(0.3f);
            int after = Ammo(list[idx]);
            Pass(after < before, $"cmpress {station}: {before - after} fired in {secs:F1} s ({before} -> {after}; trigger on {heldFrames} of {frames} frames)");
            Pass(!Trigger(ac), $"cmpress {station}: trigger off after the button comes up");
        }

        static IEnumerator Key(Aircraft ac, int n, float secs)
        {
            var asm = Nott();
            if (asm == null) { Pass(false, "nottkey: NOTT is not loaded"); yield break; }
            var cc = asm.GetTypes().FirstOrDefault(t => t.Name == "CountermeasureControlsPlugin");
            var list = Stations(ac, out var cm);
            var ammo0 = list.Cast<object>().Select(Ammo).ToArray(); byte sel0 = cm.activeIndex;
            var press = cc?.GetMethod("HandleOnPress", BF);
            if (press != null)   // NOTT plus: named stations, press and release
            {
                var datum = asm.GetTypes().Select(t => t.GetField("CountermeasureDatum", BF)).FirstOrDefault(f => f != null && f.IsStatic)?.GetValue(null) as IList;
                if (datum == null || n >= datum.Count) { Pass(false, $"nottkey: NOTT plus has no key {n} ({(datum == null ? "no key list" : datum.Count + " keys")})"); yield break; }
                var nameVar = datum[n].GetType().GetField("Name", BF)?.GetValue(datum[n]) ?? datum[n].GetType().GetProperty("Name", BF)?.GetValue(datum[n]);
                string name = nameVar?.GetType().GetProperty("Value")?.GetValue(nameVar) as string;
                log.LogInfo($"  nottkey {n}: NOTT plus key for \"{name}\"; stations {Summary(list)}");
                press.Invoke(null, new[] { nameVar });
                for (float t = 0f; t < secs; t += Time.deltaTime) yield return null;
                bool held = Trigger(ac);
                cc.GetMethod("HandleOnReleased", BF).Invoke(null, null);
                yield return new WaitForSeconds(0.3f);
                var ammo1 = list.Cast<object>().Select(Ammo).ToArray();
                int fired = ammo0.Zip(ammo1, (x, y) => x - y).Sum();
                int want = -1; for (int i = 0; i < list.Count; i++) if (Name(list[i]) == name) want = i;
                Pass(want >= 0, $"nottkey {n}: the jet has a station named \"{name}\"");
                if (want >= 0) Pass(ammo0[want] - ammo1[want] > 0, $"nottkey {n}: {ammo0[want] - ammo1[want]} {name} fired (trigger was {(held ? "on" : "off")} while held; {fired} in all)");
                Pass(!Trigger(ac), $"nottkey {n}: trigger off after release");
            }
            else   // original NOTT: its keys only select a station
            {
                var m = cc?.GetMethod(n == 0 ? "HandleOnHoldFlare" : "HandleOnHoldJammer", BF);
                if (m == null) { Pass(false, $"nottkey: no NOTT key handler {n}"); yield break; }
                m.Invoke(null, null);
                yield return null;
                log.LogInfo($"  nottkey {n}: original NOTT {(n == 0 ? "flare" : "jammer")} key: selected {sel0} -> {cm.activeIndex} ({Name(list[cm.activeIndex])})");
                Pass(Name(list[cm.activeIndex]) == (n == 0 ? "IR Flares" : "Radar Chaff"), $"nottkey {n}: selects {(n == 0 ? "IR Flares" : "Radar Chaff")}");
            }
        }
    }
}
