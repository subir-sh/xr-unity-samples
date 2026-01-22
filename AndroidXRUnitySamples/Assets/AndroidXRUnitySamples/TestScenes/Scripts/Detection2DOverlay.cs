using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class DetectionOverlay2D : MonoBehaviour
{
    [Header("Sources")]
    [SerializeField] private SentisYoloDetector detector;
    [SerializeField] private RawImage targetRawImage;

    [Header("Overlay")]
    [Tooltip("RectTransform that is aligned to the RawImage area. Boxes will be created under this.")]
    [SerializeField] private RectTransform overlayRoot;

    [Tooltip("A UI prefab with an Image (outline) and optional TMP Text child.")]
    [SerializeField] private RectTransform boxPrefab;

    [Header("Behavior")]
    [SerializeField] private int maxBoxesKnown = 30;
    [SerializeField] private bool showLabelText = true;
    [SerializeField] private float persistSeconds = 0.25f; // keep boxes briefly even if detection drops

    private readonly List<BoxItem> _active = new();
    private readonly Stack<BoxItem> _pool = new();

    private class BoxItem
    {
        public RectTransform rt;
        public Image outline;
        public TMP_Text label;
        public float lastUpdate;
        public int classId;
    }

    private void Awake()
    {
        if (boxPrefab != null)
            boxPrefab.gameObject.SetActive(false);
    }

    private void LateUpdate()
    {
        if (detector == null || targetRawImage == null || overlayRoot == null || boxPrefab == null)
            return;

        // 1) Overlay root should match RawImage rect (same anchors/pivot)
        SyncOverlayToRawImage();

        // 2) Draw boxes from latest detections
        var dets = detector.Detections;
        int count = Mathf.Min(dets.Count, maxBoxesKnown);

        for (int i = 0; i < count; i++)
        {
            var d = dets[i];
            var item = GetOrCreate(i);
            item.classId = d.classId;

            // normalized xyxy (0..1)
            // NOTE: RawImage UI has origin at bottom-left in rect space,
            // but many detectors output y with top-left origin. In our detector we normalized from model space;
            // if your boxes appear vertically flipped, toggle "flipY" below.
            bool flipY = true; // try true first; if boxes are upside-down, set false.
            var rect = NormalizedToRect(d.box, overlayRoot.rect.size, flipY);

            item.rt.anchoredPosition = rect.center;
            item.rt.sizeDelta = rect.size;

            if (item.label != null)
            {
                item.label.enabled = showLabelText;
                if (showLabelText)
                {
                    string name = detector.ClassName(d.classId);
                    item.label.text = $"{name} {d.score:0.00}";
                }
            }

            item.lastUpdate = Time.unscaledTime;
        }

        // 3) Hide leftovers if fewer detections this frame
        for (int i = count; i < _active.Count; i++)
        {
            // mark as not updated
            _active[i].lastUpdate = 0f;
        }

        // 4) Recycle stale boxes
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var b = _active[i];
            if (b.lastUpdate <= 0f) // not used this frame
            {
                // keep briefly to avoid flicker if wanted
                if (persistSeconds <= 0f || (Time.unscaledTime - b.lastUpdate) > persistSeconds)
                {
                    ReturnToPool(b);
                    _active.RemoveAt(i);
                }
            }
            else if (persistSeconds > 0f && (Time.unscaledTime - b.lastUpdate) > persistSeconds)
            {
                ReturnToPool(b);
                _active.RemoveAt(i);
            }
        }
    }

    private void SyncOverlayToRawImage()
    {
        // Make overlayRoot match the RawImage rect in the UI.
        // Assumption: overlayRoot is a sibling/child of same Canvas, intended to sit on top.
        RectTransform rawRT = targetRawImage.rectTransform;

        overlayRoot.anchorMin = rawRT.anchorMin;
        overlayRoot.anchorMax = rawRT.anchorMax;
        overlayRoot.pivot = rawRT.pivot;
        overlayRoot.anchoredPosition = rawRT.anchoredPosition;
        overlayRoot.sizeDelta = rawRT.sizeDelta;
        overlayRoot.localRotation = rawRT.localRotation;
        overlayRoot.localScale = rawRT.localScale;
    }

    private Rect NormalizedToRect(Vector4 xyxy01, Vector2 containerSize, bool flipY)
    {
        float x1 = xyxy01.x;
        float y1 = xyxy01.y;
        float x2 = xyxy01.z;
        float y2 = xyxy01.w;

        if (flipY)
        {
            // flip around 0.5: y' = 1 - y
            float ny1 = 1f - y2;
            float ny2 = 1f - y1;
            y1 = ny1; y2 = ny2;
        }

        float px1 = (x1 - 0.5f) * containerSize.x;
        float px2 = (x2 - 0.5f) * containerSize.x;
        float py1 = (y1 - 0.5f) * containerSize.y;
        float py2 = (y2 - 0.5f) * containerSize.y;

        float w = Mathf.Abs(px2 - px1);
        float h = Mathf.Abs(py2 - py1);

        Vector2 center = new Vector2((px1 + px2) * 0.5f, (py1 + py2) * 0.5f);
        Vector2 size = new Vector2(w, h);

        return new Rect(center - size * 0.5f, size);
    }

    private BoxItem GetOrCreate(int index)
    {
        while (_active.Count <= index)
        {
            _active.Add(CreateBox());
        }
        var item = _active[index];
        item.rt.gameObject.SetActive(true);
        return item;
    }

    private BoxItem CreateBox()
    {
        BoxItem item;

        if (_pool.Count > 0)
        {
            item = _pool.Pop();
            return item;
        }

        var rt = Instantiate(boxPrefab, overlayRoot);
        rt.gameObject.SetActive(true);

        var outline = rt.GetComponent<Image>();
        TMP_Text label = rt.GetComponentInChildren<TMP_Text>(true);

        // 기본 세팅: stretch 하지 말고 center 기준
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);

        item = new BoxItem { rt = rt, outline = outline, label = label };
        return item;
    }

    private void ReturnToPool(BoxItem item)
    {
        item.rt.gameObject.SetActive(false);
        _pool.Push(item);
    }
}
