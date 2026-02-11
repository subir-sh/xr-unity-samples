using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

public class GazeTracker : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private XRInteractorReticleVisual reticleVisual;
    [SerializeField] private Camera screenCamera;

    [Header("Pinch Action (log only when performed)")]
    [SerializeField] private InputActionProperty pinchAction;

    private FieldInfo _fiTargetEndPoint;
    private FieldInfo _fiHasRaycastHit;

    void Awake()
    {
        if (screenCamera == null) screenCamera = Camera.main;
        if (reticleVisual == null) reticleVisual = FindObjectOfType<XRInteractorReticleVisual>(true);

        var t = typeof(XRInteractorReticleVisual);
        _fiTargetEndPoint = t.GetField("m_TargetEndPoint", BindingFlags.Instance | BindingFlags.NonPublic);
        _fiHasRaycastHit  = t.GetField("m_HasRaycastHit",  BindingFlags.Instance | BindingFlags.NonPublic);
    }

    void Start()
    {
        Debug.Log($"[Noonchi] Viewport: {Screen.width}x{Screen.height} (0,0=bottom-left)");
    }

    private void OnPinchPerformed(InputAction.CallbackContext ctx)
    {
        LogOnceNow();
    }

    private void LogOnceNow()
    {
        // Reticle이 실제로 따라가는 최종 3D 포인트(월드)
        var p3 = (Vector3)_fiTargetEndPoint.GetValue(reticleVisual);
        var hasHit = _fiHasRaycastHit != null && (bool)_fiHasRaycastHit.GetValue(reticleVisual);
        var mode = hasHit ? "hit" : "fallback";

        // 2D = Screen Viewport Pixel
        var sp = (screenCamera != null)
            ? screenCamera.WorldToScreenPoint(p3)
            : new Vector3(float.NaN, float.NaN, float.NaN);

        var msg = $"[Noonchi] [Eyegaze] ({mode}) World Space({p3.x:F4},{p3.y:F4},{p3.z:F4}) Viewport({sp.x:F1},{sp.y:F1})";

        Debug.Log(msg);
    }

    void OnEnable()
    {
        if (pinchAction.action != null)
        {
            pinchAction.action.Enable();
            pinchAction.action.performed += OnPinchPerformed;
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
}
