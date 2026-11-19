using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace NODevDriver
{
    // Chaff against real radar missiles (dev only): a hostile shooter ahead, the player's jet held on a fixed course relative to it
    // (beam, 45 deg, head-on...), one chaff press when the first missile closes to a set range, and every radar missile aimed at the
    // player logged until it hits, misses or loses its lock.
    //   foe <jsonKey> <distance m> [loadout index]    a hostile aircraft ahead, flying at the player
    //   flyvec <deg from the foe> <speed m/s> | off   hold the player's jet on that course (0 = at the foe, 90 = beam, 180 = away)
    //   shoot <weapon key part>                       the nearest hostile aircraft fires that weapon at the player
    //   chaffat <m> | off                             one press of the jet's chaff once the nearest incoming radar missile is that close
    //   mslwatch                                      start logging incoming radar missiles; "mslreport" prints the outcome of each
    public class ChaffTest : MonoBehaviour
    {
        static ManualLogSource log;
        static Aircraft foe;
        static Vector3? holdVel;
        static float chaffAt = -1f;
        static bool watching;

        class Shot { public Missile m; public MissileSeeker seeker; public string kind; public float firstDist, lastDist, minDist = 1e9f, peakJam, brokeAt = -1f, chaffDist = -1f; public bool ended; public string outcome; public List<float> aspects = new List<float>(); }
        static readonly List<Shot> shots = new List<Shot>();
        static readonly System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        static Aircraft Player() { var hud = SceneSingleton<CombatHUD>.i; return hud != null ? hud.aircraft : null; }
        static ChaffTest Ensure(MonoBehaviour host) => host.GetComponent<ChaffTest>() ?? host.gameObject.AddComponent<ChaffTest>();

        public static void Command(MonoBehaviour host, ManualLogSource logger, string cmd, string arg)
        {
            log = logger; Ensure(host);
            var ac = Player();
            var a = arg.Split(' ');
            float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
            switch (cmd)
            {
                case "foe":
                {
                    if (ac == null) { log.LogWarning("  no aircraft"); return; }
                    var def = Resources.FindObjectsOfTypeAll<AircraftDefinition>().FirstOrDefault(d => d.jsonKey == a[0]);
                    var hq = FindObjectsOfType<FactionHQ>().FirstOrDefault(h => h != ac.NetworkHQ);
                    if (def == null || hq == null) { log.LogWarning($"  foe: definition {def != null}, enemy HQ {hq != null}"); return; }
                    var prm = def.aircraftParameters;
                    log.LogInfo($"  {a[0]}: {prm.StandardLoadouts?.Length ?? -1} standard loadouts, {prm.loadouts?.Count ?? -1} loadouts");
                    if (prm.loadouts != null)
                        for (int k = 0; k < prm.loadouts.Count; k++)
                            log.LogInfo($"  {a[0]} loadouts[{k}]: {string.Join(", ", prm.loadouts[k].weapons.Where(w => w != null).Select(w => w.jsonKey))}");
                    if (prm.StandardLoadouts != null)
                        for (int k = 0; k < prm.StandardLoadouts.Length; k++)
                            log.LogInfo($"  {a[0]} loadout {k}: {string.Join(", ", prm.StandardLoadouts[k].loadout.weapons.Where(w => w != null).Select(w => w.jsonKey))}");
                    int li = a.Length > 2 ? int.Parse(a[2]) : 0;
                    var loadout = prm.StandardLoadouts != null && prm.StandardLoadouts.Length > li ? prm.StandardLoadouts[li].loadout : prm.loadouts[1];
                    var fwd = Vector3.ProjectOnPlane(ac.transform.forward, Vector3.up).normalized;
                    var pos = ac.transform.position + fwd * F(1);
                    NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, def.unitPrefab, loadout, 0.6f, default(LiveryKey), pos.ToGlobalPosition(), Quaternion.LookRotation(-fwd), -fwd * 220f, null, hq, "chafftest_foe", 0.5f, 0.5f);
                    log.LogInfo($"  foe: {a[0]} loadout {li} at {F(1):0} m, side {hq.name}");
                    return;
                }
                case "flyvec":
                {
                    if (a[0] == "off" || ac == null) { holdVel = null; log.LogInfo("  flyvec off"); return; }
                    foe = FindObjectsOfType<Aircraft>().Where(x => x != ac && x.NetworkHQ != ac.NetworkHQ).OrderBy(x => (x.transform.position - ac.transform.position).sqrMagnitude).FirstOrDefault();
                    var to = foe != null ? Vector3.ProjectOnPlane(foe.transform.position - ac.transform.position, Vector3.up).normalized : ac.transform.forward;
                    var dir = Quaternion.Euler(0f, F(0), 0f) * to;
                    // turn the whole jet (every part body, about the aircraft's root) to face the course once, then hold that velocity:
                    // nose and flight path agree, and the pilot's and aircraft's g readings are told it was always so (a sudden
                    // velocity change otherwise reads as a lethal g spike: the jet was "disabled" in the first runs)
                    var fwd = Vector3.ProjectOnPlane(ac.transform.forward, Vector3.up).normalized;
                    var q = Quaternion.FromToRotation(fwd, dir);
                    var pivot = ac.transform.position;
                    foreach (var rb in Bodies(ac)) { rb.position = pivot + q * (rb.position - pivot); rb.rotation = q * rb.rotation; rb.transform.SetPositionAndRotation(rb.position, rb.rotation); }
                    holdVel = dir * F(1); holdBodies = 0;
                    SetVelocity(ac, holdVel.Value);
                    log.LogInfo($"  flyvec {F(0):0} deg from the foe ({(foe != null ? foe.name : "none")}) at {F(1):0} m/s");
                    return;
                }
                case "shoot":   // shoot <weapon key part>: the nearest hostile aircraft fires that weapon at the player
                {
                    foreach (var x in Resources.FindObjectsOfTypeAll<Aircraft>().Where(x => x.gameObject.scene.IsValid()))
                        log.LogInfo($"  aircraft {x.name}: active {x.isActiveAndEnabled}, side {(x.NetworkHQ != null ? x.NetworkHQ.name : "none")}, disabled {x.disabled}");
                    var f = foe != null ? foe : FindObjectsOfType<Aircraft>().Where(x => x != ac && ac != null && x.NetworkHQ != ac.NetworkHQ).OrderBy(x => (x.transform.position - ac.transform.position).sqrMagnitude).FirstOrDefault();
                    var wm = f != null ? (f.weaponManager != null ? f.weaponManager : f.GetComponentInChildren<WeaponManager>()) : null;
                    log.LogInfo($"  shoot: shooter {(f != null ? f.name : "none")}, weapon manager {wm != null}");
                    if (wm == null) { log.LogWarning("  shoot: no hostile aircraft with weapons"); return; }
                    wm.ClearTargetList(); wm.AddTargetList(ac);
                    for (int i = 0; i < 16; i++)
                    {
                        try { wm.SetActiveStation((byte)i); } catch { break; }   // past the last station
                        var st = wm.currentWeaponStation;
                        if (st != null && st.WeaponInfo != null && st.WeaponInfo.name.Contains(a[0]))
                        {
                            try { wm.Fire(); }
                            catch (System.Exception e) { log.LogWarning($"  shoot: Fire failed: {e}"); return; }
                            log.LogInfo($"  shoot: {f.name} fired {st.WeaponInfo.name} at {Vector3.Distance(f.transform.position, ac.transform.position):0} m");
                            return;
                        }
                    }
                    log.LogWarning($"  shoot: {f.name} has no {a[0]}");
                    return;
                }
                case "chaffat": chaffAt = a[0] == "off" ? -1f : F(0); log.LogInfo($"  chaffat {arg}"); return;
                case "mslwatch": watching = true; shots.Clear(); log.LogInfo("  watching incoming radar missiles"); return;
                case "mslreport":
                {
                    foreach (var s in shots)
                    {
                        string what = s.brokeAt >= 0 ? $"lock broken at {s.brokeAt:0} m" : s.outcome ?? (s.m != null ? $"still tracking at {s.lastDist:0} m" : $"gone at {s.lastDist:0} m");
                        string asp = s.aspects.Count > 0 ? $"aspect {s.aspects.Min():0}-{s.aspects.Max():0} deg" : "aspect ?";
                        log.LogInfo($"[TEST] missile {s.kind} launched at {s.firstDist:0} m, chaff at {(s.chaffDist >= 0 ? s.chaffDist.ToString("0") + " m" : "none")}, {asp}, peak jam {s.peakJam:F2}, closest {s.minDist:0} m: {(s.minDist > 30f ? "MISSED" : "reached the jet")} ({what})");
                    }
                    if (shots.Count == 0) log.LogInfo("[TEST] no radar missile came");
                    var me = Player();
                    log.LogInfo($"[TEST] player jet: {(me == null ? "gone" : me.disabled ? "disabled" : "flying")}, chaff left {(me != null && me.GetComponent<ChaffEjector>() != null ? me.GetComponent<ChaffEjector>().GetAmmo() : -1)}");
                    return;
                }
            }
        }

        static List<Rigidbody> Bodies(Aircraft ac) =>
            FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac && p.rb != null && !p.rb.isKinematic).Select(p => p.rb).Distinct().ToList();

        static readonly System.Reflection.FieldInfo PilotPrev = typeof(Pilot).GetField("velocityPrev", BF), AircraftPrev = typeof(Aircraft).GetField("velocityPrev", BF);

        static void SetVelocity(Aircraft ac, Vector3 v)
        {
            foreach (var rb in Bodies(ac)) { rb.velocity = v; rb.angularVelocity = Vector3.zero; }
            foreach (var pl in ac.pilots) if (pl != null) PilotPrev?.SetValue(pl, v);
            AircraftPrev?.SetValue(ac, v);
        }

        // the MiG plugin's chaff read-out (MiG29Instruments.Chaff.Stats), when it is loaded
        static readonly System.Reflection.MethodInfo ChaffStats = System.AppDomain.CurrentDomain.GetAssemblies()
            .Select(x => x.GetType("MiG29Instruments.Chaff", false)).FirstOrDefault(t => t != null)?.GetMethod("Stats");

        static int holdBodies;
        void FixedUpdate()
        {
            var ac = Player();
            if (ac == null || holdVel == null) return;
            // let go once the jet is hit or loses a part: holding a breaking jet's pieces flung them about (user, the head-on run)
            int n = Bodies(ac).Count;
            if (ac.disabled || (holdBodies > 0 && n != holdBodies)) { holdVel = null; log.LogInfo($"  flyvec released (jet {(ac.disabled ? "disabled" : "lost a part")})"); return; }
            holdBodies = n;
            SetVelocity(ac, holdVel.Value);
        }

        float nextLog;
        void Update()
        {
            var ac = Player();
            if (!watching || ac == null) return;
            foreach (var m in FindObjectsOfType<Missile>())
            {
                var sk = m.GetComponent<MissileSeeker>();
                if (sk == null || shots.Any(s => s.m == m)) continue;
                var tu = typeof(MissileSeeker).GetField("targetUnit", BF).GetValue(sk) as Unit;
                string kind = sk.GetType().Name;
                if (tu != ac || (kind != "SARHSeeker" && kind != "ARHSeeker")) continue;
                float d = Vector3.Distance(m.transform.position, ac.transform.position);
                shots.Add(new Shot { m = m, seeker = sk, kind = $"{m.name.Replace("(Clone)", "")} ({kind})", firstDist = d, lastDist = d });
                log.LogInfo($"  incoming {m.name} ({kind}) at {d:0} m");
            }
            bool tick = Time.time >= nextLog; if (tick) nextLog = Time.time + 0.5f;
            foreach (var s in shots)
            {
                if (s.ended) continue;
                if (s.m == null) { s.ended = true; if (s.brokeAt < 0) s.outcome = $"gone at {s.lastDist:0} m ({(ac.disabled ? "player hit" : "missed or self-destructed")})"; continue; }
                s.lastDist = Vector3.Distance(s.m.transform.position, ac.transform.position);
                s.minDist = Mathf.Min(s.minDist, s.lastDist);
                float jam = (float)(s.seeker.GetType().GetField("jamAccumulation", BF)?.GetValue(s.seeker) ?? 0f);
                s.peakJam = Mathf.Max(s.peakJam, jam);
                var tu = typeof(MissileSeeker).GetField("targetUnit", BF).GetValue(s.seeker) as Unit;
                if (s.brokeAt < 0 && (tu != ac || s.m.targetID.NotValid)) { s.brokeAt = s.lastDist; log.LogInfo($"  {s.kind}: lock broken at {s.lastDist:0} m"); }
                if (tick)
                {
                    // aspect: the jet's flight path against the missile's line of sight (90 = beaming it)
                    var rb = ac.rb; var v = rb != null ? rb.velocity : Vector3.zero;
                    float aspect = v.sqrMagnitude > 1f ? Vector3.Angle(v, ac.transform.position - s.m.transform.position) : -1f;
                    var stats = ChaffStats?.Invoke(null, new object[] { s.seeker }) as string;
                    log.LogInfo($"  {s.kind}: {s.lastDist:0} m, jam {jam:F2}, aspect {aspect:0} deg{(stats != null ? ", " + stats : "")}");
                    s.aspects.Add(aspect);
                }
                if (chaffAt > 0f && s.chaffDist < 0 && s.lastDist <= chaffAt)
                {
                    s.chaffDist = s.lastDist;
                    var ej = ac.GetComponent<ChaffEjector>();
                    if (ej != null) { ej.Fire(); log.LogInfo($"  chaff pressed at {s.lastDist:0} m (ammo now {ej.GetAmmo()})"); }
                    foreach (var o in shots) if (o.chaffDist < 0 && !o.ended) o.chaffDist = o.lastDist;
                }
            }
        }
    }
}
