using UnityEngine;

public class Detection2DOverlayForDebug : MonoBehaviour
{
    [SerializeField] private Transform camTf;   // Main Camera transform
    [SerializeField] private float forward = 2.0f; // 앞 거리

    private void Reset()
    {
        if (Camera.main != null) camTf = Camera.main.transform;
    }

    private void LateUpdate()
    {
        if (camTf == null) return;

        transform.position = camTf.position + camTf.forward * forward;
        transform.rotation = camTf.rotation; // 카메라와 동일
    }
}