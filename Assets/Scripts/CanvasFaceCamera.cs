using UnityEngine;

/// <summary>
/// Makes a World Space Canvas continuously face the main camera.
/// Attach this script to the Canvas GameObject.
/// </summary>
public class CanvasFaceCamera : MonoBehaviour
{
    [SerializeField] private Camera targetCamera;
    [SerializeField] private bool lockYAxis = false;

    private void Start()
    {
        if (targetCamera == null)
            targetCamera = Camera.main;
    }

    private void LateUpdate()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;

            if (targetCamera == null)
                return;
        }

        Vector3 cameraPosition = targetCamera.transform.position;

        if (lockYAxis)
            cameraPosition.y = transform.position.y;

        Vector3 direction = transform.position - cameraPosition;

        if (direction.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.LookRotation(direction);
    }
}