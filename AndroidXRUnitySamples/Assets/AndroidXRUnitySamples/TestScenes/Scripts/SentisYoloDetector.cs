using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;

public class SentisYoloDetector : MonoBehaviour
{
    [Header("Model")]
    [SerializeField] private ModelAsset sentisModel;
    [SerializeField] private BackendType backend = BackendType.CPU; // CPU or GPU
    [Tooltip("If true, runs one dummy inference at startup to reduce first-hit hitch.")]
    [SerializeField] private bool prewarmOnStart = true;

    [Tooltip("Minimum time between inferences in seconds (fine control).")]
    [SerializeField] private float minIntervalSeconds = 0.15f;

    [Tooltip("If already running inference, keep only the newest request.")]
    [SerializeField] private bool keepLatestRequestWhileBusy = true;

    [Header("Thresholds")]
    [SerializeField, Range(0f, 1f)] private float scoreThreshold = 0.25f;
    [SerializeField, Range(0f, 1f)] private float iouThreshold = 0.55f;
    [SerializeField] private int maxDetections = 30;

    [Header("Labels (optional)")]
    [SerializeField] private TextAsset labelsTxt;
    private string[] _labels;

    [Header("Output Names (3-output models)")]
    [Tooltip("Logs showed outputs=3 and names like output_0, output_1, output_2. Set them here if different.")]
    [SerializeField] private string boxesOutputName = "output_0";
    [SerializeField] private string classIdsOutputName = "output_1";
    [SerializeField] private string scoresOutputName = "output_2";

    [Header("Debug")]
    [Tooltip("Log at most once every X seconds.")]
    [SerializeField] private float logEverySeconds = 1.0f;

    // --- runtime ---
    private Model _model;
    private Worker _worker;

    private Tensor<float> _input;
    private TextureTransform _texTransform;
    private int _inW, _inH;

    private int _frameCounter = 0;
    private float _lastRunTime = -999f;
    private float _lastLogTime = -999f;

    private bool _isRunning = false;
    private Texture _pendingTexture = null;

    public readonly List<Detection> Detections = new();

    [Serializable]
    public struct Detection
    {
        public int classId;
        public float score;
        /// <summary>Normalized XYXY (0~1): x1,y1,x2,y2</summary>
        public Vector4 box;
    }

    private void Awake()
    {
        if (sentisModel == null)
        {
            Debug.LogError("[Sentis] sentisModel is null.");
            enabled = false;
            return;
        }

        _labels = labelsTxt
            ? labelsTxt.text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            : null;

        _model = ModelLoader.Load(sentisModel);

        // Input typically NCHW: (1, 3, H, W)
        var inShape = _model.inputs[0].shape;
        _inH = inShape.Get(2);
        _inW = inShape.Get(3);

        _input = new Tensor<float>(new TensorShape(1, 3, _inH, _inW));
        _texTransform = new TextureTransform().SetDimensions(_inW, _inH, 3);

        _worker = new Worker(_model, backend);

        Debug.Log($"[Sentis] Model loaded. Input={_model.inputs[0].name} size={_inW}x{_inH} outputs={_model.outputs.Count}");

        if (prewarmOnStart)
            StartCoroutine(PrewarmCoroutine());
    }

    private void OnDestroy()
    {
        try { _worker?.Dispose(); } catch { }
        try { _input?.Dispose(); } catch { }
    }

    /// <summary>
    /// Call this every frame with the latest texture. The detector will throttle internally.
    /// </summary>
    public void SubmitFrame(Texture sourceTexture)
    {
        if (sourceTexture == null) return;

        // Always keep latest
        if (!_isRunning || keepLatestRequestWhileBusy)
            _pendingTexture = sourceTexture;

        // If not running, try to start based on throttle rules
        if (!_isRunning)
            TryStartInferenceIfDue();
    }

    private void Update()
    {
        // In case SubmitFrame isn't called continuously, we still try to run when pending exists.
        if (!_isRunning && _pendingTexture != null)
            TryStartInferenceIfDue();
    }

    private void TryStartInferenceIfDue()
    {
        _frameCounter++;

        if (Time.unscaledTime - _lastRunTime < minIntervalSeconds)
            return;

        // Kick off
        var tex = _pendingTexture;
        _pendingTexture = null;
        if (tex == null) return;

        _isRunning = true;
        _lastRunTime = Time.unscaledTime;
        StartCoroutine(RunInferenceCoroutine(tex));
    }

    private IEnumerator PrewarmCoroutine()
    {
        // dummy tiny texture to load kernels / model path
        Texture2D tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tmp.Apply(false, true);

        // one inference
        yield return RunInferenceCoroutine(tmp, isPrewarm: true);

        Destroy(tmp);
    }

    private IEnumerator RunInferenceCoroutine(Texture sourceTexture, bool isPrewarm = false)
    {
        // 1) texture -> tensor
        TextureConverter.ToTensor(sourceTexture, _input, _texTransform);

        // 2) schedule
        _worker.Schedule(_input);

        // 3) async readback (3-output path)
        // If your model outputs differ, change names or add a fallback mapping.
        var boxesT = _worker.PeekOutput(boxesOutputName) as Tensor<float>;
        var classT = _worker.PeekOutput(classIdsOutputName) as Tensor<int>;
        var scoresT = _worker.PeekOutput(scoresOutputName) as Tensor<float>;

        if (boxesT == null || classT == null || scoresT == null)
        {
            if (!isPrewarm)
            {
                Debug.LogError($"[Sentis] Output tensors not found. " +
                               $"boxes='{boxesOutputName}' class='{classIdsOutputName}' scores='{scoresOutputName}'. " +
                               $"(Check model output names.)");
            }

            _isRunning = false;
            yield break;
        }

        var boxesAwaiter = boxesT.ReadbackAndCloneAsync().GetAwaiter();
        while (!boxesAwaiter.IsCompleted) yield return null;
        using var boxes = boxesAwaiter.GetResult();

        var classAwaiter = classT.ReadbackAndCloneAsync().GetAwaiter();
        while (!classAwaiter.IsCompleted) yield return null;
        using var classIds = classAwaiter.GetResult();

        var scoresAwaiter = scoresT.ReadbackAndCloneAsync().GetAwaiter();
        while (!scoresAwaiter.IsCompleted) yield return null;
        using var scores = scoresAwaiter.GetResult();

        if (!isPrewarm)
        {
            DecodeAndNms(boxes, classIds, scores);
            MaybeLogTop();
        }

        _isRunning = false;

        // If a newer frame arrived while we were busy, run again if due
        if (_pendingTexture != null)
            TryStartInferenceIfDue();
    }

    private void DecodeAndNms(Tensor<float> boxes, Tensor<int> classIds, Tensor<float> scores)
    {
        // boxes expected: [N,4] or [1,N,4]
        // classIds expected: [N] or [1,N]
        // scores expected: [N] or [1,N]
        var raw = ListPool<Detection>.Get();
        raw.Clear();

        int n = GetCount(boxes.shape);
        if (n <= 0)
        {
            Detections.Clear();
            ListPool<Detection>.Release(raw);
            return;
        }

        // gather candidates (score filter)
        for (int i = 0; i < n; i++)
        {
            float s = ReadScore(scores, i);
            if (s < scoreThreshold) continue;

            var b = ReadBoxXYXY(boxes, i);
            b = NormalizeIfNeeded(b);

            int cid = ReadClassId(classIds, i);

            raw.Add(new Detection
            {
                classId = cid,
                score = s,
                box = Clamp01(b)
            });

            if (raw.Count >= maxDetections * 6) break;
        }

        ApplyNms(raw);

        ListPool<Detection>.Release(raw);
    }

    private void ApplyNms(List<Detection> raw)
    {
        Detections.Clear();
        if (raw.Count == 0) return;

        raw.Sort((a, b) => b.score.CompareTo(a.score));

        // reuse suppress array
        bool[] suppressed = ArrayPool<bool>.Get(raw.Count);
        Array.Clear(suppressed, 0, raw.Count);

        for (int i = 0; i < raw.Count; i++)
        {
            if (suppressed[i]) continue;

            var di = raw[i];
            Detections.Add(di);
            if (Detections.Count >= maxDetections) break;

            for (int j = i + 1; j < raw.Count; j++)
            {
                if (suppressed[j]) continue;
                if (IoU(di.box, raw[j].box) > iouThreshold)
                    suppressed[j] = true;
            }
        }

        ArrayPool<bool>.Release(suppressed);
    }

    private void MaybeLogTop()
    {
        if (logEverySeconds <= 0) return;
        if (Time.unscaledTime - _lastLogTime < logEverySeconds) return;

        _lastLogTime = Time.unscaledTime;

        if (Detections.Count == 0)
        {
            Debug.Log("[Sentis] dets=0");
            return;
        }

        var top = Detections[0];
        string name = ClassName(top.classId);
        Debug.Log($"[Sentis] dets={Detections.Count}, top={name} score={top.score:0.00} box={top.box}");
    }

    // ---------- tensor helpers ----------

    private int GetCount(TensorShape s)
    {
        if (s.rank == 2) return s[0];
        if (s.rank == 3) return s[1];
        return 0;
    }

    private Vector4 ReadBoxXYXY(Tensor<float> boxes, int i)
    {
        if (boxes.shape.rank == 2)
            return new Vector4(boxes[i, 0], boxes[i, 1], boxes[i, 2], boxes[i, 3]);

        // [1,N,4]
        return new Vector4(boxes[0, i, 0], boxes[0, i, 1], boxes[0, i, 2], boxes[0, i, 3]);
    }

    private int ReadClassId(Tensor<int> classIds, int i)
    {
        if (classIds.shape.rank == 1) return classIds[i];
        if (classIds.shape.rank == 2) return classIds[0, i];
        return classIds[i, 0];
    }

    private float ReadScore(Tensor<float> scores, int i)
    {
        if (scores.shape.rank == 1) return scores[i];
        if (scores.shape.rank == 2) return scores[0, i];
        return scores[i, 0];
    }

    private Vector4 NormalizeIfNeeded(Vector4 b)
    {
        // If looks like pixels, normalize to 0..1
        if (b.x > 1.5f || b.y > 1.5f || b.z > 1.5f || b.w > 1.5f)
            return new Vector4(b.x / _inW, b.y / _inH, b.z / _inW, b.w / _inH);
        return b;
    }

    private Vector4 Clamp01(Vector4 b)
    {
        return new Vector4(
            Mathf.Clamp01(b.x), Mathf.Clamp01(b.y),
            Mathf.Clamp01(b.z), Mathf.Clamp01(b.w)
        );
    }

    private float IoU(Vector4 a, Vector4 b)
    {
        float x1 = Mathf.Max(a.x, b.x);
        float y1 = Mathf.Max(a.y, b.y);
        float x2 = Mathf.Min(a.z, b.z);
        float y2 = Mathf.Min(a.w, b.w);

        float iw = Mathf.Max(0, x2 - x1);
        float ih = Mathf.Max(0, y2 - y1);
        float inter = iw * ih;

        float areaA = Mathf.Max(0, a.z - a.x) * Mathf.Max(0, a.w - a.y);
        float areaB = Mathf.Max(0, b.z - b.x) * Mathf.Max(0, b.w - b.y);
        float union = areaA + areaB - inter;

        return union <= 1e-6f ? 0f : inter / union;
    }

    public string ClassName(int classId)
    {
        if (_labels == null) return classId.ToString();
        if (classId < 0 || classId >= _labels.Length) return classId.ToString();
        return _labels[classId];
    }

    // ---------- tiny pools to reduce GC ----------

    private static class ListPool<T>
    {
        private static readonly Stack<List<T>> _pool = new();

        public static List<T> Get()
        {
            if (_pool.Count > 0) return _pool.Pop();
            return new List<T>(256);
        }

        public static void Release(List<T> list)
        {
            list.Clear();
            _pool.Push(list);
        }
    }

    private static class ArrayPool<T>
    {
        private static readonly Stack<T[]> _pool = new();

        public static T[] Get(int minLen)
        {
            while (_pool.Count > 0)
            {
                var arr = _pool.Pop();
                if (arr.Length >= minLen) return arr;
            }
            return new T[minLen];
        }

        public static void Release(T[] arr)
        {
            _pool.Push(arr);
        }
    }
}
