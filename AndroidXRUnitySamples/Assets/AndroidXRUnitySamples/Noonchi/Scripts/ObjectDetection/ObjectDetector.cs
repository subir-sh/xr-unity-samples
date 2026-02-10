using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;

public class ObjectDetector : MonoBehaviour
{
    [Header("Model")]
    [SerializeField] private ModelAsset sentisModel;
    [SerializeField] private BackendType backend = BackendType.GPUCompute; // CPU or GPU

    [Tooltip("Minimum time between inferences in seconds (fine control).")]
    [SerializeField] private float minIntervalSeconds = 0.15f;
    private float _lastRunTime = -999f;

    [Tooltip("If already running inference, keep only the newest request.")]
    [SerializeField] private bool keepLatestRequestWhileBusy = true;

    [Header("Thresholds")]
    [SerializeField, Range(0f, 1f)] private float scoreThreshold = 0.25f;
    [SerializeField, Range(0f, 1f)] private float iouThreshold = 0.55f;

    [Header("Labels")]
    [SerializeField] private TextAsset labelsTxt;
    private string[] _labels;

    [Header("Output Names (3-output models)")]
    [Tooltip("Logs showed outputs=3 and names like output_0, output_1, output_2. Set them here if different.")]
    [SerializeField] private string boxesOutputName = "output_0";
    [SerializeField] private string classIdsOutputName = "output_1";
    [SerializeField] private string scoresOutputName = "output_2";

    // --- runtime ---
    private Model _model;
    private Worker _worker;

    private Tensor<float> _input;
    private TextureTransform _texTransform;
    private int _inW, _inH;

    private bool _isRunning = false;
    private bool _prewarming = true;
    private Texture _pendingTexture = null;

    private Pose cachedCameraPose;
    private Pose _pendingCameraPose;
    public readonly List<Detection> Detections = new();

    [Serializable]
    public struct Detection
    {
        public int classId;
        public float score;
        /// <summary>Normalized XYXY (0~1): x1,y1,x2,y2</summary>
        public Vector4 box;
    }

    public Pose getCachedCameraPose() { return cachedCameraPose; }

    private void Awake()
    {
        if (sentisModel == null)
        {
            enabled = false;
            return;
        }

        _labels = labelsTxt
            ? labelsTxt.text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            : null;

        _model = ModelLoader.Load(sentisModel);

        // Input typically NCHW: (1, 3, H, W)
        // model size = W * H
        var inShape = _model.inputs[0].shape;
        _inH = inShape.Get(2);
        _inW = inShape.Get(3);

        _input = new Tensor<float>(new TensorShape(1, 3, _inH, _inW));
        _texTransform = new TextureTransform().SetDimensions(_inW, _inH, 3);

        _worker = new Worker(_model, backend);

        StartCoroutine(PrewarmCoroutine());
    }

    private void OnDestroy()
    {
        try { _worker?.Dispose(); } catch { }
        try { _input?.Dispose(); } catch { }
    } 

    // CameraCapture에서 이미지 받아오기 
    public void SubmitFrame(Texture sourceTexture, Pose cameraPose)
    {
        if (_prewarming) return;
        if (sourceTexture == null) return;

        // Always keep latest
        if (!_isRunning || keepLatestRequestWhileBusy) 
        {
            _pendingTexture = sourceTexture;
            _pendingCameraPose = cameraPose;
        }

        // If not running, try to start based on throttle rules
        if (!_isRunning)
        {
            TryStartInferenceIfDue();
        }
    }

    private void TryStartInferenceIfDue()
    {
        if (Time.unscaledTime - _lastRunTime < minIntervalSeconds) return;

        // Kick off
        var tex = _pendingTexture;
        var pose = _pendingCameraPose;
        _pendingTexture = null;
        if (tex == null) return;

        cachedCameraPose = pose;
        _isRunning = true;
        _lastRunTime = Time.unscaledTime;
        StartCoroutine(RunInferenceCoroutine(tex));
    }

    // 모델 prewarm: 더미 텍스처를 만들어서, kernel/model path 로드하게 추론 한 번 돌리고 삭제
    private IEnumerator PrewarmCoroutine()
    {
        _prewarming = true;
        Texture2D tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tmp.Apply(false, true);
        yield return RunInferenceCoroutine(tmp, isPrewarm: true);
        Destroy(tmp);
        _prewarming = false;
    }

    // 실제로 "추론"하는 함수
    private IEnumerator RunInferenceCoroutine(Texture sourceTexture, bool isPrewarm = false)
    {
        // 1) texture -> tensor
        TextureConverter.ToTensor(sourceTexture, _input, _texTransform);

        // 2) schedule
        _worker.Schedule(_input);

        // 3) async readback (3-output path)
        var boxesT = _worker.PeekOutput(boxesOutputName) as Tensor<float>;
        var classT = _worker.PeekOutput(classIdsOutputName) as Tensor<int>;
        var scoresT = _worker.PeekOutput(scoresOutputName) as Tensor<float>;

        // 모델 아웃풋 이름이 다를 수 있음. 일단 메타 퀘스트 코드에서 구워진 이름 그대로 사용 중
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

        if (!isPrewarm) DecodeAndNms(boxes, classIds, scores);

        _isRunning = false;

        // 만약 추론 중 새로운 프레임이 들어왔으면, 그 프레임에 대한 추론 invoke
        if (_pendingTexture != null) TryStartInferenceIfDue();
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

            for (int j = i + 1; j < raw.Count; j++)
            {
                if (suppressed[j]) continue;
                if (IoU(di.box, raw[j].box) > iouThreshold)
                    suppressed[j] = true;
            }
        }

        ArrayPool<bool>.Release(suppressed);
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
        // Vector 4 --> (topLeftX, topLeftY, bottomRightX, bottomRightY)
        // 교차 좌표 계산
        float x1 = Mathf.Max(a.x, b.x);
        float y1 = Mathf.Max(a.y, b.y);
        float x2 = Mathf.Min(a.z, b.z);
        float y2 = Mathf.Min(a.w, b.w);

        // 교차 영역 계산
        float iw = Mathf.Max(0, x2 - x1);
        float ih = Mathf.Max(0, y2 - y1);
        float inter = iw * ih;

        // 각 박스의 영역
        float areaA = Mathf.Max(0, a.z - a.x) * Mathf.Max(0, a.w - a.y);
        float areaB = Mathf.Max(0, b.z - b.x) * Mathf.Max(0, b.w - b.y);
        // Union 계산
        float union = areaA + areaB - inter;

        // Union이 0이면 0, 아니면 Intersection over Union 반환
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
