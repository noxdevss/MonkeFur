using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonkeFur
{
    internal sealed class FurController : MonoBehaviour
    {
        private const int MirrorOnlyLayer = 22;

        private Plugin cfg;
        private bool rebuildRequested = true;
        private bool userEnabled = true;
        private bool loggedUnreadable;
        private bool failed;
        private bool visible;

        private VRRig rig;
        private SkinnedMeshRenderer body;
        private Mesh sourceMesh;
        private Transform[] bones;
        private Matrix4x4[] bindposes;
        private Matrix4x4[] skinMatrices;
        private Matrix4x4[] boneWorldToLocal;
        private int[] neededBones;

        private int segs;
        private Guide[] guides;
        private Transform[] guideBones;
        private Vector3[] guidePos;
        private Vector3[] guidePrev;
        private Vector3[] restScratch;
        private float[] rowGuideWeight;

        private GameObject root;
        private FurRenderer bodyFur;
        private FurRenderer headFur;

        private float lastDt = 1f / 72f;

        private Vector3 lastRigPos;
        private Quaternion lastRigRot;
        private bool haveRigFrame;

        private bool legacyInputBroken;
        private UnityEngine.InputSystem.Key inputSystemKey = UnityEngine.InputSystem.Key.None;
        private KeyCode cachedKeyCode = KeyCode.None;

        public void RequestRebuild() => rebuildRequested = true;

        private void OnEnable() => Application.onBeforeRender += OnBeforeRender;

        private void OnDisable() => Application.onBeforeRender -= OnBeforeRender;

        private void OnDestroy()
        {
            Application.onBeforeRender -= OnBeforeRender;
            Teardown();
        }

        private void Update()
        {
            cfg = Plugin.Instance;
            if (cfg == null) return;
            if (ToggleKeyPressed(cfg.ToggleKey.Value))
            {
                userEnabled = !userEnabled;
                Plugin.Log("Fur " + (userEnabled ? "on" : "off"));
            }
        }

        private void OnBeforeRender()
        {
            if (failed) return;
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                failed = true;
                SetVisible(false);
                Plugin.Error("MonkeFur crashed and disabled itself: " + e);
            }
        }

        private void Tick()
        {
            cfg = Plugin.Instance;
            if (cfg == null) return;

            if (!cfg.Enabled.Value || !userEnabled || !TryGetLocalBody(out VRRig currentRig, out SkinnedMeshRenderer currentBody))
            {
                SetVisible(false);
                return;
            }

            if (currentBody != body || currentRig != rig || currentBody.sharedMesh != sourceMesh || GuideBonesLost())
            {
                rig = currentRig;
                body = currentBody;
                rebuildRequested = true;
                loggedUnreadable = false;
            }

            if (rebuildRequested)
            {
                rebuildRequested = false;
                Teardown();
                if (!Build())
                {
                    Teardown();
                    return;
                }
            }
            if (guides == null) return;

            SetVisible(true);

            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1f / 240f, 1f / 20f);
            float dtRatio = Mathf.Clamp(dt / lastDt, 0.5f, 2f);
            lastDt = dt;

            UpdateBoneMatrices();
            SimulateGuides(dt, dtRatio);

            Material[] mats = body.sharedMaterials;
            int bodyLayer = body.gameObject.layer;
            bodyFur?.Sync(mats, bodyLayer);
            headFur?.Sync(mats, cfg.HeadFurInFirstPerson.Value ? bodyLayer : MirrorOnlyLayer);
        }

        private static bool TryGetLocalBody(out VRRig rig, out SkinnedMeshRenderer body)
        {
            rig = null;
            body = null;
            if (!GorillaTagger.hasInstance) return false;
            GorillaTagger tagger = GorillaTagger.Instance;
            if (tagger == null) return false;
            rig = tagger.offlineVRRig;
            if (rig == null || !rig.isActiveAndEnabled || rig.bodyRenderer == null) return false;

            GorillaBodyType type = rig.bodyRenderer.bodyType;
            if (type == GorillaBodyType.Skeleton || type == GorillaBodyType.Invisible) return false;

            body = rig.bodyRenderer.ActiveBody;
            return body != null && body.enabled && body.gameObject.activeInHierarchy && body.sharedMesh != null;
        }

        private bool GuideBonesLost() => guideBones != null && guideBones.Length > 0 && guideBones[0] == null;

        private void UpdateBoneMatrices()
        {
            foreach (int b in neededBones)
            {
                Transform t = bones[b];
                skinMatrices[b] = t.localToWorldMatrix * bindposes[b];
                boneWorldToLocal[b] = t.worldToLocalMatrix;
            }
        }

        private Vector3 SkinRest(ref Guide g, Vector3 bindPoint, float guideWeight)
        {
            Vector3 blend = Vector3.zero;
            for (int i = 0; i < g.weightCount; i++)
                blend += skinMatrices[g.bone[i]].MultiplyPoint3x4(bindPoint) * g.weight[i];
            if (guideWeight <= 0f) return blend;
            Vector3 dom = skinMatrices[g.dominant].MultiplyPoint3x4(bindPoint);
            return blend * (1f - guideWeight) + dom * guideWeight;
        }

        private void SimulateGuides(float dt, float dtRatio)
        {
            float damping = Mathf.Pow(cfg.Damping.Value, dt * 60f) * dtRatio;
            Vector3 gravityStep = Physics.gravity * (cfg.GravityScale.Value * dt * dt);
            float carryFactor = 1f - cfg.Inertia.Value;
            float minLift = cfg.MinLift.Value;
            float scale = Mathf.Max(0.01f, rig.scaleFactor);
            float teleportSq = cfg.TeleportDistance.Value * cfg.TeleportDistance.Value * scale * scale;
            float baseStiffness = cfg.Stiffness.Value;
            float worldCarry = 1f - cfg.WorldDrag.Value;
            int points = segs + 1;

            Vector3 rigPos = rig.transform.position;
            Quaternion rigRot = rig.transform.rotation;
            Matrix4x4 frameDelta = Matrix4x4.identity;
            bool frameTeleport = false;
            if (haveRigFrame)
            {
                Quaternion turn = rigRot * Quaternion.Inverse(lastRigRot);
                turn.ToAngleAxis(out float turnAngle, out _);
                if (turnAngle > 180f) turnAngle = 360f - turnAngle;
                Quaternion carriedTurn = turnAngle >= cfg.SnapTurnAngle.Value ? turn : Quaternion.identity;
                frameDelta = Matrix4x4.TRS(rigPos, carriedTurn, Vector3.one) * Matrix4x4.Translate(-lastRigPos);
                frameTeleport = (rigPos - lastRigPos).sqrMagnitude > teleportSq;
            }
            lastRigPos = rigPos;
            lastRigRot = rigRot;
            haveRigFrame = true;

            for (int gi = 0; gi < guides.Length; gi++)
            {
                ref Guide g = ref guides[gi];
                int pi = gi * points;

                if (frameTeleport) g.initialized = false;
                if (g.initialized)
                {
                    g.lastRoot = frameDelta.MultiplyPoint3x4(g.lastRoot);
                    for (int k = 1; k < points; k++)
                    {
                        int i = pi + k;
                        guidePos[i] = Vector3.LerpUnclamped(guidePos[i], frameDelta.MultiplyPoint3x4(guidePos[i]), worldCarry);
                        guidePrev[i] = frameDelta.MultiplyPoint3x4(guidePrev[i]);
                    }
                }

                for (int k = 0; k < points; k++)
                    restScratch[k] = SkinRest(ref g, g.bindRoot + g.bindStep * k, rowGuideWeight[k]);

                Vector3 rootPos = restScratch[0];
                Vector3 n = Vector3.zero;
                for (int i = 0; i < g.weightCount; i++)
                    n += skinMatrices[g.bone[i]].MultiplyVector(g.bindNormal) * g.weight[i];
                n.Normalize();

                if (!g.initialized || (rootPos - g.lastRoot).sqrMagnitude > teleportSq)
                {
                    for (int k = 0; k < points; k++) guidePos[pi + k] = guidePrev[pi + k] = restScratch[k];
                    g.initialized = true;
                }
                else
                {
                    Vector3 carry = (rootPos - g.lastRoot) * carryFactor;
                    guidePos[pi] = guidePrev[pi] = rootPos;

                    for (int k = 1; k < points; k++)
                    {
                        int i = pi + k;
                        Vector3 cur = guidePos[i] + carry;
                        Vector3 old = guidePrev[i] + carry;
                        Vector3 vel = (cur - old) * damping;
                        old = cur;
                        cur += vel + gravityStep;

                        Vector3 parent = guidePos[i - 1];
                        Vector3 restSeg = restScratch[k] - restScratch[k - 1];
                        float seg = restSeg.magnitude;

                        float s = Mathf.Clamp01(baseStiffness * (1f - 0.5f * (k - 1) / segs));
                        cur = Vector3.LerpUnclamped(cur, parent + restSeg, 1f - Mathf.Pow(1f - s, dt * 60f));

                        Vector3 d = cur - parent;
                        float len = d.magnitude;
                        cur = len > 1e-7f ? parent + d * (seg / len) : parent + restSeg;

                        float h = Vector3.Dot(cur - rootPos, n);
                        float minH = seg * k * minLift;
                        if (h < minH) cur += n * (minH - h);

                        guidePos[i] = cur;
                        guidePrev[i] = old;
                    }
                }
                g.lastRoot = rootPos;

                Matrix4x4 toLocal = boneWorldToLocal[g.dominant];
                for (int k = 1; k < points; k++)
                {
                    Vector3 offset = (guidePos[pi + k] - restScratch[k]) / rowGuideWeight[k];
                    guideBones[gi * segs + k - 1].localPosition = toLocal.MultiplyVector(offset);
                }
            }
        }

        private bool Build()
        {
            sourceMesh = body.sharedMesh;
            if (!sourceMesh.isReadable)
            {
                if (!loggedUnreadable) Plugin.Warn($"Body mesh '{sourceMesh.name}' is not CPU-readable; fur disabled for it.");
                loggedUnreadable = true;
                return false;
            }

            Vector3[] meshVerts = sourceMesh.vertices;
            Vector3[] meshNormals = sourceMesh.normals;
            Vector2[] uvs = sourceMesh.uv;
            BoneWeight[] boneWeights = sourceMesh.boneWeights;
            bindposes = sourceMesh.bindposes;
            bones = body.bones;
            if (bones == null || bones.Length == 0 || boneWeights.Length != meshVerts.Length || bindposes.Length < bones.Length)
            {
                Plugin.Warn($"Body mesh '{sourceMesh.name}' is not skinned the way MonkeFur expects; fur disabled for it.");
                return false;
            }
            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b] == null)
                {
                    Plugin.Warn("Body has a missing bone; fur disabled.");
                    return false;
                }
            }
            if (meshNormals == null || meshNormals.Length != meshVerts.Length)
            {
                sourceMesh.RecalculateNormals();
                meshNormals = sourceMesh.normals;
            }
            if (uvs == null || uvs.Length != meshVerts.Length) uvs = new Vector2[meshVerts.Length];

            int boneCount = bones.Length;
            skinMatrices = new Matrix4x4[boneCount];
            boneWorldToLocal = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++) skinMatrices[b] = bones[b].localToWorldMatrix * bindposes[b];

            var subs = new List<int>();
            int matCount = body.sharedMaterials.Length;
            foreach (string part in cfg.FurSubmeshes.Value.Split(','))
            {
                if (int.TryParse(part.Trim(), out int idx) && idx >= 0 && idx < sourceMesh.subMeshCount && idx < matCount && !subs.Contains(idx))
                    subs.Add(idx);
            }
            if (subs.Count == 0) subs.Add(0);
            int[] submeshList = subs.ToArray();

            var triV = new List<int>();
            var triSub = new List<int>();
            var cumArea = new List<float>();
            float total = 0f;
            for (int si = 0; si < submeshList.Length; si++)
            {
                int[] tris = sourceMesh.GetTriangles(submeshList[si]);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Vector3 a = meshVerts[tris[t]], b = meshVerts[tris[t + 1]], c = meshVerts[tris[t + 2]];
                    float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                    if (area <= 1e-12f) continue;
                    total += area;
                    triV.Add(tris[t]); triV.Add(tris[t + 1]); triV.Add(tris[t + 2]);
                    triSub.Add(si);
                    cumArea.Add(total);
                }
            }
            if (cumArea.Count == 0)
            {
                Plugin.Warn("Body mesh has no triangles in the selected submeshes.");
                return false;
            }

            float worldPerMesh = EstimateWorldPerMesh(meshVerts, boneWeights, triV);
            float scale = Mathf.Max(0.01f, rig.scaleFactor);
            float lengthMesh = cfg.FurLength.Value * scale / worldPerMesh;
            float widthMesh = cfg.StrandWidth.Value * scale / worldPerMesh;
            float headRadius = cfg.HeadClearRadius.Value * scale;
            int headBone = FindHeadBone();
            Vector3 eye = GorillaTagger.Instance.mainCamera != null
                ? GorillaTagger.Instance.mainCamera.transform.position
                : rig.transform.position;

            segs = cfg.Segments.Value;
            int points = segs + 1;
            float variation = cfg.LengthVariation.Value;
            float tilt = cfg.Tilt.Value;
            var rng = new System.Random(cfg.Seed.Value);

            int count = cfg.StrandCount.Value;
            var strands = new Strand[count];
            for (int i = 0; i < count; i++)
            {
                float pick = (float)rng.NextDouble() * total;
                int tri = cumArea.BinarySearch(pick);
                if (tri < 0) tri = ~tri;
                if (tri >= cumArea.Count) tri = cumArea.Count - 1;

                float r1 = Mathf.Sqrt((float)rng.NextDouble());
                float r2 = (float)rng.NextDouble();
                var bary = new Vector3(1f - r1, r1 * (1f - r2), r1 * r2);
                int v0 = triV[tri * 3], v1 = triV[tri * 3 + 1], v2 = triV[tri * 3 + 2];

                Vector3 p = meshVerts[v0] * bary.x + meshVerts[v1] * bary.y + meshVerts[v2] * bary.z;
                Vector3 n = (meshNormals[v0] * bary.x + meshNormals[v1] * bary.y + meshNormals[v2] * bary.z).normalized;
                Vector3 edge = meshVerts[v1] - meshVerts[v0];
                edge -= n * Vector3.Dot(edge, n);
                edge = edge.sqrMagnitude > 1e-12f ? edge.normalized : Vector3.Cross(n, Vector3.up).normalized;
                float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                Vector3 side = edge * Mathf.Cos(angle) + Vector3.Cross(n, edge) * Mathf.Sin(angle);
                Vector3 restDir = (n + Vector3.Cross(side, n) * tilt).normalized;

                int wv = bary.x >= bary.y && bary.x >= bary.z ? v0 : (bary.y >= bary.z ? v1 : v2);
                ref Strand s = ref strands[i];
                s.root = p;
                s.normal = n;
                s.side = (side - restDir * Vector3.Dot(side, restDir)).normalized;
                s.step = restDir * (lengthMesh * (1f - variation * (float)rng.NextDouble()) / segs);
                s.uv = uvs[v0] * bary.x + uvs[v1] * bary.y + uvs[v2] * bary.z;
                s.submeshSlot = triSub[tri];
                s.SetWeights(boneWeights[wv]);
                s.guide = -1;

                Vector3 worldRoot = skinMatrices[s.dominant].MultiplyPoint3x4(p);
                s.isHead = (headBone >= 0 && s.dominant == headBone) || (worldRoot - eye).sqrMagnitude < headRadius * headRadius;
            }

            int guideCount = Mathf.Clamp(cfg.GuideCount.Value, 1, count);
            var guideList = new List<int>(guideCount);
            for (int i = 0; i < guideCount; i++) guideList.Add(i);

            var guidesByBone = new Dictionary<int, List<int>>();
            foreach (int gi in guideList) GetList(guidesByBone, strands[gi].dominant).Add(gi);
            for (int i = guideCount; i < count; i++)
            {
                if (!guidesByBone.ContainsKey(strands[i].dominant))
                {
                    guideList.Add(i);
                    GetList(guidesByBone, strands[i].dominant).Add(i);
                }
            }

            var guideIndexOfStrand = new Dictionary<int, int>(guideList.Count);
            for (int gIdx = 0; gIdx < guideList.Count; gIdx++) guideIndexOfStrand[guideList[gIdx]] = gIdx;
            for (int i = 0; i < count; i++)
            {
                ref Strand s = ref strands[i];
                if (guideIndexOfStrand.TryGetValue(i, out int own)) { s.guide = own; continue; }
                float best = float.MaxValue;
                foreach (int gi in guidesByBone[s.dominant])
                {
                    float dist = (strands[gi].root - s.root).sqrMagnitude;
                    if (Vector3.Dot(strands[gi].normal, s.normal) < 0.3f) dist *= 100f;
                    if (dist < best) { best = dist; s.guide = guideIndexOfStrand[gi]; }
                }
            }

            root = new GameObject("MonkeFur");
            DontDestroyOnLoad(root);
            root.hideFlags = HideFlags.HideAndDontSave;

            guides = new Guide[guideList.Count];
            guideBones = new Transform[guides.Length * segs];
            var extraBindposes = new Matrix4x4[guideBones.Length];
            var needed = new HashSet<int>();
            for (int gIdx = 0; gIdx < guides.Length; gIdx++)
            {
                ref Strand s = ref strands[guideList[gIdx]];
                ref Guide g = ref guides[gIdx];
                g.bindRoot = s.root;
                g.bindStep = s.step;
                g.bindNormal = s.normal;
                g.bone = s.bone;
                g.weight = s.weight;
                g.weightCount = s.weightCount;
                g.dominant = s.dominant;
                for (int i = 0; i < g.weightCount; i++) needed.Add(g.bone[i]);
                needed.Add(g.dominant);

                for (int k = 0; k < segs; k++)
                {
                    var t = new GameObject("MonkeFurGuide").transform;
                    t.SetParent(bones[g.dominant], false);
                    t.localPosition = Vector3.zero;
                    t.localRotation = Quaternion.identity;
                    t.localScale = Vector3.one;
                    guideBones[gIdx * segs + k] = t;
                    extraBindposes[gIdx * segs + k] = bindposes[g.dominant];
                }
            }
            neededBones = new int[needed.Count];
            needed.CopyTo(neededBones);

            guidePos = new Vector3[guides.Length * points];
            guidePrev = new Vector3[guides.Length * points];
            restScratch = new Vector3[points];
            rowGuideWeight = new float[points];
            for (int k = 0; k < points; k++) rowGuideWeight[k] = (float)k / segs;

            var allBones = new Transform[boneCount + guideBones.Length];
            Array.Copy(bones, allBones, boneCount);
            Array.Copy(guideBones, 0, allBones, boneCount, guideBones.Length);
            var allBindposes = new Matrix4x4[allBones.Length];
            Array.Copy(bindposes, allBindposes, boneCount);
            Array.Copy(extraBindposes, 0, allBindposes, boneCount, extraBindposes.Length);

            var bodyStrands = new List<Strand>(count);
            var headStrands = new List<Strand>();
            foreach (Strand s in strands) (s.isHead ? headStrands : bodyStrands).Add(s);

            var settings = new FurMeshSettings
            {
                segs = segs,
                blades = cfg.BladesPerStrand.Value,
                doubleSided = cfg.DoubleSided.Value,
                halfWidth = widthMesh * 0.5f,
                tipWidth = cfg.TipWidth.Value,
                submeshSlots = submeshList.Length,
                guideBoneOffset = boneCount,
                rowGuideWeight = rowGuideWeight,
            };
            if (bodyStrands.Count > 0) bodyFur = new FurRenderer("MonkeFur Body", body.transform, bodyStrands, settings, allBones, allBindposes, body, submeshList);
            if (headStrands.Count > 0) headFur = new FurRenderer("MonkeFur Head", body.transform, headStrands, settings, allBones, allBindposes, body, submeshList);
            visible = true;

            Plugin.Log($"Built fur on '{sourceMesh.name}': {bodyStrands.Count} body + {headStrands.Count} head strands, " +
                       $"{guides.Length} simulated guides, {segs} segments, {settings.blades} blade(s), submeshes [{string.Join(",", submeshList)}], " +
                       "head bone: " + (headBone >= 0 ? bones[headBone].name : "none") +
                       $", body scale {body.transform.lossyScale}, root bone {(body.rootBone != null ? body.rootBone.name : "none")}");
            return true;
        }

        private float EstimateWorldPerMesh(Vector3[] verts, BoneWeight[] weights, List<int> triV)
        {
            float meshSum = 0f, worldSum = 0f;
            int step = Mathf.Max(1, triV.Count / 3 / 200);
            for (int t = 0; t + 1 < triV.Count; t += 3 * step)
            {
                int a = triV[t], b = triV[t + 1];
                int bone = weights[a].boneIndex0;
                Matrix4x4 m = skinMatrices[bone];
                meshSum += (verts[b] - verts[a]).magnitude;
                worldSum += (m.MultiplyPoint3x4(verts[b]) - m.MultiplyPoint3x4(verts[a])).magnitude;
            }
            return meshSum > 1e-9f && worldSum > 1e-9f ? worldSum / meshSum : 1f;
        }

        private static List<int> GetList(Dictionary<int, List<int>> map, int key)
        {
            if (!map.TryGetValue(key, out List<int> list)) map[key] = list = new List<int>();
            return list;
        }

        private int FindHeadBone()
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i].name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            return -1;
        }

        private void SetVisible(bool value)
        {
            if (visible == value) return;
            visible = value;
            bodyFur?.SetActive(value);
            headFur?.SetActive(value);
            if (!value) haveRigFrame = false;
            if (!value && guides != null)
            {
                for (int i = 0; i < guides.Length; i++) guides[i].initialized = false;
            }
        }

        private void Teardown()
        {
            bodyFur?.Destroy();
            headFur?.Destroy();
            bodyFur = null;
            headFur = null;
            if (guideBones != null)
            {
                foreach (Transform t in guideBones) if (t != null) Destroy(t.gameObject);
            }
            if (root != null) Destroy(root);
            guideBones = null;
            guides = null;
            root = null;
            visible = false;
            haveRigFrame = false;
        }

        private bool ToggleKeyPressed(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            if (!legacyInputBroken)
            {
                try
                {
                    return Input.GetKeyDown(key);
                }
                catch (InvalidOperationException)
                {
                    legacyInputBroken = true;
                }
            }

            if (key != cachedKeyCode)
            {
                cachedKeyCode = key;
                string name = key.ToString();
                if (name.StartsWith("Alpha")) name = "Digit" + name.Substring(5);
                if (!Enum.TryParse(name, true, out inputSystemKey)) inputSystemKey = UnityEngine.InputSystem.Key.None;
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && inputSystemKey != UnityEngine.InputSystem.Key.None && kb[inputSystemKey].wasPressedThisFrame;
        }
    }

    internal struct Strand
    {
        public Vector3 root, normal, side, step;
        public Vector2 uv;
        public int submeshSlot;
        public bool isHead;
        public int guide;

        public int[] bone;
        public float[] weight;
        public int weightCount;
        public int dominant;

        public void SetWeights(BoneWeight w)
        {
            var b = new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
            var x = new[] { w.weight0, w.weight1, w.weight2, w.weight3 };
            Array.Sort(x, b);
            Array.Reverse(x);
            Array.Reverse(b);
            weightCount = 0;
            float sum = 0f;
            for (int i = 0; i < 3; i++)
            {
                if (x[i] <= 0f) break;
                sum += x[i];
                weightCount++;
            }
            if (weightCount == 0) { x[0] = 1f; sum = 1f; weightCount = 1; }
            bone = new int[weightCount];
            weight = new float[weightCount];
            for (int i = 0; i < weightCount; i++)
            {
                bone[i] = b[i];
                weight[i] = x[i] / sum;
            }
            dominant = bone[0];
        }
    }

    internal struct Guide
    {
        public Vector3 bindRoot, bindStep, bindNormal;
        public int[] bone;
        public float[] weight;
        public int weightCount;
        public int dominant;
        public Vector3 lastRoot;
        public bool initialized;
    }

    internal sealed class FurMeshSettings
    {
        public int segs;
        public int blades;
        public bool doubleSided;
        public float halfWidth;
        public float tipWidth;
        public int submeshSlots;
        public int guideBoneOffset;
        public float[] rowGuideWeight;
    }

    internal sealed class FurRenderer
    {
        private readonly GameObject go;
        private readonly Mesh mesh;
        private readonly SkinnedMeshRenderer smr;
        private readonly Material[] materials;
        private readonly int[] submeshList;

        public FurRenderer(string name, Transform parent, List<Strand> strands, FurMeshSettings s,
                           Transform[] allBones, Matrix4x4[] allBindposes, SkinnedMeshRenderer body, int[] submeshList)
        {
            this.submeshList = submeshList;
            strands.Sort((a, b) => a.submeshSlot.CompareTo(b.submeshSlot));
            int n = strands.Count;
            int points = s.segs + 1;
            int vertsPerStrand = s.blades * points * 2;
            int vcount = n * vertsPerStrand;

            var verts = new Vector3[vcount];
            var normals = new Vector3[vcount];
            var uv = new Vector2[vcount];
            var weights = new BoneWeight[vcount];

            for (int si = 0; si < n; si++)
            {
                Strand st = strands[si];
                Vector3 sideB = Vector3.Cross(st.step.normalized, st.side);
                int vb = si * vertsPerStrand;
                for (int k = 0; k < points; k++)
                {
                    Vector3 pt = st.root + st.step * k;
                    float hw = s.halfWidth * Mathf.Lerp(1f, s.tipWidth, (float)k / s.segs);
                    BoneWeight bw = MakeWeight(st, k, s);
                    for (int b = 0; b < s.blades; b++)
                    {
                        Vector3 off = (b == 0 ? st.side : sideB) * hw;
                        int r = vb + b * points * 2 + k * 2;
                        verts[r] = pt - off;
                        verts[r + 1] = pt + off;
                        normals[r] = normals[r + 1] = st.normal;
                        uv[r] = uv[r + 1] = st.uv;
                        weights[r] = weights[r + 1] = bw;
                    }
                }
            }

            mesh = new Mesh { name = name, indexFormat = vcount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.boneWeights = weights;
            mesh.bindposes = allBindposes;
            mesh.subMeshCount = s.submeshSlots;

            int trisPerSeg = s.doubleSided ? 4 : 2;
            int start = 0;
            for (int slot = 0; slot < s.submeshSlots; slot++)
            {
                int end = start;
                while (end < n && strands[end].submeshSlot == slot) end++;
                var idx = new int[(end - start) * s.blades * s.segs * trisPerSeg * 3];
                int ii = 0;
                for (int si = start; si < end; si++)
                {
                    for (int b = 0; b < s.blades; b++)
                    {
                        int vb = si * vertsPerStrand + b * points * 2;
                        for (int k = 0; k < s.segs; k++)
                        {
                            int a = vb + k * 2, bR = a + 1, c = a + 2, d = a + 3;
                            idx[ii++] = a; idx[ii++] = c; idx[ii++] = bR;
                            idx[ii++] = bR; idx[ii++] = c; idx[ii++] = d;
                            if (s.doubleSided)
                            {
                                idx[ii++] = a; idx[ii++] = bR; idx[ii++] = c;
                                idx[ii++] = bR; idx[ii++] = d; idx[ii++] = c;
                            }
                        }
                    }
                }
                mesh.SetTriangles(idx, slot, false);
                start = end;
            }
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);

            go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = allBones;
            smr.rootBone = body.rootBone;
            smr.quality = SkinQuality.Bone4;
            smr.updateWhenOffscreen = false;
            smr.skinnedMotionVectors = false;
            smr.shadowCastingMode = ShadowCastingMode.Off;
            smr.receiveShadows = true;
            smr.allowOcclusionWhenDynamic = false;

            if (body.rootBone != null)
            {
                Bounds lb = body.localBounds;
                lb.Expand(lb.size.magnitude * 0.35f);
                smr.localBounds = lb;
            }
            else
            {
                smr.updateWhenOffscreen = true;
            }

            materials = new Material[s.submeshSlots];
        }

        private static BoneWeight MakeWeight(Strand st, int row, FurMeshSettings s)
        {
            float g = st.guide >= 0 && row > 0 ? s.rowGuideWeight[row] : 0f;
            var idx = new int[4];
            var w = new float[4];
            int c = 0;
            for (int i = 0; i < st.weightCount; i++, c++)
            {
                idx[c] = st.bone[i];
                w[c] = st.weight[i] * (1f - g);
            }
            if (g > 0f)
            {
                idx[c] = s.guideBoneOffset + st.guide * s.segs + row - 1;
                w[c] = g;
                c++;
            }
            Array.Sort(w, idx, 0, c);
            Array.Reverse(w, 0, c);
            Array.Reverse(idx, 0, c);
            return new BoneWeight
            {
                boneIndex0 = idx[0], weight0 = w[0],
                boneIndex1 = idx[1], weight1 = w[1],
                boneIndex2 = idx[2], weight2 = w[2],
                boneIndex3 = idx[3], weight3 = w[3],
            };
        }

        public void Sync(Material[] bodyMats, int layer)
        {
            bool changed = false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material m = submeshList[i] < bodyMats.Length ? bodyMats[submeshList[i]] : null;
                if (materials[i] != m) { materials[i] = m; changed = true; }
            }
            if (changed) smr.sharedMaterials = materials;
            if (go.layer != layer) go.layer = layer;
        }

        public void SetActive(bool value)
        {
            if (go != null) go.SetActive(value);
        }

        public void Destroy()
        {
            if (go != null) UnityEngine.Object.Destroy(go);
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
        }
    }
}
