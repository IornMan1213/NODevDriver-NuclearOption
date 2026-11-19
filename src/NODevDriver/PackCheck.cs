using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace NODevDriver
{
    // MiG-29 Pack checks, run by the driver's "packcheck" command (scenarios/*.txt). Every check logs one line,
    // "[TEST] PASS <what>" or "[TEST] FAIL <what>: <why>", and a summary "[TEST] packcheck: N passed, M failed" that
    // run.sh collects. What can be checked from the encyclopedia runs anywhere after the main menu has loaded; the
    // player-jet checks run only while flying a pack jet.
    static class PackCheck
    {
        static ManualLogSource log;
        static int passed, failed;

        static void Check(bool ok, string what, string why = "")
        {
            if (ok) { passed++; log.LogInfo($"[TEST] PASS {what}"); }
            else { failed++; log.LogWarning($"[TEST] FAIL {what}{(why.Length > 0 ? ": " + why : "")}"); }
        }

        class Jet { public string key, name; public float value; public int rank; public int presets; public string[] defaultHas, innerHas, innerNot; public bool glass; }

        static readonly Jet[] Jets =
        {
            new Jet { key = "mig29_Fulcrum", name = "MiG-29 Fulcrum", value = 50f, rank = 2, presets = 5, glass = false,
                      defaultHas = new[] { "mig29_R27R_single" }, innerHas = new[] { "mig29_R27R_single", "mig29_R27T_single", "mig29_R73_single", "mig29_R60M_single", "mig29_PTB1150" },
                      innerNot = new[] { "mig29smt_R77_single", "mig29smt_Kh29L_single", "mig29smt_Kh31P_single", "mig29smt_KAB500L_single" } },
            new Jet { key = "mig29smt_Fulcrum", name = "MiG-29SMT", value = 90f, rank = 3, presets = 8, glass = true,
                      defaultHas = new[] { "mig29smt_R77_single" }, innerHas = new[] { "mig29smt_R77_single", "mig29_R27R_single", "mig29smt_Kh29L_single", "mig29smt_Kh31P_single", "mig29smt_KAB500L_single", "mig29_PTB1150" },
                      innerNot = new string[0] },
        };

        static readonly string[] SharedWeapons = { "mig29_R73_single", "mig29_R73_outer", "mig29_R60M_single", "mig29_R60M_outer", "mig29_R27R_single", "mig29_R27T_single",
                                                   "mig29_PTB1500", "mig29_PTB1150", "mig29smt_R77_single", "mig29smt_Kh29L_single", "mig29smt_Kh31P_single", "mig29smt_KAB500L_single" };
        // the SMT mod's own copies of the shared weapons: the pack has them once, under the 9.12's keys
        static readonly string[] NotInPack = { "mig29smt_R73_single", "mig29smt_R27R_single", "mig29smt_R60M_single", "mig29smt_PTB1500" };
        static readonly string[] Sets = { "GSh-30-1", "Centreline", "Inner Pylons", "Middle Pylons", "Outer Pylons", "Wingtips", "Tail Hook" };
        static readonly string[] Pods = { "mig29_cmpod_flares", "mig29_cmpod_chaff", "mig29_cmpod_mixed" };
        static readonly string[] Drivers = { "StoresDriver", "NozzleDriver", "GearPathDriver", "IntakeDriver", "ChuteDriver" };

        public static void Run(ManualLogSource logger)
        {
            log = logger; passed = failed = 0;
            var guids = BepInEx.Bootstrap.Chainloader.PluginInfos.Keys.ToList();
            Check(guids.Contains("iornman.mig29pack"), "MiG29Pack.dll loaded");
            Check(!guids.Contains("iornman.mig29.instruments") && !guids.Contains("iornman.mig29smt.instruments"), "the old MiG-29 / SMT plugins are not loaded");

            var lookup = Encyclopedia.Lookup; var weapons = Encyclopedia.WeaponLookup;
            Check(lookup != null && weapons != null, "encyclopedia loaded");
            if (lookup == null || weapons == null) { Summary(); return; }
            foreach (var key in SharedWeapons) Check(weapons.ContainsKey(key), $"weapon {key} registered");
            foreach (var key in NotInPack) Check(!weapons.ContainsKey(key), $"no SMT-mod copy {key}");

            foreach (var j in Jets)
            {
                var def = lookup.TryGetValue(j.key, out var u) ? u as AircraftDefinition : null;
                Check(def != null, $"{j.key} in the encyclopedia");
                if (def == null) continue;
                Check(def.unitName == j.name, $"{j.key} name", def.unitName);
                Check(Mathf.Approximately(def.value, j.value), $"{j.key} price ${j.value} M", $"{def.value}");
                var prm = def.aircraftParameters;
                Check(prm != null && prm.rankRequired == j.rank, $"{j.key} rank {j.rank}", prm != null ? $"{prm.rankRequired}" : "no parameters");
                var wm = def.unitPrefab != null ? def.unitPrefab.GetComponentInChildren<WeaponManager>(true) : null;
                Check(wm != null, $"{j.key} weapon manager");
                if (wm != null)
                {
                    var names = wm.hardpointSets.Select(s => s.name).ToArray();
                    Check(names.SequenceEqual(Sets), $"{j.key} stations {string.Join(" / ", Sets)}", string.Join(" / ", names));
                    var inner = wm.hardpointSets.FirstOrDefault(s => s.name == "Inner Pylons");
                    var innerKeys = inner != null ? inner.weaponOptions.Where(o => o != null).Select(o => o.jsonKey).ToList() : new List<string>();
                    foreach (var k in j.innerHas) Check(innerKeys.Contains(k), $"{j.key} inner pylons offer {k}");
                    foreach (var k in j.innerNot) Check(!innerKeys.Contains(k), $"{j.key} inner pylons do not offer {k}");
                    var tips = wm.hardpointSets.FirstOrDefault(s => s.name == "Wingtips");
                    var tipKeys = tips != null ? tips.weaponOptions.Where(o => o != null).Select(o => o.jsonKey).ToList() : new List<string>();
                    foreach (var k in Pods) Check(tipKeys.Contains(k), $"{j.key} wingtips offer {k}");
                    var middle = wm.hardpointSets.FirstOrDefault(s => s.name == "Middle Pylons");
                    var racks = middle != null ? middle.weaponOptions.Where(o => o != null && (o.name.Contains("_double") || o.name.Contains("_triple") || o.name.Contains("_quad"))).Select(o => o.name).ToList() : new List<string>();
                    Check(racks.Count == 0, $"{j.key} middle pylons take single stores only", string.Join(", ", racks));
                }
                if (prm != null)
                {
                    Check(prm.StandardLoadouts != null && prm.StandardLoadouts.Length == j.presets, $"{j.key} {j.presets} preset loadouts", $"{prm.StandardLoadouts?.Length}");
                    var dflt = prm.loadouts != null && prm.loadouts.Count > 1 ? prm.loadouts[1].weapons.Where(w => w != null).Select(w => w.jsonKey).ToList() : new List<string>();
                    foreach (var k in j.defaultHas) Check(dflt.Contains(k), $"{j.key} default loadout carries {k}", string.Join(", ", dflt));
                }
            }

            // the jet being flown
            var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
            var jet = ac != null && ac.definition != null ? Jets.FirstOrDefault(x => x.key == ac.definition.jsonKey) : null;
            if (jet == null) log.LogInfo("[TEST] (not flying a pack jet: player-jet checks skipped)");
            else
            {
                var comps = ac.GetComponents<MonoBehaviour>().Where(c => c != null).Select(c => c.GetType().Name).ToList();
                foreach (var d in Drivers) Check(comps.Contains(d), $"{jet.key} flown: {d} attached");
                Check(comps.Contains("InstrumentDriver"), $"{jet.key} flown: cockpit instruments attached");
                if (jet.glass) Check(comps.Contains("ScreenDriver"), $"{jet.key} flown: glass screens attached");
                var cockpit = ac.cockpit != null ? ((Component)ac.cockpit).transform : ac.transform;
                int needles = cockpit.GetComponentsInChildren<Transform>(true).Count(t => t.name == "needle");
                Check(jet.glass || needles >= 20, $"{jet.key} flown: analog needles built", $"{needles}");
                // countermeasure stations: the game pools them by name (IR Flares, Radar Chaff); wingtip pods add to them
                var cmf = typeof(Aircraft).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(f => f.FieldType == typeof(CountermeasureManager));
                var cm = cmf?.GetValue(ac) as CountermeasureManager;
                var stations = cm != null ? typeof(CountermeasureManager).GetField("countermeasureStations", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(cm) as System.Collections.IList : null;
                int Ammo(string name)
                {
                    if (stations == null) return -1;
                    foreach (var st in stations)
                        if ((string)st.GetType().GetField("displayName").GetValue(st) == name) return (int)st.GetType().GetField("ammo").GetValue(st);
                    return -1;
                }
                Check(Ammo("IR Flares") >= 90, $"{jet.key} flown: 90+ flares", $"{Ammo("IR Flares")}");
                Check(Ammo("Radar Chaff") >= 90, $"{jet.key} flown: 90+ chaff", $"{Ammo("Radar Chaff")}");
            }
            Summary();
        }

        static void Summary() => log.LogInfo($"[TEST] packcheck: {passed} passed, {failed} failed");
    }
}
