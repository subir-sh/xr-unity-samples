using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class DetectionUIDrawer : MonoBehaviour
{
    [Header("Sources")]
    [SerializeField] private ObjectDetector detector;
    [SerializeField] private Camera xrCamera; // 유지(라벨 face eye 등)

    [Header("Physics Raycast")]
    //[SerializeField] private LayerMask physicsMask = ~0;
    [SerializeField] private float maxDistance = 10f;

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
    [SerializeField, Min(0f)] private float persistSeconds = 0.2f;
    [SerializeField] private bool invertForward = false;
    [SerializeField] private bool clampMinSize = true;
    [SerializeField, Min(0.001f)] private float minSizeMeters = 0.02f;

    [Header("RGB Camera Intrinsics (for 640x640)")]
    [SerializeField] private float fx = 386.67f;
    [SerializeField] private float fy = 386.67f;
    [SerializeField] private float cx = 320f;
    [SerializeField] private float cy = 320f;
    [SerializeField] private int imageW = 640;
    [SerializeField] private int imageH = 640;

    private readonly List<BoxItem> _active = new();
    private readonly Stack<BoxItem> _pool = new();

    private class BoxItem
    {
        public Transform root;
        public LineRenderer lr;
        public TMP_Text label;
        public Transform labelRoot;
        public float lastSeen;
    }

    // =========================
    // Unity Loop
    // =========================
    private void LateUpdate()
    {
        var dets = detector.Detections;

        if (Time.frameCount % 30 == 0)
            Debug.Log($"[Noonchi]: UIDrawer LateUpdate detCount={(dets == null ? -1 : dets.Count)}");

        if (dets == null || dets.Count == 0)
        {
            CleanupExpired(); // persist seconds가 지난 박스 제거 
            return;
        }

        MarkAllUnseen(); // 이번 추론으로 업데이트된 박스만 살리기 위해, 전부 unseen으로 체크 
        // (박스는 pool에서 나오는 것이므로)

        Pose eyePose = detector.getCachedCameraPose(); 
        Vector3 eyePos = eyePose.position;
        Vector3 eyeUp = eyePose.rotation * Vector3.up;

        for (int i = 0; i < dets.Count; i++)
        {
            // 각 detection에 대해, 현실 세계에서의 위치 찾고 UI 박스 배치
            if (!TryPlaceUI(dets[i], i, eyePose, eyePos, eyeUp)) 
                continue;
        }

        CleanupExpired(); 
    }

    // =========================
    // Placement per detection
    // =========================
    private bool TryPlaceUI(ObjectDetector.Detection d, int index, Pose eyePose, Vector3 eyePos, Vector3 eyeUp)
    {
        // 1) inference -> viewport
        Vector4 b = d.box; // normalized xyxy
        GetInferenceFromBox(b, flipY, out var uvCenter, out var uvMin, out var uvMax);

        // 2) viewport point -> world ray 
        Ray centerRay = InferenceToWorldRay(uvCenter, eyePose);

        // 3) 만들어진 ray 쏘기 -> physics hit 판정
        if (!TryPhysicsRaycast(centerRay, out var hit)) return false;
        //if (hit.distance <= 0.0001f) return false;
        Vector3 worldCenter = hit.point;

        // 5) compute size on tangent plane
        if (!TryComputeSizeMeters(uvMin, uvMax, eyePose, eyePos, worldCenter, out float width, out float height)) return false;

        if (clampMinSize)
        {
            width = Mathf.Max(width, minSizeMeters);
            height = Mathf.Max(height, minSizeMeters);
        }

        // 6) draw/update visuals
        var item = GetOrCreate(index);
        item.lastSeen = Time.unscaledTime;

        UpdateTransform(item, worldCenter, eyePos, eyeUp);
        UpdateOutline(item, width, height);
        UpdateLabel(item, d, worldCenter, eyePos, eyeUp);

        return true;
    }

    // =========================
    // Coordinate transforms 
    // =========================
    private static void GetInferenceFromBox(Vector4 b, bool flipY, out Vector2 center, out Vector2 uvMin, out Vector2 uvMax)
    {
        center = new Vector2((b.x + b.z) * 0.5f, (b.y + b.w) * 0.5f);
        // center x = (xmin + xmax)/2, center y = (ymin + ymax)/2
        
        // bbox의 두 꼭지점 제공
        uvMin = new Vector2(b.x, b.y);
        uvMax = new Vector2(b.z, b.w);

        if (!flipY) return;

        // y축 뒤집기
        center.y = 1f - center.y;
        float yMin = 1f - uvMax.y;
        float yMax = 1f - uvMin.y;
        uvMin.y = yMin;
        uvMax.y = yMax;
    }

    private Ray InferenceToWorldRay(Vector2 uv, Pose camPose)
    {
        // Inference to Viewport
        // 정규화된 0~1 좌표를 픽셀로 변환
        // uv = uvCenter인데, 넣는 이유: 그 박스를 대표하는 한 점을 world space에서 얻기 위해서 
        float canvasCenterX = uv.x * imageW;
        float canvasCenterY = uv.y * imageH;

        // Viewport to Local Space 
        // fx, fy, cx, cy는 camera intrinsics 값
        float x = (canvasCenterX - cx) / fx;
        float y = (canvasCenterY - cy) / fy;

        // Local Space to World Space
        Vector3 dirCamera = new Vector3(x, y, 1f).normalized; 
        // -x, y, -1: 카메라 forward / pinhole 모델 좌우축 정의에 따라 달라질 수도 있음 
        Vector3 dirWorld = camPose.rotation * dirCamera;
        // 카메라 로컬 방향을 카메라 회전으로 돌려서, 월드 방향으로 변환

        return new Ray(camPose.position, dirWorld);
        // ray의 원점은 world space 상 카메라 위치, 방향은 월드 방향 
    }

    // =========================
    // Depth (Physics)
    // =========================
    private bool TryPhysicsRaycast(Ray ray, out RaycastHit hit)
        => Physics.Raycast(ray, out hit, maxDistance);//, physicsMask);

    // =========================
    // Size estimation
    // =========================
    private bool TryComputeSizeMeters(Vector2 uvMin, Vector2 uvMax, Pose eyePose, Vector3 eyePos, Vector3 worldCenter,
                                      out float width, out float height)
    {
        width = height = 0f;

        Vector3 normalEyeToCenter = (worldCenter - eyePos).normalized;
        Plane plane = new Plane(normalEyeToCenter, worldCenter);

        Ray minRay = InferenceToWorldRay(uvMin, eyePose);
        Ray maxRay = InferenceToWorldRay(uvMax, eyePose);

        if (!plane.Raycast(minRay, out float tMin) || !plane.Raycast(maxRay, out float tMax))
            return false;

        Vector3 pMin = minRay.GetPoint(tMin);
        Vector3 pMax = maxRay.GetPoint(tMax);

        Quaternion eyeRot = eyePose.rotation;
        Vector3 pMinLocal = Quaternion.Inverse(eyeRot) * (pMin - eyePos);
        Vector3 pMaxLocal = Quaternion.Inverse(eyeRot) * (pMax - eyePos);

        width = Mathf.Abs(pMaxLocal.x - pMinLocal.x);
        height = Mathf.Abs(pMaxLocal.y - pMinLocal.y);
        return true;
    }

    // =========================
    // Visual update
    // =========================
    private void UpdateTransform(BoxItem item, Vector3 worldCenter, Vector3 eyePos, Vector3 eyeUp)
    {
        Vector3 forwardToEye = (eyePos - worldCenter).normalized;
        if (invertForward) forwardToEye = -forwardToEye;
        item.root.SetPositionAndRotation(worldCenter, Quaternion.LookRotation(forwardToEye, eyeUp));
        item.root.localScale = Vector3.one;
    }

    private void UpdateOutline(BoxItem item, float width, float height)
    {
        if (item.lr == null) return;

        if (!drawOutline)
        {
            item.lr.enabled = false;
            return;
        }

        item.lr.enabled = true;
        item.lr.startWidth = outlineWidth;
        item.lr.endWidth = outlineWidth;
        SetOutlineRectLocal(item.lr, width, height);
    }

    private void UpdateLabel(BoxItem item, ObjectDetector.Detection d, Vector3 worldCenter, Vector3 eyePos, Vector3 eyeUp)
    {
        if (item.label == null) return;

        item.label.enabled = showLabel;
        if (!showLabel) return;

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
        else item.labelRoot.localScale = Vector3.one;

        item.labelRoot.rotation = labelFaceEye
            ? Quaternion.LookRotation(item.labelRoot.position - eyePos, eyeUp)
            : item.root.rotation;
    }

    // =========================
    // Pool / lifetime
    // =========================
    private void MarkAllUnseen()
    {
        for (int i = 0; i < _active.Count; i++)
            _active[i].lastSeen = -Mathf.Abs(_active[i].lastSeen);
    }

    private void CleanupExpired()
    {
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
    }

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

    // 디버그용
    [System.Serializable]
    public struct DebugRayItem
    {
        public Ray ray;
        public bool hasHit;
        public Pose hitPose;
        public int classId;
        public float score;
    }

    [System.Serializable]
    public struct DebugSnapshot
    {
        public Pose leftEyePose;
        public Pose mainCamPose;
        public List<DebugRayItem> rays;
    }

    public bool TryBuildDebugSnapshot(out DebugSnapshot snapshot)
    {
        snapshot = default;

        var dets = detector.Detections;
        if (dets == null || dets.Count == 0) return false;

        Pose eyePose = detector.getCachedCameraPose(); // 네 최신 코드에서 쓰는 eyePose
        snapshot.leftEyePose = eyePose;
        snapshot.mainCamPose = new Pose(xrCamera.transform.position, xrCamera.transform.rotation);

        int count = Mathf.Min(dets.Count, 30); // maxBoxes가 없으니 일단 30. 원하면 serialize로 빼
        snapshot.rays = new List<DebugRayItem>(count);

        for (int i = 0; i < count; i++)
        {
            var d = dets[i];

            // 1) inference -> uvCenter
            Vector4 b = d.box;
            GetInferenceFromBox(b, flipY, out var uvCenter, out _, out _);

            // 2) uvCenter -> world ray
            Ray centerRay = InferenceToWorldRay(uvCenter, eyePose);

            // 3) physics hit
            bool hasHit = TryPhysicsRaycast(centerRay, out var hit);

            snapshot.rays.Add(new DebugRayItem
            {
                ray = centerRay,
                hasHit = hasHit,
                hitPose = hasHit ? new Pose(hit.point, Quaternion.identity) : default, // physics는 pose가 없어서 point만
                classId = d.classId,
                score = d.score
            });
        }

        return snapshot.rays.Count > 0;
    }
}
