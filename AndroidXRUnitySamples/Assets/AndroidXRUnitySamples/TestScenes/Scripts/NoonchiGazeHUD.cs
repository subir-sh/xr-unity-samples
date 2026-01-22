using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using TMPro;
//using AndroidXRUnitySamples;

public class NoonchiGazeHUD : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private XRInteractorReticleVisual reticleVisual;
    [SerializeField] private Camera screenCamera;
    [SerializeField] private TextMeshProUGUI text;

    [Header("Pinch Action (log only when performed)")]
    [SerializeField] private InputActionProperty pinchAction;

    // XRI private fields
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
        //Singleton.Instance.OriginManager.EnableGazeInteraction = true;

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
            Debug.Log("[Noonchi] PinchAction is null.");
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
        // 예외 처리
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
    }
}
