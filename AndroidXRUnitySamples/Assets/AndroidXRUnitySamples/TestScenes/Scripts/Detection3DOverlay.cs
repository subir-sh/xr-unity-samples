using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class Detection3DOverlayXR : MonoBehaviour
{
    [Header("Inputs")]
    [SerializeField] private SentisYoloDetector detector;
    [SerializeField] private Camera xrCamera;

    [Header("AR Raycast")]
    [SerializeField] private ARRaycastManager arRaycast;
    [SerializeField] private TrackableType arTrackables = TrackableType.Planes | TrackableType.FeaturePoint;

    [Header("3D UI Box (World-Space Canvas)")]
    [SerializeField] private RectTransform boxPrefab;   // Image outline + TMP(optional)
    [SerializeField] private Transform contentParent;   // World Space Canvas 아래
    [SerializeField] private float persistSeconds = 0.25f;

    [Header("BBox coords")]
    [SerializeField] private bool yIsTopLeft = true;    // detector bbox y가 top-left면 true

    [Header("Left Eye")]
    [SerializeField] private bool useLeftEyePoseForRays = true;

    [Header("XR Debug Visualize (LineRenderer)")]
    [SerializeField] private bool drawDebug = true;
    [SerializeField] private float debugRayLengthM = 3.0f;
    [SerializeField] private float debugLineWidthM = 0.01f;
    [SerializeField] private float debugFallbackDepthM = 2.0f;
    [SerializeField] private int debugOnlyThisDetectionIndex = 0;

    // runtime
    private readonly List<ARRaycastHit> _hits = new(16);

    private readonly List<BoxData> _drawn = new();
    private readonly List<BoxData> _pool = new();

    private readonly Vector2[] _cornersVP = new Vector2[4];

    // left-eye InputDevice cache
    private InputDevice _leftEyeDevice;
    private bool _leftEyeDeviceFound;

    // debug renderers
    private LineRenderer _lrCenterRay;      // cyan
    private LineRenderer _lrViewportRect;   // yellow
    private LineRenderer _lrDetRect;        // magenta
    private Transform _hitMarker;           // small sphere (optional)

    private class BoxData
    {
        public int classId;
        public float lastUpdate;
        public RectTransform rt;
    }

    private void Awake()
    {
        if (!xrCamera) xrCamera = Camera.main;
        if (boxPrefab) boxPrefab.gameObject.SetActive(false);
        InitializeLeftEyeDevice();

        if (drawDebug)
            CreateDebugObjects();
    }

    private void Update()
    {
        if (!detector || !xrCamera || !arRaycast || !boxPrefab || !contentParent)
            return;

        CleanupBoxes();

        // left eye pose per-frame
        Pose leftPose = default;
        bool hasLeftPose = false;
        if (useLeftEyePoseForRays)
        {
            if (!_leftEyeDeviceFound || !_leftEyeDevice.isValid)
                InitializeLeftEyeDevice();

            hasLeftPose = TryGetLeftEyePose(out var p, out var r);
            if (hasLeftPose) leftPose = new Pose(p, r);
        }

        // ===== Debug: center ray + full viewport rect =====
        if (drawDebug)
        {
            DrawCenterRay(hasLeftPose, leftPose); // cyan

            float depth = GetDepthForDebugPlane(hasLeftPose, leftPose);
            DrawViewportRect(new Rect(0, 0, 1, 1), depth, hasLeftPose, leftPose, _lrViewportRect); // yellow
        }

        var dets = detector.Detections;
        if (dets == null || dets.Count == 0)
        {
            if (drawDebug)
            {
                _lrDetRect.enabled = false;
                if (_hitMarker) _hitMarker.gameObject.SetActive(false);
            }
            return;
        }

        for (int i = 0; i < dets.Count; i++)
        {
            var d = dets[i];

            // bbox normalized xyxy (0..1)
            float x1 = d.box.x;
            float y1 = d.box.y;
            float x2 = d.box.z;
            float y2 = d.box.w;
            if (x2 <= x1 || y2 <= y1) continue;

            // viewport rect (bottom-left origin)
            Rect vpRect = Rect.MinMaxRect(x1, y1, x2, y2);
            if (yIsTopLeft)
                vpRect = new Rect(vpRect.xMin, 1f - vpRect.yMax, vpRect.width, vpRect.height);

            Vector2 vpCenter = vpRect.center;

            // === LeftEye 월드 Ray로 ARRaycast ===
            Ray worldRay = BuildWorldRayFromViewport(vpCenter, hasLeftPose, leftPose);

            if (!TryARRaycast(worldRay, out Pose hitPose, out float hitDistance))
                continue;

            // debug: selected detection bbox rect + hit marker
            if (drawDebug && i == debugOnlyThisDetectionIndex)
            {
                _lrDetRect.enabled = true;
                DrawViewportRect(vpRect, hitDistance, hasLeftPose, leftPose, _lrDetRect); // magenta

                if (_hitMarker)
                {
                    _hitMarker.gameObject.SetActive(true);
                    _hitMarker.position = hitPose.position;
                }
            }

            // === 3D UI 박스 배치 (Meta 샘플 방식과 동일한 핵심) ===
            Pose rayPose = hasLeftPose ? leftPose : new Pose(xrCamera.transform.position, xrCamera.transform.rotation);
            Vector3 origin = rayPose.position;
            Quaternion rot = rayPose.rotation;

            // center point on plane at hitDistance
            Vector3 worldCenter = origin + worldRay.direction.normalized * hitDistance;
            Vector3 viewDir = (worldCenter - origin).normalized;

            // plane perpendicular to viewDir through worldCenter
            Plane plane = new Plane(viewDir, worldCenter);

            // build plane basis (u, v)
            Vector3 camUp = xrCamera.transform.up;
            Vector3 u = Vector3.Cross(camUp, viewDir);
            if (u.sqrMagnitude < 1e-6f) u = Vector3.Cross(xrCamera.transform.right, viewDir);
            u.Normalize();
            Vector3 v = Vector3.Cross(viewDir, u).normalized;

            // 4 corners in viewport
            _cornersVP[0] = new Vector2(vpRect.xMin, vpRect.yMin);
            _cornersVP[1] = new Vector2(vpRect.xMax, vpRect.yMin);
            _cornersVP[2] = new Vector2(vpRect.xMax, vpRect.yMax);
            _cornersVP[3] = new Vector2(vpRect.xMin, vpRect.yMax);

            float minU = float.PositiveInfinity, maxU = float.NegativeInfinity;
            float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
            bool gotAny = false;

            for (int k = 0; k < 4; k++)
            {
                Ray rCorner = BuildWorldRayFromViewport(_cornersVP[k], hasLeftPose, leftPose);
                if (!plane.Raycast(rCorner, out float t)) continue;

                Vector3 pCorner = rCorner.GetPoint(t);
                Vector3 dCorner = pCorner - worldCenter;

                float pu = Vector3.Dot(dCorner, u);
                float pv = Vector3.Dot(dCorner, v);

                gotAny = true;
                minU = Mathf.Min(minU, pu);
                maxU = Mathf.Max(maxU, pu);
                minV = Mathf.Min(minV, pv);
                maxV = Mathf.Max(maxV, pv);
            }

            if (!gotAny) continue;

            Vector2 size = new Vector2(Mathf.Abs(maxU - minU), Mathf.Abs(maxV - minV));
            if (float.IsNaN(size.x) || float.IsNaN(size.y) || size.x <= 0f || size.y <= 0f)
                continue;

            var box = GetOrCreate(d.classId);
            box.lastUpdate = Time.time;

            box.rt.SetPositionAndRotation(worldCenter, Quaternion.LookRotation(viewDir));
            box.rt.sizeDelta = size;

            var uiText = box.rt.GetComponentInChildren<TMP_Text>(true);
            if (uiText) uiText.text = $"{detector.ClassName(d.classId)} {d.score:0.00}";
        }
    }

    // -----------------------
    // AR Raycast
    // -----------------------
    private bool TryARRaycast(Ray ray, out Pose hitPose, out float hitDistance)
    {
        _hits.Clear();
        bool ok = arRaycast.Raycast(ray, _hits, arTrackables);
        if (!ok || _hits.Count == 0)
        {
            hitPose = default;
            hitDistance = 0f;
            return false;
        }

        int best = 0;
        float bestDist = float.PositiveInfinity;
        for (int i = 0; i < _hits.Count; i++)
        {
            float d = _hits[i].distance;
            if (d < bestDist) { bestDist = d; best = i; }
        }

        hitPose = _hits[best].pose;
        hitDistance = bestDist;
        return true;
    }

    // -----------------------
    // LeftEye pose (InputDevice)
    // -----------------------
    private void InitializeLeftEyeDevice()
    {
        var devices = new List<InputDevice>(4);
        InputDevices.GetDevicesAtXRNode(XRNode.LeftEye, devices);

        if (devices.Count > 0)
        {
            _leftEyeDevice = devices[0];
            _leftEyeDeviceFound = _leftEyeDevice.isValid;
        }
        else
        {
            _leftEyeDeviceFound = false;
        }
    }

    private bool TryGetLeftEyePose(out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!_leftEyeDeviceFound || !_leftEyeDevice.isValid)
            return false;

        bool gotPos = _leftEyeDevice.TryGetFeatureValue(CommonUsages.leftEyePosition, out position);
        bool gotRot = _leftEyeDevice.TryGetFeatureValue(CommonUsages.leftEyeRotation, out rotation);
        return gotPos && gotRot;
    }

    // -----------------------
    // Build LeftEye world ray from viewport
    // -----------------------
    private Ray BuildWorldRayFromViewport(Vector2 viewport01, bool hasLeftPose, Pose leftPose)
    {
        Pose rayPose = hasLeftPose ? leftPose : new Pose(xrCamera.transform.position, xrCamera.transform.rotation);

        // 1) get direction from xrCamera projection in world
        Vector3 camDirWorld = xrCamera.ViewportPointToRay(new Vector3(viewport01.x, viewport01.y, 0f)).direction.normalized;

        // 2) convert that direction into xrCamera local
        Vector3 camDirLocal = Quaternion.Inverse(xrCamera.transform.rotation) * camDirWorld;

        // 3) rotate into left-eye (or fallback camera) world
        Vector3 dirWorld = (rayPose.rotation * camDirLocal).normalized;

        return new Ray(rayPose.position, dirWorld);
    }

    // -----------------------
    // Debug objects (LineRenderer)
    // -----------------------
    private void CreateDebugObjects()
    {
        _lrCenterRay = CreateLineRenderer("DBG_CenterRay", Color.cyan, 2);
        _lrViewportRect = CreateLineRenderer("DBG_ViewportRect01", Color.yellow, 5);
        _lrDetRect = CreateLineRenderer("DBG_DetRect", Color.magenta, 5);

        // hit marker (small sphere)
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "DBG_HitMarker";
        go.transform.localScale = Vector3.one * 0.05f;
        Destroy(go.GetComponent<Collider>());
        _hitMarker = go.transform;
        _hitMarker.gameObject.SetActive(false);
    }

    private LineRenderer CreateLineRenderer(string name, Color color, int positions)
    {
        GameObject go = new GameObject(name);
        var lr = go.AddComponent<LineRenderer>();

        lr.useWorldSpace = true;
        lr.positionCount = positions;
        lr.startWidth = debugLineWidthM;
        lr.endWidth = debugLineWidthM;
        lr.numCapVertices = 6;

        var mat = new Material(Shader.Find("Sprites/Default"));
        lr.material = mat;
        lr.startColor = color;
        lr.endColor = color;

        lr.enabled = true;
        return lr;
    }

    private void DrawCenterRay(bool hasLeftPose, Pose leftPose)
    {
        if (!_lrCenterRay) return;

        Ray r = BuildWorldRayFromViewport(new Vector2(0.5f, 0.5f), hasLeftPose, leftPose);

        _lrCenterRay.enabled = true;
        _lrCenterRay.positionCount = 2;
        _lrCenterRay.SetPosition(0, r.origin);
        _lrCenterRay.SetPosition(1, r.origin + r.direction * debugRayLengthM);
    }

    private float GetDepthForDebugPlane(bool hasLeftPose, Pose leftPose)
    {
        Ray center = BuildWorldRayFromViewport(new Vector2(0.5f, 0.5f), hasLeftPose, leftPose);
        if (TryARRaycast(center, out _, out float d))
            return Mathf.Max(0.05f, d);

        return Mathf.Max(0.05f, debugFallbackDepthM);
    }

    // viewport rect (bottom-left) -> draw on plane at given depth along rect center ray
    private void DrawViewportRect(Rect viewportRect01, float depth, bool hasLeftPose, Pose leftPose, LineRenderer lr)
    {
        if (!lr) return;

        Vector2 centerVP = viewportRect01.center;
        Ray centerRay = BuildWorldRayFromViewport(centerVP, hasLeftPose, leftPose);

        Vector3 centerPoint = centerRay.origin + centerRay.direction.normalized * depth;
        Vector3 viewDir = (centerPoint - centerRay.origin).normalized;
        Plane plane = new Plane(viewDir, centerPoint);

        Vector2 c0 = new Vector2(viewportRect01.xMin, viewportRect01.yMin);
        Vector2 c1 = new Vector2(viewportRect01.xMax, viewportRect01.yMin);
        Vector2 c2 = new Vector2(viewportRect01.xMax, viewportRect01.yMax);
        Vector2 c3 = new Vector2(viewportRect01.xMin, viewportRect01.yMax);

        if (!CornerOnPlane(c0, plane, hasLeftPose, leftPose, out var p0)) { lr.enabled = false; return; }
        if (!CornerOnPlane(c1, plane, hasLeftPose, leftPose, out var p1)) { lr.enabled = false; return; }
        if (!CornerOnPlane(c2, plane, hasLeftPose, leftPose, out var p2)) { lr.enabled = false; return; }
        if (!CornerOnPlane(c3, plane, hasLeftPose, leftPose, out var p3)) { lr.enabled = false; return; }

        lr.enabled = true;
        lr.positionCount = 5;
        lr.SetPosition(0, p0);
        lr.SetPosition(1, p1);
        lr.SetPosition(2, p2);
        lr.SetPosition(3, p3);
        lr.SetPosition(4, p0);
    }

    private bool CornerOnPlane(Vector2 vp, Plane plane, bool hasLeftPose, Pose leftPose, out Vector3 p)
    {
        Ray r = BuildWorldRayFromViewport(vp, hasLeftPose, leftPose);
        if (!plane.Raycast(r, out float t))
        {
            p = default;
            return false;
        }
        p = r.GetPoint(t);
        return true;
    }

    // -----------------------
    // Pooling (3D UI boxes)
    // -----------------------
    private void CleanupBoxes()
    {
        for (int i = _drawn.Count - 1; i >= 0; --i)
        {
            if (Time.time - _drawn[i].lastUpdate > persistSeconds)
            {
                ReturnToPool(_drawn[i]);
                _drawn.RemoveAt(i);
            }
        }
    }

    private BoxData GetOrCreate(int classId)
    {
        // classId 별 1개 유지(원하면 det index별로 바꿔도 됨)
        for (int i = _drawn.Count - 1; i >= 0; --i)
            if (_drawn[i].classId == classId)
                return _drawn[i];

        BoxData data;
        if (_pool.Count > 0)
        {
            data = _pool[_pool.Count - 1];
            _pool.RemoveAt(_pool.Count - 1);
            data.rt.gameObject.SetActive(true);
        }
        else
        {
            var rt = Instantiate(boxPrefab, contentParent);
            rt.gameObject.SetActive(true);
            // center anchoring
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            data = new BoxData { rt = rt };
        }

        data.classId = classId;
        data.lastUpdate = Time.time;
        _drawn.Add(data);
        return data;
    }

    private void ReturnToPool(BoxData b)
    {
        b.rt.gameObject.SetActive(false);
        _pool.Add(b);
    }
}
