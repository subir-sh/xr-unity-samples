using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using TMPro;

public class NoonchiGazeHUD : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private XRInteractorReticleVisual reticleVisual;
    [SerializeField] private Camera screenCamera;
    [SerializeField] private TextMeshProUGUI text;

    [Header("Pinch Action (log only when performed)")]
    [SerializeField] private InputActionProperty pinchAction;

    [Header("One-time logs")]
    [SerializeField] private bool logCanvasInfoOnce = true;
    [SerializeField] private bool logHitBasisOnce = true;

    // XRI private fields
    private FieldInfo _fiTargetEndPoint;
    private FieldInfo _fiHasRaycastHit;
    private FieldInfo _fiInteractor;

    // Once-only guards
    private bool _didLogCanvasInfo;
    private bool _didLogHitBasis;

    void Awake()
    {
        if (screenCamera == null) screenCamera = Camera.main;
        if (reticleVisual == null) reticleVisual = FindObjectOfType<XRInteractorReticleVisual>(true);

        var t = typeof(XRInteractorReticleVisual);
        _fiTargetEndPoint = t.GetField("m_TargetEndPoint", BindingFlags.Instance | BindingFlags.NonPublic);
        _fiHasRaycastHit  = t.GetField("m_HasRaycastHit",  BindingFlags.Instance | BindingFlags.NonPublic);
        _fiInteractor     = t.GetField("m_Interactor",     BindingFlags.Instance | BindingFlags.NonPublic);
    }

    void Start()
    {
        if (logCanvasInfoOnce && !_didLogCanvasInfo)
        {
            _didLogCanvasInfo = true;
            PrintWorldSpaceCanvasInfoOnce();
        }

        Debug.Log($"[Noonchi] Screen: {Screen.width}x{Screen.height} (0,0=bottom-left)");
    }

    void OnEnable()
    {
        if (pinchAction.action != null)
        {
            pinchAction.action.Enable();
            pinchAction.action.performed += OnPinchPerformed;
        }
        else
        {
            Debug.Log("[Noonchi] PinchAction is null. Assign InputActionProperty in Inspector.");
        }
    }

    void OnDisable()
    {
        if (pinchAction.action != null)
        {
            pinchAction.action.performed -= OnPinchPerformed;
            pinchAction.action.Disable();
        }
    }

    private void OnPinchPerformed(InputAction.CallbackContext ctx)
    {
        LogOnceNow();
    }

    private void LogOnceNow()
    {
        if (text == null)
        {
            Debug.Log("[Noonchi] HUD text is null");
            return;
        }

        if (reticleVisual == null || _fiTargetEndPoint == null)
        {
            text.text = "[Noonchi] reticleVisual not found";
            Debug.Log("[Noonchi] reticleVisual not found");
            return;
        }

        // Reticle이 실제로 따라가는 최종 3D 포인트(월드)
        var p3 = (Vector3)_fiTargetEndPoint.GetValue(reticleVisual);
        var hasHit = _fiHasRaycastHit != null && (bool)_fiHasRaycastHit.GetValue(reticleVisual);
        var mode = hasHit ? "hit" : "fallback";

        // 2D는 Screen 픽셀
        var sp = (screenCamera != null)
            ? screenCamera.WorldToScreenPoint(p3)
            : new Vector3(float.NaN, float.NaN, float.NaN);

        var msg = $"[Noonchi] [Eyegaze] ({mode}) 3D({p3.x:F4},{p3.y:F4},{p3.z:F4}) 2D({sp.x:F1},{sp.y:F1})";

        text.text = msg;
        Debug.Log(msg);

        // hit 기준점 확인 1회 (hit가 처음 뜨는 순간)
        if (logHitBasisOnce && !_didLogHitBasis && hasHit)
        {
            _didLogHitBasis = true;
            PrintHitBasisOnce(p3);
        }
    }

    // ----------------------------
    // (1) World Space Canvas info
    // ----------------------------
    private void PrintWorldSpaceCanvasInfoOnce()
    {
        if (text == null)
        {
            Debug.Log("[Noonchi] CanvasInfo: HUD text is null");
            return;
        }

        var canvas = text.GetComponentInParent<Canvas>(true);
        if (canvas == null)
        {
            Debug.Log("[Noonchi] CanvasInfo: Canvas not found in parents of HUD text");
            return;
        }

        var rt = canvas.GetComponent<RectTransform>();
        var rectSizeLocal = rt.rect.size;

        // 소수점 2자리면 0.00으로 보일 수 있어서 6자리로 찍음
        var lossy = rt.lossyScale;
        var approxWorldSize = new Vector2(rectSizeLocal.x * lossy.x, rectSizeLocal.y * lossy.y);

        Debug.Log(
            $"[Noonchi] CanvasInfo: name={canvas.name} renderMode={canvas.renderMode} " +
            $"rectSizeLocal={rectSizeLocal} lossyScale=({lossy.x:F6},{lossy.y:F6},{lossy.z:F6}) " +
            $"approxWorldSize=({approxWorldSize.x:F4},{approxWorldSize.y:F4}) " +
            $"worldPos={rt.position} worldRot={rt.rotation.eulerAngles} scaleFactor={canvas.scaleFactor}"
        );

        if (canvas.renderMode == RenderMode.WorldSpace)
            Debug.Log("[Noonchi] CanvasInfo: WorldSpace canvas -> 2D(x,y) logged is Screen pixels (camera projection).");
    }

    // ----------------------------
    // (2) 3D hit basis check
    // ----------------------------
    private void PrintHitBasisOnce(Vector3 worldPointFromReticle)
    {
        var attachTf = TryGetInteractorAttachTransform();
        if (attachTf != null)
        {
            var asRayLocal = attachTf.InverseTransformPoint(worldPointFromReticle);
            Debug.Log($"[Noonchi] HitBasis: pointWorld={worldPointFromReticle} asRayLocal={asRayLocal} (local.z ~= forward distance)");
        }
        else
        {
            Debug.Log($"[Noonchi] HitBasis: pointWorld={worldPointFromReticle} (could not read interactor attachTransform)");
        }

        // 참고용 왕복 (실제 hit collider가 아니라 데모용일 수 있음)
        var anyCollider = FindAnyNearbyCollider();
        if (anyCollider != null)
        {
            var ct = anyCollider.transform;
            var local = ct.InverseTransformPoint(worldPointFromReticle);
            var back = ct.TransformPoint(local);
            var diff = Vector3.Distance(back, worldPointFromReticle);

            Debug.Log(
                $"[Noonchi] HitBasis(ref): using collider '{anyCollider.name}' local={local} backToWorld={back} diff={diff:F6}"
            );
        }
    }

    private Transform TryGetInteractorAttachTransform()
    {
        if (reticleVisual == null || _fiInteractor == null) return null;

        var interactorObj = _fiInteractor.GetValue(reticleVisual);
        if (interactorObj is UnityEngine.XR.Interaction.Toolkit.Interactors.XRBaseInteractor baseInteractor)
            return baseInteractor.attachTransform;

        return null;
    }

    private Collider FindAnyNearbyCollider()
    {
        if (reticleVisual == null) return null;

        var cols = reticleVisual.GetComponentsInParent<Collider>(true);
        if (cols != null && cols.Length > 0) return cols[0];

        cols = reticleVisual.GetComponentsInChildren<Collider>(true);
        if (cols != null && cols.Length > 0) return cols[0];

        return null;
    }
}
