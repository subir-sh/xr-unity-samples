using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;

public class Detection3DOverlay_ARFoundation_LeftEyeDebug : MonoBehaviour
{
    [Header("Sources")]
    [SerializeField] private SentisYoloDetector detector;
    [SerializeField] private Camera xrCamera;
    [SerializeField] private ARRaycastManager arRaycast;

    [Header("Raycast")]
    [SerializeField] private TrackableType trackables = TrackableType.Planes | TrackableType.FeaturePoint;

    [Header("BBox coords")]
    [SerializeField] private bool flipY = true;

    [Header("Visuals")]
    [SerializeField] private bool drawOutline = true;
    [SerializeField, Min(0.0001f)] private float outlineWidth = 0.003f;

    [Header("Label")]
    [SerializeField] private bool showLabel = true;
    [SerializeField] private bool labelFaceEye = true;
    [SerializeField] private float labelDepthOffset = 0.015f;
    [SerializeField] private float labelFontSize = 0.35f;

    [Header("Label Size Stabilization (screen-ish size)")]
    [SerializeField] private bool stabilizeLabelSize = true;
    [SerializeField, Min(0.001f)] private float labelRefDistance = 1.0f;
    [SerializeField, Min(0.0001f)] private float labelRefScale = 1.0f;
    [SerializeField, Min(0.0001f)] private float labelMinScale = 0.7f;
    [SerializeField, Min(0.0001f)] private float labelMaxScale = 2.5f;

    [Header("Placement")]
    [SerializeField, Min(1)] private int maxBoxes = 30;
    [SerializeField, Min(0f)] private float persistSeconds = 0.25f;
    [SerializeField] private bool invertForward = false;
    [SerializeField] private bool clampMinSize = true;
    [SerializeField, Min(0.001f)] private float minSizeMeters = 0.02f;

    [Header("Debug Logs")]
    [SerializeField] private bool debugLogs = true;
    [SerializeField, Min(0.1f)] private float logEverySeconds = 1.0f;
    [SerializeField] private bool debugDrawRays = false;

    [Header("Debug 3D Markers (instantiate once @ first hit)")]
    [SerializeField] private bool debugInstantiateOnce = true;
    [SerializeField, Min(0.001f)] private float debugSphereScale = 0.02f;
    [SerializeField, Min(0.001f)] private float debugPoseCubeScale = 0.035f;
    [SerializeField, Min(0.001f)] private float debugForwardLen = 0.18f;
    [SerializeField, Min(0.0001f)] private float debugLineWidth = 0.004f;
    [SerializeField, Min(0.01f)] private float debugRayLength = 3.0f;

    private readonly List<ARRaycastHit> _hits = new();
    private readonly List<BoxItem> _active = new();
    private readonly Stack<BoxItem> _pool = new();
    private float _lastLogT = -999f;

    private class BoxItem
    {
        public Transform root;
        public LineRenderer lr;
        public TMP_Text label;
        public Transform labelRoot;
        public float lastSeen;
    }

    // --- Debug marker state (created once) ---
    private bool _debugCaptured = false;
    private Transform _debugRoot;

    // (1) ray origin sphere
    private GameObject _dbgRayOriginSphere;
    // (2) ray line
    private LineRenderer _dbgRayLine;
    // (3) ray hit sphere
    private GameObject _dbgRayHitSphere;
    // (4) LeftEye pose marker (cube + forward line)
    private Transform _dbgLeftEyeMarker;
    // (5) Main cam pose marker (cube + forward line)
    private Transform _dbgMainCamMarker;

    private void LateUpdate()
    {
        if (!ValidateRefs()) return;

        var dets = detector.Detections;
        int count = Mathf.Min(dets.Count, maxBoxes);

        for (int i = 0; i < _active.Count; i++)
            _active[i].lastSeen = -Mathf.Abs(_active[i].lastSeen);

        int hits = 0, miss = 0, placed = 0;

        Pose eyePose = GetEyePose(xrCamera, Camera.StereoscopicEye.Left);
        Vector3 eyePos = eyePose.position;
        Vector3 eyeUp = eyePose.rotation * Vector3.up;

        for (int i = 0; i < count; i++)
        {
            var d = dets[i];
            Vector4 b = d.box; // normalized xyxy (0..1)

            Vector2 uvCenter = new Vector2((b.x + b.z) * 0.5f, (b.y + b.w) * 0.5f);
            Vector2 uvMin = new Vector2(b.x, b.y);
            Vector2 uvMax = new Vector2(b.z, b.w);

            if (flipY)
            {
                uvCenter.y = 1f - uvCenter.y;
                float yMin = 1f - uvMax.y;
                float yMax = 1f - uvMin.y;
                uvMin.y = yMin;
                uvMax.y = yMax;
            }

            Ray centerRay = ViewportPointToStereoRay(xrCamera, uvCenter, Camera.StereoscopicEye.Left);
            if (debugDrawRays) Debug.DrawRay(centerRay.origin, centerRay.direction * 3f, Color.cyan);

            if (!TryARRaycast(centerRay, out var hit))
            {
                miss++;
                continue;
            }
            hits++;

            if (hit.distance <= 0.0001f) continue;

            // hit point is the best "ground truth" for placement
            Vector3 worldCenter = hit.pose.position;

            // Debug markers: capture ONLY once, at the first successful hit
            if (debugInstantiateOnce && !_debugCaptured)
            {
                CaptureDebugOnce(centerRay, hit, eyePose, xrCamera.transform);
                _debugCaptured = true;
            }

            Vector3 normalEyeToCenter = (worldCenter - eyePos).normalized;
            Plane plane = new Plane(normalEyeToCenter, worldCenter);

            Ray minRay = ViewportPointToStereoRay(xrCamera, uvMin, Camera.StereoscopicEye.Left);
            Ray maxRay = ViewportPointToStereoRay(xrCamera, uvMax, Camera.StereoscopicEye.Left);
            if (!plane.Raycast(minRay, out float tMin) || !plane.Raycast(maxRay, out float tMax))
                continue;

            Vector3 pMin = minRay.GetPoint(tMin);
            Vector3 pMax = maxRay.GetPoint(tMax);

            Quaternion eyeRot = eyePose.rotation;
            Vector3 pMinLocal = Quaternion.Inverse(eyeRot) * (pMin - eyePos);
            Vector3 pMaxLocal = Quaternion.Inverse(eyeRot) * (pMax - eyePos);

            float width = Mathf.Abs(pMaxLocal.x - pMinLocal.x);
            float height = Mathf.Abs(pMaxLocal.y - pMinLocal.y);

            if (clampMinSize)
            {
                width = Mathf.Max(width, minSizeMeters);
                height = Mathf.Max(height, minSizeMeters);
            }

            var item = GetOrCreate(i);
            item.lastSeen = Time.unscaledTime;

            Vector3 forwardToEye = (eyePos - worldCenter).normalized;
            if (invertForward) forwardToEye = -forwardToEye;

            item.root.SetPositionAndRotation(worldCenter, Quaternion.LookRotation(forwardToEye, eyeUp));
            item.root.localScale = Vector3.one;

            if (drawOutline && item.lr != null)
            {
                item.lr.enabled = true;
                item.lr.startWidth = outlineWidth;
                item.lr.endWidth = outlineWidth;
                SetOutlineRectLocal(item.lr, width, height);
            }
            else if (item.lr != null)
            {
                item.lr.enabled = false;
            }

            if (item.label != null)
            {
                item.label.enabled = showLabel;

                if (showLabel)
                {
                    item.label.text = $"{detector.ClassName(d.classId)} {d.score:0.00}";
                    item.label.fontSize = labelFontSize;

                    item.labelRoot.localPosition = new Vector3(0f, 0f, +labelDepthOffset);

                    if (stabilizeLabelSize)
                    {
                        float dist = Vector3.Distance(eyePos, worldCenter);
                        float s = (dist / Mathf.Max(0.0001f, labelRefDistance)) * labelRefScale;
                        s = Mathf.Clamp(s, labelMinScale, labelMaxScale);
                        item.labelRoot.localScale = Vector3.one * s;
                    }
                    else
                    {
                        item.labelRoot.localScale = Vector3.one;
                    }

                    if (labelFaceEye)
                        item.labelRoot.rotation = Quaternion.LookRotation(item.labelRoot.position - eyePos, eyeUp);
                    else
                        item.labelRoot.rotation = item.root.rotation;
                }
            }

            if (debugLogs && Time.unscaledTime - _lastLogT >= logEverySeconds)
            {
                float dot = Vector3.Dot((eyePos - worldCenter).normalized, item.root.forward);
                float dist = Vector3.Distance(eyePos, worldCenter);
                float scl = (item.labelRoot != null) ? item.labelRoot.localScale.x : 0f;

                Debug.Log(
                    $"[3DOverlay][DBG] center={worldCenter} size=({width:0.000},{height:0.000}) " +
                    $"dist={dist:0.00} labelScale={scl:0.00} dot={dot:0.00} hitType={hit.hitType} hitDist={hit.distance:0.00}"
                );
            }

            placed++;
        }

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var it = _active[i];
            float last = Mathf.Abs(it.lastSeen);
            if (Time.unscaledTime - last > persistSeconds)
            {
                ReturnToPool(it);
                _active.RemoveAt(i);
            }
        }

        if (debugLogs && Time.unscaledTime - _lastLogT >= logEverySeconds)
        {
            _lastLogT = Time.unscaledTime;
            Debug.Log($"[3DOverlay] dets={count} hits={hits} miss={miss} placed={placed} stereo={xrCamera.stereoEnabled} flipY={flipY} stabilizeLabelSize={stabilizeLabelSize}");
        }
    }

    private bool ValidateRefs()
    {
        if (detector == null || xrCamera == null || arRaycast == null)
        {
            ThrottledWarn($"[3DOverlay] Missing refs: detector={detector != null}, cam={xrCamera != null}, arRaycast={arRaycast != null}");
            return false;
        }
        if (!xrCamera.stereoEnabled)
        {
            ThrottledWarn("[3DOverlay] xrCamera.stereoEnabled == false.");
            return false;
        }
        return true;
    }

    private void ThrottledWarn(string msg)
    {
        if (!debugLogs) return;
        if (Time.unscaledTime - _lastLogT < logEverySeconds) return;
        _lastLogT = Time.unscaledTime;
        Debug.LogWarning(msg);
    }

    private bool TryARRaycast(Ray ray, out ARRaycastHit hit)
    {
        _hits.Clear();
        if (arRaycast.Raycast(ray, _hits, trackables))
        {
            hit = _hits[0];
            return true;
        }
        hit = default;
        return false;
    }

    // ----- UI -----

    private BoxItem GetOrCreate(int index)
    {
        while (_active.Count <= index)
            _active.Add(CreateBox());

        var it = _active[index];
        if (!it.root.gameObject.activeSelf) it.root.gameObject.SetActive(true);
        return it;
    }

    private BoxItem CreateBox()
    {
        if (_pool.Count > 0)
        {
            var it = _pool.Pop();
            it.root.gameObject.SetActive(true);
            return it;
        }

        var rootGO = new GameObject("BBoxRoot");
        rootGO.transform.SetParent(transform, false);

        var lr = rootGO.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = true;
        lr.positionCount = 4;
        lr.enabled = false;

        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        lr.material = (sh != null) ? new Material(sh) : null;
        if (lr.material != null)
        {
            if (lr.material.HasProperty("_Color")) lr.material.SetColor("_Color", Color.green);
            if (lr.material.HasProperty("_BaseColor")) lr.material.SetColor("_BaseColor", Color.green);
            lr.material.renderQueue = 5000;
        }

        var labelRootGO = new GameObject("LabelRoot");
        labelRootGO.transform.SetParent(rootGO.transform, false);

        TMP_Text label = labelRootGO.GetComponentInChildren<TMP_Text>(true);
        if (label == null)
        {
            var tmpGO = new GameObject("LabelTMP");
            tmpGO.transform.SetParent(labelRootGO.transform, false);
            label = tmpGO.AddComponent<TextMeshPro>();
            label.alignment = TextAlignmentOptions.Center;
            label.text = "Label";
        }

        return new BoxItem
        {
            root = rootGO.transform,
            lr = lr,
            label = label,
            labelRoot = labelRootGO.transform,
            lastSeen = Time.unscaledTime
        };
    }

    private void ReturnToPool(BoxItem it)
    {
        it.root.gameObject.SetActive(false);
        _pool.Push(it);
    }

    private static void SetOutlineRectLocal(LineRenderer lr, float width, float height)
    {
        float hx = width * 0.5f;
        float hy = height * 0.5f;
        lr.SetPosition(0, new Vector3(-hx, -hy, 0f));
        lr.SetPosition(1, new Vector3(hx, -hy, 0f));
        lr.SetPosition(2, new Vector3(hx, hy, 0f));
        lr.SetPosition(3, new Vector3(-hx, hy, 0f));
    }

    // ----- Debug markers (instantiate once) -----

    private void CaptureDebugOnce(Ray ray, ARRaycastHit hit, Pose leftEyePose, Transform mainCamTf)
    {
        if (_debugRoot == null)
        {
            var go = new GameObject("DebugMarkers_Once");
            go.transform.SetParent(transform, false);
            _debugRoot = go.transform;
        }

        // (1) Ray origin sphere - Magenta
        _dbgRayOriginSphere = CreateSphere("RayOrigin_Sphere", ray.origin, debugSphereScale, _debugRoot, Color.magenta);

        // (2) Ray line - Cyan
        _dbgRayLine = CreateWorldLine("Ray_Line", _debugRoot, debugLineWidth, Color.cyan);
        Vector3 rayEnd = ray.origin + ray.direction.normalized * debugRayLength;
        _dbgRayLine.SetPosition(0, ray.origin);
        _dbgRayLine.SetPosition(1, rayEnd);

        // (3) Ray hit sphere - Yellow
        Vector3 hitPos = hit.pose.position;
        _dbgRayHitSphere = CreateSphere("RayHit_Sphere", hitPos, debugSphereScale, _debugRoot, Color.yellow);

        // (4) LeftEye pose marker - Red cube + Orange forward line
        _dbgLeftEyeMarker = CreatePoseMarker(
            "LeftEye_Pose",
            leftEyePose.position,
            leftEyePose.rotation,
            _debugRoot,
            debugPoseCubeScale,
            debugForwardLen,
            debugLineWidth,
            cubeColor: Color.red,
            forwardLineColor: new Color(1f, 0.5f, 0f) // orange
        );

        // (5) Main Camera pose marker - Green cube + Blue forward line
        _dbgMainCamMarker = CreatePoseMarker(
            "MainCamera_Pose",
            mainCamTf.position,
            mainCamTf.rotation,
            _debugRoot,
            debugPoseCubeScale,
            debugForwardLen,
            debugLineWidth,
            cubeColor: Color.green,
            forwardLineColor: Color.blue
        );

        if (debugLogs)
        {
            Debug.Log(
                $"[3DOverlay][DBG-ONCE]\n" +
                $"  RayOrigin   (Magenta) = {ray.origin}\n" +
                $"  RayDir      (Cyan)    = {ray.direction}\n" +
                $"  RayHit       (Yellow) = {hitPos}  hitDist={hit.distance:0.00}  hitType={hit.hitType}\n" +
                $"  LeftEyePose   (Red/Orange)\n" +
                $"    pos={leftEyePose.position}\n" +
                $"    euler={leftEyePose.rotation.eulerAngles}\n" +
                $"    quat={leftEyePose.rotation}\n" +
                $"  MainCamPose   (Green/Blue)\n" +
                $"    pos={mainCamTf.position}\n" +
                $"    euler={mainCamTf.rotation.eulerAngles}\n" +
                $"    quat={mainCamTf.rotation}"
            );
        }
    }

    private static GameObject CreateSphere(string name, Vector3 pos, float scale, Transform parent, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * scale;

        var col = go.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);

        ApplyColor(go, color);
        return go;
    }

    private static LineRenderer CreateWorldLine(string name, Transform parent, float width, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.startWidth = width;
        lr.endWidth = width;

        var mat = MakeUnlitMat(color);
        lr.material = mat;
        if (lr.material != null) lr.material.renderQueue = 5000;

        return lr;
    }

    // Pose marker: rotated cube + forward line (local space)
    private static Transform CreatePoseMarker(
        string name,
        Vector3 pos,
        Quaternion rot,
        Transform parent,
        float cubeScale,
        float forwardLen,
        float lineWidth,
        Color cubeColor,
        Color forwardLineColor
    )
    {
        var root = new GameObject(name).transform;
        root.SetParent(parent, false);
        root.SetPositionAndRotation(pos, rot);

        // rotated cube (shows rotation visually)
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Body_Cube";
        cube.transform.SetParent(root, false);
        cube.transform.localPosition = Vector3.zero;
        cube.transform.localRotation = Quaternion.identity;
        cube.transform.localScale = Vector3.one * cubeScale;

        var cubeCol = cube.GetComponent<Collider>();
        if (cubeCol != null) Object.Destroy(cubeCol);
        ApplyColor(cube, cubeColor);

        // forward line in local space (so it rotates with root)
        var fwdGO = new GameObject("Forward_Line");
        fwdGO.transform.SetParent(root, false);

        var lr = fwdGO.AddComponent<LineRenderer>();
        lr.useWorldSpace = false; // local
        lr.positionCount = 2;
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;
        lr.material = MakeUnlitMat(forwardLineColor);
        if (lr.material != null) lr.material.renderQueue = 5000;

        lr.SetPosition(0, Vector3.zero);
        lr.SetPosition(1, Vector3.forward * forwardLen);

        return root;
    }

    private static void ApplyColor(GameObject go, Color color)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;

        var mat = MakeUnlitMat(color);
        if (mat != null)
        {
            mat.renderQueue = 5000;
            r.material = mat;
        }
    }

    private static Material MakeUnlitMat(Color color)
    {
        Shader sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return null;

        var mat = new Material(sh);

        // common properties
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);

        return mat;
    }

    // ----- Left-eye stereo ray utilities -----

    public static Ray ViewportPointToStereoRay(Camera cam, Vector2 viewport01, Camera.StereoscopicEye eye)
    {
        Matrix4x4 V = cam.GetStereoViewMatrix(eye);
        Matrix4x4 P = cam.GetStereoProjectionMatrix(eye);
        Matrix4x4 invVP = (P * V).inverse;

        float x = viewport01.x * 2f - 1f;
        float y = viewport01.y * 2f - 1f;

        Vector4 nearClip = new Vector4(x, y, -1f, 1f);
        Vector4 farClip = new Vector4(x, y, 1f, 1f);

        Vector4 nearW = invVP * nearClip; nearW /= nearW.w;
        Vector4 farW = invVP * farClip; farW /= farW.w;

        Vector3 origin = new Vector3(nearW.x, nearW.y, nearW.z);
        Vector3 dir = (new Vector3(farW.x, farW.y, farW.z) - origin).normalized;
        return new Ray(origin, dir);
    }

    public static Pose GetEyePose(Camera cam, Camera.StereoscopicEye eye)
    {
        Matrix4x4 V = cam.GetStereoViewMatrix(eye);
        Matrix4x4 W = V.inverse;

        Vector3 pos = W.GetColumn(3);
        Quaternion rot = Quaternion.LookRotation(W.GetColumn(2), W.GetColumn(1));
        return new Pose(pos, rot);
    }
}
