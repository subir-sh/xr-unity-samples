using System.Reflection;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using TMPro; 

public class NoonchiGazeHUD1 : MonoBehaviour
{
    [SerializeField] private XRInteractorReticleVisual reticleVisual; 
    [SerializeField] private Camera screenCamera; 
    [SerializeField] private TextMeshProUGUI text; 
    [SerializeField] private float intervalSeconds = 1f;

    private float _next;

    // private field 접근 (m_TargetEndPoint가 레티클이 따라가는 점)
    private FieldInfo _fiTargetEndPoint;
    private FieldInfo _fiHasRaycastHit;
    
    void Awake()
    {
        if (screenCamera == null) screenCamera = Camera.main;
        if (reticleVisual == null) reticleVisual = FindObjectOfType<XRInteractorReticleVisual>(true);

        var t = typeof(XRInteractorReticleVisual);
        _fiTargetEndPoint = t.GetField("m_TargetEndPoint", BindingFlags.Instance | BindingFlags.NonPublic);
        _fiHasRaycastHit  = t.GetField("m_HasRaycastHit",  BindingFlags.Instance | BindingFlags.NonPublic);

        _next = Time.unscaledTime + Mathf.Max(0.01f, intervalSeconds);
    }

    // reticleVisual.Update()가 끝난 뒤가 안전해서 LateUpdate
    void LateUpdate()
    {
        if (text == null) return;
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + Mathf.Max(0.01f, intervalSeconds);

        if (reticleVisual == null || _fiTargetEndPoint == null)
        {
            text.text = "[Noonchi] reticleVisual not found";
            return;
        }

        var p3 = (Vector3)_fiTargetEndPoint.GetValue(reticleVisual);
        var hasHit = _fiHasRaycastHit != null && (bool)_fiHasRaycastHit.GetValue(reticleVisual);

        var sp = (screenCamera != null) ? screenCamera.WorldToScreenPoint(p3)
                                        : new Vector3(float.NaN, float.NaN, float.NaN);

        var mode = hasHit ? "hit" : "fallback";
        var msg = $"[Noonchi] [Eyegaze] ({mode}) 3D({p3.x:F4},{p3.y:F4},{p3.z:F4}) 2D({sp.x:F1},{sp.y:F1})";

        text.text = msg;
        Debug.Log(msg);
    }
}
