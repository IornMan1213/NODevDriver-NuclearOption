using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace NODevDriver
{
    // The released MiG-29 Fulcrum and MiG-29SMT mods, installed instead of the pack (driver command "modcheck"): the same kind of
    // checks as PackCheck, for whichever of the two mods' plugins is loaded, with each mod's own keys (mig29_* / mig29smt_*).
    static class ModCheck
    {
        static ManualLogSource log;
        static int passed, failed;

        static void Check(bool ok, string what, string why = "")
        {
            if (ok) { passed++; log.LogInfo($"[TEST] PASS {what}"); }
            else { failed++; log.LogWarning($"[TEST] FAIL {what}{(why.Length > 0 ? ": " + why : "")}"); }
        }

        class Mod { public string guid, key, name, prefix, defaultKey; public int presets; public bool glass; }

        static readonly Mod[] Mods =
        {
            new Mod { guid = "iornman.mig29.instruments", key = "mig29_Fulcrum", name = "MiG-29 Fulcrum", prefix = "mig29_", defaultKey = "mig29_R27R_single", presets = 5 },
            new Mod { guid = "iornman.mig29smt.instruments", key = "mig29smt_Fulcrum", name = "MiG-29SMT", prefix = "mig29smt_", defaultKey = "mig29smt_R77_single", presets = 8, glass = true },
        };
        static readonly string[] Sets = { "GSh-30-1", "Centreline", "Inner Pylons", "Middle Pylons", "Outer Pylons", "Wingtips", "Tail Hook" };
        static readonly string[] Drivers = { "StoresDriver", "NozzleDriver", "GearPathDriver", "IntakeDriver", "ChuteDriver" };

        public static void Run(ManualLogSource logger)
        {
            log = logger; passed = failed = 0;
            var guids = BepInEx.Bootstrap.Chainloader.PluginInfos.Keys.ToList();
            Check(!guids.Contains("iornman.mig29pack"), "the MiG-29 Pack is not loaded (the mods are tested on their own)");
            var mods = Mods.Where(m => guids.Contains(m.guid)).ToList();
            Check(mods.Count > 0, "a MiG-29 mod's plugin is loaded", string.Join(", ", guids));
            var lookup = Encyclopedia.Lookup;
            foreach (var m in mods)
            {
                var def = lookup != null && lookup.TryGetValue(m.key, out var u) ? u as AircraftDefinition : null;
                Check(def != null, $"{m.key} in the encyclopedia");
                if (def == null) continue;
                Check(def.unitName == m.name, $"{m.key} name", def.unitName);
                var wm = def.unitPrefab != null ? def.unitPrefab.GetComponentInChildren<WeaponManager>(true) : null;
                if (wm != null)
                {
                    var names = wm.hardpointSets.Select(s => s.name).ToArray();
                    Check(names.SequenceEqual(Sets), $"{m.key} stations {string.Join(" / ", Sets)}", string.Join(" / ", names));
                    var tips = wm.hardpointSets.FirstOrDefault(s => s.name == "Wingtips");
                    var tipKeys = tips != null ? tips.weaponOptions.Where(o => o != null).Select(o => o.jsonKey).ToList() : new List<string>();
                    foreach (var p in new[] { "flares", "chaff", "mixed" })
                        Check(tipKeys.Contains(m.prefix + "cmpod_" + p), $"{m.key} wingtips offer {m.prefix}cmpod_{p}", string.Join(", ", tipKeys));
                }
                var prm = def.aircraftParameters;
                if (prm != null)
                {
                    Check(prm.StandardLoadouts != null && prm.StandardLoadouts.Length == m.presets, $"{m.key} {m.presets} preset loadouts", $"{prm.StandardLoadouts?.Length}");
                    var dflt = prm.loadouts != null && prm.loadouts.Count > 1 ? prm.loadouts[1].weapons.Where(w => w != null).Select(w => w.jsonKey).ToList() : new List<string>();
                    Check(dflt.Contains(m.defaultKey), $"{m.key} default loadout carries {m.defaultKey}", string.Join(", ", dflt));
                }
            }

            var hud = SceneSingleton<CombatHUD>.i; var ac = hud != null ? hud.aircraft : null;
            var flown = ac != null && ac.definition != null ? mods.FirstOrDefault(x => x.key == ac.definition.jsonKey) : null;
            if (flown == null) log.LogInfo("[TEST] (not flying a MiG: flown checks skipped)");
            else
            {
                var comps = ac.GetComponents<MonoBehaviour>().Where(c => c != null).Select(c => c.GetType().Name).ToList();
                foreach (var d in Drivers) Check(comps.Contains(d), $"{flown.key} flown: {d} attached");
                Check(comps.Contains("InstrumentDriver"), $"{flown.key} flown: cockpit instruments attached");
                if (flown.glass) Check(comps.Contains("ScreenDriver"), $"{flown.key} flown: glass screens attached");
                var bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var cm = typeof(Aircraft).GetFields(bf).FirstOrDefault(f => f.FieldType == typeof(CountermeasureManager))?.GetValue(ac) as CountermeasureManager;
                var stations = cm != null ? typeof(CountermeasureManager).GetField("countermeasureStations", bf)?.GetValue(cm) as System.Collections.IList : null;
                int Ammo(string n)
                {
                    if (stations != null) foreach (var st in stations)
                        if ((string)st.GetType().GetField("displayName").GetValue(st) == n) return (int)st.GetType().GetField("ammo").GetValue(st);
                    return -1;
                }
                Check(Ammo("IR Flares") >= 90, $"{flown.key} flown: 90+ flares", $"{Ammo("IR Flares")}");
                Check(Ammo("Radar Chaff") >= 90, $"{flown.key} flown: 90+ chaff", $"{Ammo("Radar Chaff")}");
            }
            log.LogInfo($"[TEST] modcheck: {passed} passed, {failed} failed");
        }
    }
}
