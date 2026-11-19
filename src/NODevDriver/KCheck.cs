using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace NODevDriver
{
    // The MiG-29K airframe in the pack (driver command "kcheck"): the single-seat K and the two-seaters MiG-29KUB and MiG-35UB
    // (PACK.md "The two-seaters"). For each that is built: its nine stations in order, the Kh-29T and Kh-31A on the inner and middle
    // pylons, the KAB-500Kr on the inner, its own eleven presets, 12.7 t empty, its own liveries, and the MiG-style hook on the carrier jets only.
    static class KCheck
    {
        static ManualLogSource log;
        static int passed, failed;

        static void Check(bool ok, string what, string why = "")
        {
            if (ok) { passed++; log.LogInfo($"[TEST] PASS {what}"); }
            else { failed++; log.LogWarning($"[TEST] FAIL {what}{(why.Length > 0 ? ": " + why : "")}"); }
        }

        class Jet { public string key, name; public bool carrier; public string[] liveries; }

        static readonly Jet[] Jets =
        {
            new Jet { key = "mig29k_Fulcrum", name = "MiG-29K", carrier = true, liveries = new[] { "MiG-29K Indian Navy 203", "MiG-29K Russian Navy 41" } },
            new Jet { key = "mig29kub_Fulcrum", name = "MiG-29KUB", carrier = true, liveries = new[] { "KUB Indian Navy 113", "KUB Russian Navy 47" } },
            new Jet { key = "mig35ub_Fulcrum", name = "MiG-35UB", carrier = false, liveries = new[] { "MiG-35 VKS Grey 12", "MiG-35 Demonstrator 702" } },
        };
        static readonly string[] Sets = { "GSh-30-1", "Centreline", "Inner Pylons", "Middle Pylons", "Outer Pylons", "Outboard Pylons", "Wingtips", "Tail Hook" };
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static object Get(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            return t.GetField(name, BF)?.GetValue(o) ?? t.GetProperty(name, BF)?.GetValue(o);
        }

        public static void Run(ManualLogSource logger)
        {
            log = logger; passed = failed = 0;
            var lookup = Encyclopedia.Lookup;
            int built = 0;
            foreach (var j in Jets)
            {
                var def = lookup != null && lookup.TryGetValue(j.key, out var u) ? u as AircraftDefinition : null;
                if (def == null) { log.LogInfo($"[TEST] ({j.key} not built: skipped)"); continue; }
                built++;
                Check(def.unitName == j.name, $"{j.key} name", def.unitName);
                var wm = def.unitPrefab != null ? def.unitPrefab.GetComponentInChildren<WeaponManager>(true) : null;
                if (wm != null)
                {
                    var names = wm.hardpointSets.Select(s => s.name).ToArray();
                    Check(names.SequenceEqual(Sets), $"{j.key} stations {string.Join(" / ", Sets)}", string.Join(" / ", names));
                    List<string> Keys(string set) => wm.hardpointSets.FirstOrDefault(s => s.name == set)?.weaponOptions.Where(o => o != null).Select(o => o.jsonKey).ToList() ?? new List<string>();
                    foreach (var set in new[] { "Inner Pylons", "Middle Pylons" })
                        foreach (var k in new[] { "mig29k_Kh29T_single", "mig29k_Kh31A_single" })
                            Check(Keys(set).Contains(k), $"{j.key} {set} offer {k}", string.Join(", ", Keys(set)));
                    Check(Keys("Inner Pylons").Contains("mig29k_KAB500Kr_single"), $"{j.key} Inner Pylons offer the KAB-500Kr", string.Join(", ", Keys("Inner Pylons")));
                    var outboard = Keys("Outboard Pylons");
                    Check(outboard.Contains("mig29_R73_outer"), $"{j.key} Outboard Pylons offer the R-73", string.Join(", ", outboard));
                    var hook = Keys("Tail Hook");
                    Check(j.carrier ? hook.Contains("mig29k_TailHook") : !hook.Contains("mig29k_TailHook"), $"{j.key} tail hook {(j.carrier ? "is the MiG-style one" : "is not the carrier one")}", string.Join(", ", hook));
                }
                var prm = def.aircraftParameters;
                if (prm != null)
                {
                    Check(prm.StandardLoadouts != null && prm.StandardLoadouts.Length == 11, $"{j.key} 11 preset loadouts", $"{prm.StandardLoadouts?.Length}");
                    var liveryNames = new List<string>();
                    if (Get(prm, "liveries") is IEnumerable lv) foreach (var e in lv) if (Get(e, "name") is string n) liveryNames.Add(n);
                    foreach (var name in j.liveries) Check(liveryNames.Contains(name), $"{j.key} livery {name}", string.Join(", ", liveryNames.Distinct()));
                    Check(!liveryNames.Any(n => n.StartsWith("SMT ")), $"{j.key} no SMT liveries");
                }
                var empty = Get(Get(def, "aircraftInfo"), "emptyWeight");
                Check(empty is float f && Mathf.Abs(f - 12700f) < 1f, $"{j.key} 12.7 t empty", $"{empty}");
            }
            Check(built > 0, "a MiG-29K airframe jet is in the pack");
            log.LogInfo($"[TEST] kcheck: {passed} passed, {failed} failed");
        }
    }
}
