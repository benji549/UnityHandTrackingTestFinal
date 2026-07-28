using UnityEngine;

public class SmoothedTransformFollower : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private bool preserveInitialOffset = true;

    [Header("Position")]
    [SerializeField, Min(0.001f)]
    private float positionSmoothTime = 0.06f;

    [SerializeField, Min(0f)]
    private float maximumSpeed = Mathf.Infinity;

    [Header("Rotation")]
    [SerializeField, Min(0f)]
    private float rotationSharpness = 18f;

    [Header("Tracking Recovery")]
    [Tooltip("Snap instead of smoothing if the target moves farther than this.")]
    [SerializeField, Min(0f)]
    private float teleportDistance = 1f;

    private Vector3 positionVelocity;
    private Vector3 localPositionOffset;
    private Quaternion localRotationOffset;

    private void Start()
    {
        if (target == null)
        {
            Debug.LogError(
                $"{nameof(SmoothedTransformFollower)} on {name} has no target.",
                this);

            enabled = false;
            return;
        }

        CaptureOffset();
    }

    private void LateUpdate()
    {
        Vector3 targetPosition;
        Quaternion targetRotation;

        if (preserveInitialOffset)
        {
            targetPosition = target.TransformPoint(localPositionOffset);
            targetRotation = target.rotation * localRotationOffset;
        }
        else
        {
            targetPosition = target.position;
            targetRotation = target.rotation;
        }

        if (Vector3.Distance(transform.position, targetPosition) > teleportDistance)
        {
            transform.SetPositionAndRotation(targetPosition, targetRotation);
            positionVelocity = Vector3.zero;
            return;
        }

        transform.position = Vector3.SmoothDamp(
            transform.position,
            targetPosition,
            ref positionVelocity,
            positionSmoothTime,
            maximumSpeed,
            Time.deltaTime);

        // Frame-rate-independent rotation smoothing.
        float rotationAmount =
            1f - Mathf.Exp(-rotationSharpness * Time.deltaTime);

        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            targetRotation,
            rotationAmount);
    }

    [ContextMenu("Capture Current Offset")]
    public void CaptureOffset()
    {
        if (target == null)
            return;

        localPositionOffset = target.InverseTransformPoint(transform.position);
        localRotationOffset =
            Quaternion.Inverse(target.rotation) * transform.rotation;

        positionVelocity = Vector3.zero;
    }

    public void SetTarget(Transform newTarget, bool keepCurrentOffset = true)
    {
        target = newTarget;

        if (target != null && keepCurrentOffset)
            CaptureOffset();
    }
}