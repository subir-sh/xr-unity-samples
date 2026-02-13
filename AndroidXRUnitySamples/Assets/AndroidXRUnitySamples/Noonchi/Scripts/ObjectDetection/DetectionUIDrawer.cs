using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class DetectionUIDrawer : MonoBehaviour
{
    [Header("Sources")]
    [SerializeField] private ObjectDetector detector;
    [SerializeField] private Camera xrCamera; // 유지(라벨 face eye 등)

    [Header("Config")]
    [SerializeField] private DetectionUIDrawerSettings drawerSettings;
    [SerializeField] private CameraIntrinsics intrinsics;

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
        GetInferenceFromBox(b, drawerSettings.flipY, out var uvCenter, out var uvMin, out var uvMax);

        // 2) viewport point -> world ray 
        Ray centerRay = InferenceToWorldRay(uvCenter, eyePose);

        // 3) 만들어진 ray 쏘기 -> physics hit 판정
        if (!TryPhysicsRaycast(centerRay, out var hit)) return false;
        Vector3 worldCenter = hit.point;

        // 4) hit 된 지점의 depth에 view-facing plane을 만들고, bbox 가로/세로 근사하여 view-facing UI 박스 준비
        if (!TryComputeSizeMeters(uvMin, uvMax, eyePose, eyePos, worldCenter, out float width, out float height)) return false;

        if (drawerSettings.clampMinSize)
        {  
            // 작게 그려진 박스들 제거 
            width = Mathf.Max(width, drawerSettings.minSizeMeters);
            height = Mathf.Max(height, drawerSettings.minSizeMeters);
        }

        // 5) 실제로 UI overlay 그리고, 업데이트하기
        var item = GetOrCreate(index); // index에 해당하는 BoxItem을 가져오거나, 새로 생성
        item.lastSeen = Time.unscaledTime; // 이번 프레임에 갱신됐는지 여부 (CleanupExpired를 위해)

        UpdateTransform(item, worldCenter, eyePos, eyeUp); // 월드 위치 고정 + 눈을 향하도록 회전
        UpdateOutline(item, width, height); // 계산된 크기로 3D box 크기 갱신
        UpdateLabel(item, d, worldCenter, eyePos, eyeUp); // class, score text + label 위치, 스케일, 방향 갱신

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
        float canvasCenterX = uv.x * intrinsics.imageW;
        float canvasCenterY = uv.y * intrinsics.imageH;

        // Viewport to Local Space 
        // fx, fy, cx, cy는 camera intrinsics 값
        float x = (canvasCenterX - intrinsics.cx) / intrinsics.fx;
        float y = (canvasCenterY - intrinsics.cy) / intrinsics.fy;

        // Local Space to World Space
        Vector3 dirCamera = new Vector3(x, y, 1f).normalized; 
        // -x, y, -1: 카메라 forward / pinhole 모델 좌우축 정의에 따라 달라질 수도 있음 
        Vector3 dirWorld = camPose.rotation * dirCamera;
        // 카메라 로컬 방향을 카메라 회전으로 돌려서, 월드 방향으로 변환

        return new Ray(camPose.position, dirWorld);
        // ray의 원점은 world space 상 카메라 위치, 방향은 월드 방향 
    }

    // =========================
    // Depth (Physics Raycast)
    // =========================
    private bool TryPhysicsRaycast(Ray ray, out RaycastHit hit)
        => Physics.Raycast(ray, out hit, drawerSettings.maxDistance);//, physicsMask);

    // =========================
    // Bbox Size estimation
    // =========================
    private bool TryComputeSizeMeters(Vector2 uvMin, Vector2 uvMax, Pose eyePose, Vector3 eyePos, Vector3 worldCenter,
                                      out float width, out float height)
    {
        width = height = 0f;

        // TryPhysicsRaycast에서 만들어진 hit을 local world center로 변환하고, 그것을 지나는 view-facing plane을 만듦
        Vector3 normalEyeToCenter = (worldCenter - eyePos).normalized;
        Plane plane = new Plane(normalEyeToCenter, worldCenter);

        // 2D Image 좌표계의 bbox의 두 꼭지점(uvMin, uvMax) 또한 InferenceToWorldRay로 world ray화
        Ray minRay = InferenceToWorldRay(uvMin, eyePose);
        Ray maxRay = InferenceToWorldRay(uvMax, eyePose);

        // 두 꼭지점 ray를 hit center plane과 교차 (physics raycast 아님)
        if (!plane.Raycast(minRay, out float tMin) || !plane.Raycast(maxRay, out float tMax))
            return false;

        Vector3 pMin = minRay.GetPoint(tMin);
        Vector3 pMax = maxRay.GetPoint(tMax);

        // 교차된 두 점(pMin, pMax)을 eye 기준 로컬로 바꿔서 두 점의 x, y 차이를 기반으로 bbox 가로/세로 근사
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
        // UI를 world center에 두고, eye로 향하게 한 뒤, TryComputeSizeMeters에서 근사된 크기 적용 
        Vector3 forwardToEye = (eyePos - worldCenter).normalized;
        if (drawerSettings.invertForward) forwardToEye = -forwardToEye;
        item.root.SetPositionAndRotation(worldCenter, Quaternion.LookRotation(forwardToEye, eyeUp));
        item.root.localScale = Vector3.one;
    }

    private void UpdateOutline(BoxItem item, float width, float height)
    {
        if (item.lr == null) return;

        if (!drawerSettings.drawOutline)
        {
            item.lr.enabled = false;
            return;
        }

        item.lr.enabled = true;
        item.lr.startWidth = drawerSettings.outlineWidth;
        item.lr.endWidth = drawerSettings.outlineWidth;
        //SetOutlineRectLocal(item.lr, width, height); // --> bbox 전체 사각형 그리기
        SetOutlineEllipseLocal(item.lr, width, height); // --> bbox의 width, height를 지름으로 하는 타원 그리기
    }

    private void UpdateLabel(BoxItem item, ObjectDetector.Detection d, Vector3 worldCenter, Vector3 eyePos, Vector3 eyeUp)
    {
        if (item.label == null) return;

        item.label.enabled = drawerSettings.showLabel;
        if (!drawerSettings.showLabel) return;

        item.label.text = $"{detector.ClassName(d.classId)} {d.score:0.00}";
        item.label.fontSize = drawerSettings.labelFontSize;

        item.labelRoot.localPosition = new Vector3(0f, 0f, +drawerSettings.labelDepthOffset);

        if (drawerSettings.stabilizeLabelSize)
        {
            float dist = Vector3.Distance(eyePos, worldCenter);
            float s = (dist / Mathf.Max(0.0001f, drawerSettings.labelRefDistance)) * drawerSettings.labelRefScale;
            s = Mathf.Clamp(s, drawerSettings.labelMinScale, drawerSettings.labelMaxScale);
            item.labelRoot.localScale = Vector3.one * s;
        }
        else item.labelRoot.localScale = Vector3.one;

        item.labelRoot.rotation = drawerSettings.labelFaceEye
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
            if (Time.unscaledTime - last > drawerSettings.persistSeconds)
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

    // 타원 그리기 (r1, r2 = width/2, height/2)
    private static void SetOutlineEllipseLocal(
        LineRenderer lr, float width, float height, 
        int segments = 48,
        float padding = 0.12f // 0~1: 비율 패딩 (0.12f: 12% 작게 만들기)
    )
    {
        if (lr == null) return;

        float rx = width * 0.5f;
        float ry = height * 0.5f;

        rx *= (1f - padding);
        ry *= (1f - padding);
        rx = Mathf.Max(0.0001f, rx);
        ry = Mathf.Max(0.0001f, ry);

        segments = Mathf.Clamp(segments, 12, 256);

        lr.loop = true;
        lr.useWorldSpace = false;
        lr.positionCount = segments;

        float step = (Mathf.PI * 2f) / segments;
        for (int i = 0; i < segments; i++)
        {
            float a = i * step;
            float x = Mathf.Cos(a) * rx;
            float y = Mathf.Sin(a) * ry;
            lr.SetPosition(i, new Vector3(x, y, 0f));
        }
    }
}
