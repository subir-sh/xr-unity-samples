using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;

public class Detection3DOverlay_ARFoundation_LeftEye : MonoBehaviour
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

    private readonly List<ARRaycastHit> _hits = new();
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

    private void LateUpdate()
    {
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
            //if (detector.ClassName(d.classId) == "tvmonitor") continue; // FOR DEBUGGING

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

            Vector2 pxCenter = new Vector2(uvCenter.x * imageW, uvCenter.y * imageH);
            Ray centerRay = PixelToWorldRay_FromIntrinsics(pxCenter, eyePose);

            if (!TryARRaycast(centerRay, out var hit))
            {
                miss++;
                continue;
            }
            hits++;

            if (hit.distance <= 0.0001f) continue;

            // hit point is the best "ground truth" for placement
            Vector3 worldCenter = hit.pose.position;

            Vector3 normalEyeToCenter = (worldCenter - eyePos).normalized;
            Plane plane = new Plane(normalEyeToCenter, worldCenter);

            Vector2 pxMin = new Vector2(uvMin.x * imageW, uvMin.y * imageH);
            Vector2 pxMax = new Vector2(uvMax.x * imageW, uvMax.y * imageH);

            Ray minRay = PixelToWorldRay_FromIntrinsics(pxMin, eyePose);
            Ray maxRay = PixelToWorldRay_FromIntrinsics(pxMax, eyePose);

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

    private Ray PixelToWorldRay_FromIntrinsics(Vector2 pixel, Pose camPose)
    {
        // pixel: (0..W, 0..H) with top-left origin
        float u = pixel.x;
        float v = pixel.y;

        // If your YOLO coords are already flipped earlier, keep this false.
        // Here we assume pixel is already in the same orientation as intrinsics.
        // (You currently flip uv in code. So DON'T flip again here.)
        // If you later move flipY logic, adjust accordingly.
        // if (flipY) v = (imageH - 1) - v;

        float x = (u - cx) / Mathf.Max(1e-6f, fx);
        float y = (v - cy) / Mathf.Max(1e-6f, fy);

        Vector3 dirCam = new Vector3(-x, y, -1f).normalized;
        Vector3 dirWorld = camPose.rotation * dirCam;

        return new Ray(camPose.position, dirWorld);
    }

    [System.Serializable]
    public struct DebugRayItem
    {
        public Ray ray;
        public bool hasHit;
        public Pose hitPose;
        public TrackableType hitType;
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
        int count = Mathf.Min(dets.Count, maxBoxes);
        if (count <= 0) return false;

        Pose eyePose = GetEyePose(xrCamera, Camera.StereoscopicEye.Left);

        snapshot.leftEyePose = eyePose;
        snapshot.mainCamPose = new Pose(xrCamera.transform.position, xrCamera.transform.rotation);
        snapshot.rays = new List<DebugRayItem>(count);

        for (int i = 0; i < count; i++)
        {
            var d = dets[i];

            // (옵션) tvmonitor는 스킵하고 싶으면 여기 한 줄
            // if (detector.ClassName(d.classId) == "tvmonitor") continue;

            Vector4 b = d.box;
            Vector2 uvCenter = new Vector2((b.x + b.z) * 0.5f, (b.y + b.w) * 0.5f);
            if (flipY) uvCenter.y = 1f - uvCenter.y;

            Vector2 pxCenter = new Vector2(uvCenter.x * imageW, uvCenter.y * imageH);
            Ray r = PixelToWorldRay_FromIntrinsics(pxCenter, eyePose);

            bool hasHit = TryARRaycast(r, out var hit);

            snapshot.rays.Add(new DebugRayItem
            {
                ray = r,
                hasHit = hasHit,
                hitPose = hasHit ? hit.pose : default,
                hitType = hasHit ? hit.hitType : 0,
                classId = d.classId,
                score = d.score
            });
        }

        return snapshot.rays.Count > 0;
    }
}
