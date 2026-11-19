using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace NODevDriver
{
    // Break-every-part pictures (dev only), to compare how the stock jets and the MiGs come apart. The player's jet should be held in
    // the air first ("hold 30"). Then, outermost part first (furthest from the jet's centre), for each part:
    //   _a  the part as it is, framed from outside the jet
    //   _b  after ApplyDamage(dmg): the damage shading and any effects the game spawns on it
    //   _c  after it breaks off, the same view: the break left on the jet
    //   _d  the piece itself, a moment later, falling away
    // Breaking is the game's own disintegration: AeroPart.SpawnFragments (debris effect, its joints let go), plus any joint another
    // part holds on it (BreakJointsToPart); parts hanging under it go with it, as in flight.
    //   partshots <file prefix> [damage, default 60] [max parts]   -> BepInEx/<prefix>_<nn>_<part>_<a|b|c|d>.png, "[TEST] partshots done"
    //   partbreak <part name>                                      break one part, no pictures
    public class PartShots : MonoBehaviour
    {
        static ManualLogSource log;
        static Aircraft Player() { var hud = SceneSingleton<CombatHUD>.i; return hud != null ? hud.aircraft : null; }

        public static void Command(MonoBehaviour host, ManualLogSource logger, string cmd, string arg)
        {
            log = logger;
            var ps = host.GetComponent<PartShots>() ?? host.gameObject.AddComponent<PartShots>();
            var ac = Player();
            if (ac == null) { log.LogWarning("  no aircraft"); return; }
            var a = arg.Split(' ');
            if (cmd == "partbreak")
            {
                var p = Parts(ac).FirstOrDefault(x => x.name == a[0]);
                if (p == null) { log.LogWarning($"  no part {a[0]}"); return; }
                Break(ac, p); log.LogInfo($"  broke {p.name}");
                return;
            }
            float dmg = a.Length > 1 ? float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) : 60f;
            int max = a.Length > 2 ? int.Parse(a[2]) : 999;
            ps.StartCoroutine(ps.Run(ac, a[0], dmg, max));
        }

        static List<UnitPart> Parts(Aircraft ac) => FindObjectsOfType<UnitPart>().Where(p => p.parentUnit == ac).ToList();

        static Bounds? PartBounds(UnitPart p)
        {
            Bounds? b = null;
            foreach (var r in p.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || r.GetComponentInParent<UnitPart>() != p || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (b == null) b = r.bounds; else { var bb = b.Value; bb.Encapsulate(r.bounds); b = bb; }
            }
            return b;
        }

        // The game's disintegration (SpawnFragments: debris effect, the part's joints let go), then the jet held for the pictures is
        // split for real: held parts are kinematic, so let-go joints never break on their own. Every joint between the part's side
        // (the part and whatever hangs only from it) and the rest goes, and that side falls.
        static void Break(Aircraft ac, UnitPart p)
        {
            if (p is AeroPart ap)
                foreach (var other in Parts(ac).OfType<AeroPart>()) if (other != ap) other.BreakJointsToPart(ap);
            p.SpawnFragments();
            var bodies = Parts(ac).Select(x => x.rb).Where(r => r != null).Distinct().ToList();
            var joints = bodies.SelectMany(r => r.GetComponents<Joint>()).Where(j => j != null && j.connectedBody != null).ToList();
            var rootRb = ac.rb; var partRb = p.rb;
            if (partRb == null || partRb == rootRb) return;
            var reach = new HashSet<Rigidbody> { rootRb }; var todo = new Queue<Rigidbody>(); todo.Enqueue(rootRb);
            while (todo.Count > 0)
            {
                var r = todo.Dequeue();
                foreach (var j in joints)
                {
                    var a = j.GetComponent<Rigidbody>(); var b = j.connectedBody;
                    var o = a == r ? b : b == r ? a : null;
                    if (o == null || o == partRb || reach.Contains(o)) continue;
                    reach.Add(o); todo.Enqueue(o);
                }
            }
            var side = bodies.Where(r => !reach.Contains(r)).ToList();
            foreach (var j in joints)
            {
                var a = j.GetComponent<Rigidbody>(); var b = j.connectedBody;
                if (side.Contains(a) != side.Contains(b)) Destroy(j);
            }
            foreach (var r in side) { r.isKinematic = false; r.velocity = (r.worldCenterOfMass - rootRb.worldCenterOfMass).normalized * 2f; }
        }

        static void Shot(Vector3 from, Vector3 to, Vector3 up, float fov, string file)
        {
            var go = new GameObject("devcam"); var cam = go.AddComponent<Camera>();
            cam.transform.position = from; cam.transform.LookAt(to, up);
            cam.fieldOfView = fov; cam.nearClipPlane = 0.05f; cam.farClipPlane = 2000f;
            var main = Camera.main; if (main != null) cam.cullingMask = main.cullingMask;
            var rt = new RenderTexture(1200, 800, 24); cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt; var tex = new Texture2D(1200, 800, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1200, 800), 0, 0); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, file), tex.EncodeToPNG());
            Destroy(tex); rt.Release(); Destroy(rt); Destroy(go);
        }

        IEnumerator Run(Aircraft ac, string prefix, float dmg, int max)
        {
            var root = ac.transform;
            var centre = root.position;
            var order = Parts(ac).Where(p => p.transform != root && PartBounds(p) != null)
                .OrderByDescending(p => (PartBounds(p).Value.center - centre).magnitude).Take(max).ToList();
            log.LogInfo($"  partshots: {order.Count} parts: {string.Join(", ", order.Select(p => p.name))}");
            int n = 0;
            foreach (var p in order)
            {
                n++;
                var name = $"{prefix}_{n:00}_{p.name}";
                var pb = PartBounds(p);
                if (p == null || pb == null) { log.LogInfo($"  {name}: gone already (went with a part broken earlier)"); continue; }
                var c = pb.Value.center;
                // look at the part from outside the jet: out from the jet's centre, a little above, far enough to frame it
                var outDir = Vector3.ProjectOnPlane(c - root.position, root.up);
                if (outDir.sqrMagnitude < 0.25f) outDir = -root.forward;   // parts on the centreline: from behind and above
                outDir = (outDir.normalized + root.up * 0.45f).normalized;
                float dist = Mathf.Clamp(pb.Value.extents.magnitude * 2.6f, 2.5f, 14f);
                var from = c + outDir * dist;
                Shot(from, c, root.up, 40f, name + "_a.png");
                p.ApplyDamage(dmg, 0f, 0f, 0f);
                yield return new WaitForSeconds(0.6f);
                Shot(from, c, root.up, 40f, name + "_b.png");
                Break(ac, p);
                yield return new WaitForSeconds(1.2f);
                Shot(from, c, root.up, 40f, name + "_c.png");
                var fb = p != null ? PartBounds(p) : null;
                if (fb != null) Shot(fb.Value.center + outDir * dist * 1.2f, fb.Value.center, Vector3.up, 40f, name + "_d.png");
                bool off = p != null && (p.transform.position - c).magnitude > 0.3f;
                log.LogInfo($"  {name}: hp after {dmg} dmg, {(off ? "came off" : "still in place")}");
                yield return new WaitForSeconds(0.2f);
            }
            log.LogInfo("[TEST] PASS partshots done");
        }
    }
}
