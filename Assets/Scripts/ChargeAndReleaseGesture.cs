using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Charge-and-release gesture controller for XR Hands.
///
/// This script does NOT reference StaticHandGesture directly - wire it up
/// entirely from the Inspector instead:
///
///   On the Thumbs Up "Static Hand Gesture" component:
///     - Gesture Performed  ->  (this GameObject) ChargeAndReleaseGesture.OnThumbsUpPerformed
///     - Gesture Ended      ->  (this GameObject) ChargeAndReleaseGesture.OnThumbsUpEnded
///
///   On the Fist "Static Hand Gesture" component:
///     - Gesture Performed  ->  (this GameObject) ChargeAndReleaseGesture.OnFistPerformed
///     (Gesture Ended isn't needed for the fist)
///
/// Flow:
///   Thumbs Up -> spawn a ball at spawnPoint and grow it every frame ("charging").
///   Fist      -> only fires the shot (destroys ball + plays particles) if the
///                hand was actively charging, i.e. the gesture immediately
///                before this Fist was Thumbs Up. A fist made from an idle/open
///                hand with no prior charge is ignored entirely.
///
/// Aim stabilization:
///   The hand physically moves as the fingers curl from Thumbs Up into a Fist,
///   which disturbs aim right at the moment you'd naturally fire. To avoid firing
///   from that disturbed pose, we keep a short rolling history of aimSource's
///   world pose. The instant Thumbs Up ends (the curl is just beginning), we look
///   slightly backward into that history and lock in a "stable" pose from just
///   before the disturbance started. The shot is fired from that locked pose
///   instead of aimSource's live (disturbed) pose.
/// </summary>
public class ChargeAndReleaseGesture : MonoBehaviour
{
    [Header("Ball (charge visual)")]
    [SerializeField] private Transform spawnPoint;      // e.g. a child of the palm
    [SerializeField] private GameObject ballPrefab;
    [SerializeField] private float startScale = 0.02f;
    [SerializeField] private float maxScale = 0.25f;
    [SerializeField] private float growthRate = 0.15f;  // scale units per second

[SerializeField] private AudioSource shootSound;
    [Header("Release")]
    [Tooltip("Used as a template - a clone is instantiated at the locked aim pose " +
             "and played, so the live scene instance (still parented under your " +
             "wrist-follow / One Euro anchor) is never itself moved or reparented.")]
    [SerializeField] private ParticleSystem muzzleFlash;

    [Header("Aim stabilization")]
    [Tooltip("The transform that gets disturbed by the gesture motion - typically " +
             "the wrist-follow anchor that your One Euro filter drives (or the " +
             "muzzle transform itself, if it has no independent offset from that anchor).")]
    [SerializeField] private Transform aimSource;
    [Tooltip("How much aim history to keep around, in seconds. Only needs to comfortably " +
             "cover lockLookbackTime.")]
    [SerializeField] private float aimBufferDuration = 0.5f;
    [Tooltip("When Thumbs Up ends, look this far back into the aim history to find a " +
             "stable pose from before the curl-into-fist motion started disturbing aim. " +
             "Start around 0.08-0.12s and tune by feel.")]
    [SerializeField] private float lockLookbackTime = 0.1f;

    [Header("Tuning")]
    [Tooltip("How long to wait after Thumbs Up ends before cancelling the charge, " +
             "in case the hand is still mid-motion into a Fist. This absorbs the " +
             "fact that Thumbs Up and Fist are detected by two independent " +
             "Static Hand Gesture components with no guaranteed frame ordering " +
             "between them.")]
    [SerializeField] private float cancelGraceTime = 0.15f;

    private struct AimSample
    {
        public float time;
        public Vector3 position;
        public Quaternion rotation;
    }

    private readonly List<AimSample> _aimBuffer = new List<AimSample>(64);
    private Pose? _lockedAimPose;

    private GameObject _ball;
    private float _currentScale;
    private bool _isCharging;
    private Coroutine _cancelRoutine;

    private void Update()
    {
        if (!_isCharging || _ball == null) return;

        _currentScale = Mathf.Min(_currentScale + growthRate * Time.deltaTime, maxScale);
        _ball.transform.localScale = Vector3.one * _currentScale;
    }

    // Record after Update so we capture aimSource's pose for this frame AFTER any
    // filters (e.g. your One Euro filter) have already run and moved it.
    private void LateUpdate()
    {
        RecordAimSample();
    }

    private void RecordAimSample()
    {
        if (aimSource == null) return;

        _aimBuffer.Add(new AimSample
        {
            time = Time.time,
            position = aimSource.position,
            rotation = aimSource.rotation
        });

        float cutoff = Time.time - aimBufferDuration;
        int trimCount = 0;
        while (trimCount < _aimBuffer.Count && _aimBuffer[trimCount].time < cutoff)
            trimCount++;
        if (trimCount > 0)
            _aimBuffer.RemoveRange(0, trimCount);
    }

    /// <summary>Interpolated aim pose from "timeAgo" seconds before now.</summary>
    private Pose SampleAimAt(float timeAgo)
    {
        if (_aimBuffer.Count == 0)
        {
            return aimSource != null
                ? new Pose(aimSource.position, aimSource.rotation)
                : default;
        }

        float targetTime = Time.time - timeAgo;

        if (targetTime <= _aimBuffer[0].time)
            return new Pose(_aimBuffer[0].position, _aimBuffer[0].rotation);

        int last = _aimBuffer.Count - 1;
        if (targetTime >= _aimBuffer[last].time)
            return new Pose(_aimBuffer[last].position, _aimBuffer[last].rotation);

        for (int i = 1; i < _aimBuffer.Count; i++)
        {
            if (_aimBuffer[i].time >= targetTime)
            {
                AimSample a = _aimBuffer[i - 1];
                AimSample b = _aimBuffer[i];
                float t = Mathf.InverseLerp(a.time, b.time, targetTime);
                return new Pose(
                    Vector3.Lerp(a.position, b.position, t),
                    Quaternion.Slerp(a.rotation, b.rotation, t));
            }
        }

        AimSample fallback = _aimBuffer[last];
        return new Pose(fallback.position, fallback.rotation);
    }

    // ---- Assign these from the Inspector on each Static Hand Gesture component ----

    /// <summary>Hook this to the Thumbs Up gesture's "Gesture Performed" event.</summary>
    public void OnThumbsUpPerformed()
    {
        if (_cancelRoutine != null)
        {
            StopCoroutine(_cancelRoutine);
            _cancelRoutine = null;
        }

        _isCharging = true;
        _lockedAimPose = null; // clear any stale lock from a previous cycle

        if (_ball == null)
        {
            _ball = Instantiate(ballPrefab, spawnPoint.position, spawnPoint.rotation, spawnPoint);
            _currentScale = startScale;
            _ball.transform.localScale = Vector3.one * _currentScale;
        }
    }

    /// <summary>Hook this to the Thumbs Up gesture's "Gesture Ended" event.</summary>
    public void OnThumbsUpEnded()
    {
        if (_isCharging)
        {
            // The curl into a fist is just beginning - lock the aim now, using a
            // slightly-earlier sample so we skip past the first moments of that motion.
            _lockedAimPose = SampleAimAt(lockLookbackTime);
            _cancelRoutine = StartCoroutine(CancelIfFistNeverCame());
        }
    }

    private IEnumerator CancelIfFistNeverCame()
    {
        yield return new WaitForSeconds(cancelGraceTime);
        CancelCharge();
        _cancelRoutine = null;
    }

    /// <summary>Hook this to the Fist gesture's "Gesture Performed" event.</summary>
    public void OnFistPerformed()
    {
        // The important guard: a fist made from an idle/open hand (no prior
        // Thumbs Up charge) is ignored completely.
        if (!_isCharging) return;

        if (_cancelRoutine != null)
        {
            StopCoroutine(_cancelRoutine);
            _cancelRoutine = null;
        }

        Fire();
    }

    private void Fire()
    {
        if (_ball != null)
        {
            Destroy(_ball);
            _ball = null;
        }

        // Fall back to a fresh lookback sample in the unlikely case Fire happens
        // without OnThumbsUpEnded having locked one already.
        Pose firePose = _lockedAimPose ?? SampleAimAt(lockLookbackTime);
        SpawnMuzzleFlash(firePose);

        _isCharging = false;
        _lockedAimPose = null;
    }

    private void SpawnMuzzleFlash(Pose pose)
    {
        if (muzzleFlash == null) return;

        // Instantiate a detached clone at the locked pose instead of playing/moving
        // the live, wrist-parented instance - the live one keeps quietly following
        // the wrist, ready as a template for the next shot.
        ParticleSystem fx = Instantiate(muzzleFlash, pose.position, pose.rotation);
        fx.Play();
        shootSound.Play();

        float lifetime = fx.main.duration + fx.main.startLifetime.constantMax;
        Destroy(fx.gameObject, lifetime);
    }

    private void CancelCharge()
    {
        if (_ball != null)
        {
            Destroy(_ball);
            _ball = null;
        }

        _isCharging = false;
        _lockedAimPose = null;
    }
}