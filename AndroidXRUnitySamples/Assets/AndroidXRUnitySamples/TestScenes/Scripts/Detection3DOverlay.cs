using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class Detection3DOverlay : MonoBehaviour
{
    [Header("Inputs")]
    [SerializeField] private SentisYoloDetector detector;
    [SerializeField] private Camera xrCamera;

    [Header("Raycast")]
    [SerializeField] private ARRaycastManager arRaycast;
    [SerializeField] private TrackableType arTrackables =
        TrackableType.Planes | TrackableType.FeaturePoint; // | TrackableType.Depth;

    [SerializeField] private float maxPhysicsDistance = 10f;

    [Header("BBox prefab")]
    [SerializeField] private RectTransform boxPrefab;
    [SerializeField] private Transform contentParent;
    [SerializeField] private float persistSeconds = 0.35f;

    [Header("BBox coordinate conventions")]
    [SerializeField] private bool yIsTopLeft = true;

    [Header("Debug")]
    [SerializeField] private bool debugLogs = true;
    [SerializeField] private bool logPerDetection = false;
    [SerializeField] private float summaryLogEverySeconds = 1.0f;

    private float _lastSummary = -999f;

    private readonly List<BoxData> _drawn = new();
    private readonly List<BoxData> _pool = new();

    private class BoxData
    {
        public int classId;
        public float lastUpdate;
        public RectTransform rt;
    }

    private void Awake()
    {
        if (!xrCamera) xrCamera = Camera.main;
        if (!contentParent && boxPrefab) contentParent = boxPrefab.parent;
        if (boxPrefab) boxPrefab.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (!detector || !xrCamera || !boxPrefab || !contentParent)
        {
            if (debugLogs && Time.unscaledTime - _lastSummary > summaryLogEverySeconds)
            {
                _lastSummary = Time.unscaledTime;
                Debug.Log($"[Noonchi] [Overlay] Missing refs: detector={(detector ? "OK" : "NULL")}, " +
                          $"xrCamera={(xrCamera ? "OK" : "NULL")}, boxPrefab={(boxPrefab ? "OK" : "NULL")}, contentParent={(contentParent ? "OK" : "NULL")}");
            }
            return;
        }

        // 1) cleanup
        int removed = 0;
        for (int i = _drawn.Count - 1; i >= 0; --i)
        {
            float age = Time.time - _drawn[i].lastUpdate;
            if (age > persistSeconds)
            {
                if (logPerDetection && debugLogs)
                    Debug.Log($"[Noonchi] [Overlay] Cleanup: classId={_drawn[i].classId}, age={age:0.00}s -> return to pool");
                ReturnToPool(_drawn[i]);
                _drawn.RemoveAt(i);
                removed++;
            }
        }

        // 2) detections
        var dets = detector.Detections;
        if (dets == null || dets.Count == 0)
        {
            if (debugLogs && Time.unscaledTime - _lastSummary > summaryLogEverySeconds)
            {
                _lastSummary = Time.unscaledTime;
                Debug.Log($"[Noonchi] [Overlay] dets=0 (drawn={_drawn.Count}, pool={_pool.Count}, removed={removed})");
            }
            return;
        }

        if (debugLogs && Time.unscaledTime - _lastSummary > summaryLogEverySeconds)
        {
            _lastSummary = Time.unscaledTime;
            var top = dets[0];
            Debug.Log($"[Noonchi] [Overlay] dets={dets.Count} drawn={_drawn.Count} pool={_pool.Count} removed={removed} " +
                      $"top: classId={top.classId} score={top.score:0.00} box={top.box}");
        }

        // 3) per detection
        for (int i = 0; i < dets.Count; i++)
        {
            var d = dets[i];

            float x1 = d.box.x;
            float y1 = d.box.y;
            float x2 = d.box.z;
            float y2 = d.box.w;

            if (x2 <= x1 || y2 <= y1)
            {
                if (logPerDetection && debugLogs)
                    Debug.Log($"[Noonchi] [Overlay] det#{i} SKIP invalid xyxy: box={d.box}");
                continue;
            }

            var rect = Rect.MinMaxRect(x1, y1, x2, y2);

            Vector2 vc = rect.center;
            if (yIsTopLeft) vc.y = 1f - vc.y;

            if (logPerDetection && debugLogs)
                Debug.Log($"[Noonchi] [Overlay] det#{i} 2D " +
                          $"classId={d.classId} score={d.score:0.00} " +
                          $"xyxy01=({rect.xMin:0.000},{rect.yMin:0.000},{rect.xMax:0.000},{rect.yMax:0.000}) " +
                          $"wh01=({rect.width:0.000},{rect.height:0.000}) " +
                          $"center01=({rect.center.x:0.000},{rect.center.y:0.000}) vc={vc} yIsTopLeft={yIsTopLeft}");

            // 4) raycast center
            if (!TryRaycastViewport(vc, out var hitPos, out var hitNormal, out var hitDistance))
            {
                if (logPerDetection && debugLogs)
                    Debug.Log($"[Noonchi] [Overlay] det#{i} Raycast FAILED vc={vc}");
                continue;
            }

            if (logPerDetection && debugLogs)
            {
                var camPos = xrCamera.transform.position;
                var camFwd = xrCamera.transform.forward;
                var hitVec = (hitPos - camPos);
                float frontDot = Vector3.Dot(camFwd, hitVec.normalized);

                Debug.Log($"[Noonchi] [Overlay] det#{i} Raycast OK " +
                          $"hitPos={hitPos} dist={hitDistance:0.000} normal={hitNormal} " +
                          $"frontDot={frontDot:0.000} hitVec={hitVec}");
            }

            // 5) normRect corners
            Rect normRect = rect;
            if (yIsTopLeft)
                normRect = new Rect(rect.xMin, 1f - rect.yMax, rect.width, rect.height);

            Vector2 vMin = normRect.min;
            Vector2 vMax = normRect.max;

            var centerRay = xrCamera.ViewportPointToRay(normRect.center);
            Vector3 worldCenter = centerRay.GetPoint(hitDistance);

            Vector3 viewDir = (worldCenter - xrCamera.transform.position).normalized;
            var plane = new Plane(viewDir, worldCenter);

            //

            // Build plane basis (u=right on plane, v=up on plane)
            Vector3 camUp = xrCamera.transform.up;
            Vector3 u = Vector3.Cross(camUp, viewDir);
            if (u.sqrMagnitude < 1e-6f)
                u = Vector3.Cross(xrCamera.transform.right, viewDir);
            u.Normalize();
            Vector3 v = Vector3.Cross(viewDir, u).normalized;

            // 4 corners in viewport (0..1, bottom-left origin)
            Vector2 c0 = new Vector2(normRect.xMin, normRect.yMin);
            Vector2 c1 = new Vector2(normRect.xMax, normRect.yMin);
            Vector2 c2 = new Vector2(normRect.xMin, normRect.yMax);
            Vector2 c3 = new Vector2(normRect.xMax, normRect.yMax);

            // Intersect each corner ray with the plane, then project onto (u,v)
            float minU = float.PositiveInfinity, maxU = float.NegativeInfinity;
            float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;

            Vector3 worldMin = Vector3.zero, worldMax = Vector3.zero; // (debug용)
            bool gotAny = false;

            Vector2[] corners = { c0, c1, c2, c3 };
            for (int k = 0; k < 4; k++)
            {
                var r = xrCamera.ViewportPointToRay(corners[k]);
                if (!plane.Raycast(r, out float t)) continue;

                Vector3 p = r.GetPoint(t);
                Vector3 dis = p - worldCenter;

                float pu = Vector3.Dot(dis, u);
                float pv = Vector3.Dot(dis, v);

                if (!gotAny) { worldMin = p; worldMax = p; gotAny = true; }
                else
                {
                    // 그냥 로그용으로 대충 min/max 업데이트
                    worldMin = new Vector3(Mathf.Min(worldMin.x, p.x), Mathf.Min(worldMin.y, p.y), Mathf.Min(worldMin.z, p.z));
                    worldMax = new Vector3(Mathf.Max(worldMax.x, p.x), Mathf.Max(worldMax.y, p.y), Mathf.Max(worldMax.z, p.z));
                }

                if (pu < minU) minU = pu;
                if (pu > maxU) maxU = pu;
                if (pv < minV) minV = pv;
                if (pv > maxV) maxV = pv;
            }

            if (!gotAny)
            {
                if (logPerDetection && debugLogs)
                    Debug.Log($"[Noonchi] [Overlay] det#{i} Plane corner intersections FAILED (no valid corner hits).");
                continue;
            }

            Vector2 size = new Vector2(Mathf.Abs(maxU - minU), Mathf.Abs(maxV - minV));

            /*var minRay = xrCamera.ViewportPointToRay(vMin);
            var maxRay = xrCamera.ViewportPointToRay(vMax);

            if (!plane.Raycast(minRay, out float tMin))
            {
                if (logPerDetection && debugLogs)
                    Debug.Log($"[Noonchi] [Overlay] det#{i} Plane.Raycast(minRay) FAILED vMin={vMin}");
                continue;
            }
            if (!plane.Raycast(maxRay, out float tMax))
            {
                if (logPerDetection && debugLogs)
                    Debug.Log($"[Noonchi] [Overlay] det#{i} Plane.Raycast(maxRay) FAILED vMax={vMax}");
                continue;
            }

            Vector3 worldMin = minRay.GetPoint(tMin);
            Vector3 worldMax = maxRay.GetPoint(tMax);

            Vector3 localMin = Quaternion.Inverse(xrCamera.transform.rotation) * (worldMin - xrCamera.transform.position);
            Vector3 localMax = Quaternion.Inverse(xrCamera.transform.rotation) * (worldMax - xrCamera.transform.position);

            Vector2 size = new Vector2(
                Mathf.Abs(localMax.x - localMin.x),
                Mathf.Abs(localMax.y - localMin.y)
            );*/

            if (float.IsNaN(size.x) || float.IsNaN(size.y) || size.x <= 0f || size.y <= 0f)
            {
                if (logPerDetection && debugLogs)
                   //Debug.Log($"[Noonchi] [Overlay] det#{i} INVALID size={size} localMin={localMin} localMax={localMax} tMin={tMin} tMax={tMax}");
                continue;
            }

            if (logPerDetection && debugLogs)
            {
                float distCenter = Vector3.Distance(xrCamera.transform.position, worldCenter);
                Debug.Log($"[Noonchi] [Overlay] det#{i} 3D " +
                          $"worldCenter={worldCenter} distCenter={distCenter:0.000} " +
                          $"worldMin={worldMin} worldMax={worldMax} " +
                          $"sizeXY(world)={size} " +
                          $"viewDir={viewDir} vMin={vMin} vMax={vMax}");
            }

            // 6) create/reuse box
            var box = GetOrCreate(d.classId);
            box.lastUpdate = Time.time;

            // 7) apply transform + size
            box.rt.SetPositionAndRotation(worldCenter, Quaternion.LookRotation(viewDir));
            box.rt.sizeDelta = size;

            // 8) validate visual components on instantiated box (Image visible?)
            if (logPerDetection && debugLogs)
            {
                var img = box.rt.GetComponent<Image>();
                var tmp = box.rt.GetComponentInChildren<TMP_Text>(true);

                Debug.Log($"[Noonchi] [Overlay] det#{i} PLACE boxGO={box.rt.name} active={box.rt.gameObject.activeInHierarchy} " +
                          $"pos={box.rt.position} sizeDelta={box.rt.sizeDelta} hasImage={(img ? "YES" : "NO")} hasTMP={(tmp ? "YES" : "NO")}");

                if (img)
                {
                    Debug.Log($"[Noonchi] [Overlay] det#{i} Image state: enabled={img.enabled} color={img.color} sprite={(img.sprite ? img.sprite.name : "NULL")}");
                    if (img.color.a <= 0.01f)
                        Debug.Log($"[Noonchi] [Overlay] det#{i} Image alpha is ~0 => invisible.");
                }
                else
                {
                    Debug.Log($"[Noonchi] [Overlay] det#{i} WARNING: No Image component on box root. If Image is on a child, move it to root or fetch child Image.");
                }
            }

            // 9) set text
            var uiText = box.rt.GetComponentInChildren<TMP_Text>(true);
            if (uiText)
                uiText.text = $"{detector.ClassName(d.classId)} {d.score:0.00}";
            else if (logPerDetection && debugLogs)
                Debug.Log($"[Noonchi] [Overlay] det#{i} WARNING: TMP_Text not found in box prefab instance.");
        }
    }

    private bool TryRaycastViewport(Vector2 viewport01, out Vector3 pos, out Vector3 normal, out float distance)
    {
        if (!arRaycast)
        {
            if (debugLogs && logPerDetection)
                Debug.Log("[Noonchi] [Overlay] Raycast: arRaycast is NULL");
            pos = default; normal = default; distance = 0f;
            return false;
        }

        var screen = (Vector2)xrCamera.ViewportToScreenPoint(new Vector3(viewport01.x, viewport01.y, 0));
        var hits = new List<ARRaycastHit>(8);

        bool ok = arRaycast.Raycast(screen, hits, arTrackables);

        if (debugLogs && logPerDetection)
            Debug.Log($"[Noonchi] [Overlay] Raycast: viewport={viewport01} screen={screen} ok={ok} hits={hits.Count} trackables={arTrackables}");

        if (ok && hits.Count > 0)
        {
            // Pick closest hit (min hit.distance)
            int best = 0;
            float bestDist = float.PositiveInfinity;

            for (int i = 0; i < hits.Count; i++)
            {
                // ARRaycastHit.distance는 screen-ray 상 거리
                float d = hits[i].distance;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }

            var h = hits[best];
            pos = h.pose.position;
            normal = h.pose.up;
            distance = bestDist; // <= Vector3.Distance보다 일관적

            if (debugLogs && logPerDetection)
                Debug.Log($"[Noonchi] [Overlay] Raycast HIT(best): idx={best}/{hits.Count} dist={distance:0.000} pos={pos} normal={normal}");

            return true;
        }

        pos = default;
        normal = default;
        distance = 0f;
        return false;
    }

    private BoxData GetOrCreate(int classId)
    {
        for (int i = _drawn.Count - 1; i >= 0; --i)
        {
            if (_drawn[i].classId == classId)
            {
                if (debugLogs && logPerDetection)
                    Debug.Log($"[Noonchi] [Overlay] Reuse existing box for classId={classId} go={_drawn[i].rt.name}");
                return _drawn[i];
            }
        }

        BoxData data;
        if (_pool.Count > 0)
        {
            data = _pool[_pool.Count - 1];
            _pool.RemoveAt(_pool.Count - 1);
            data.rt.gameObject.SetActive(true);

            if (debugLogs && logPerDetection)
                Debug.Log($"[Noonchi] [Overlay] Pool pop classId={classId} go={data.rt.name} poolNow={_pool.Count}");
        }
        else
        {
            var rt = Instantiate(boxPrefab, contentParent);
            rt.gameObject.SetActive(true);
            data = new BoxData { rt = rt };

            if (debugLogs)
                Debug.Log($"[Noonchi] [Overlay] Instantiate new box for classId={classId} go={rt.name} parent={contentParent.name}");
        }

        data.classId = classId;
        data.lastUpdate = Time.time;
        _drawn.Add(data);
        return data;
    }

    private void ReturnToPool(BoxData b)
    {
        if (debugLogs && logPerDetection)
            Debug.Log($"[Noonchi] [Overlay] ReturnToPool classId={b.classId} go={b.rt.name}");

        b.rt.gameObject.SetActive(false);
        _pool.Add(b);
    }
}
