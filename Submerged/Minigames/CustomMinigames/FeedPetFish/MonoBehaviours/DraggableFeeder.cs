using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime.Attributes;
using Reactor.Utilities.Attributes;
using UnityEngine;

namespace Submerged.Minigames.CustomMinigames.FeedPetFish.MonoBehaviours;

[RegisterInIl2Cpp]
public sealed class DraggableFeeder(nint ptr) : MonoBehaviour(ptr)
{
    private const float SNAP_BACK_DURATION = 0.2f;

    public FeedFishMinigame owner;
    public Transform rotationTarget;
    public ParticleSystem fishFood;
    public BoxCollider2D activatedArea;

    private int _correctFoodIndex;
    private float _counter;
    private bool _isCorrectFood;
    private bool _isNearDropZone;

    private Vector3 _lastLocation;
    private Camera _mainCamera;
    private Vector3 _mouseOffset;
    private Transform _myLid;
    private float _recordedMovement;
    private float _stepDuration;

#if ANDROID
    private bool _isBeingDragged = false;
    private int _activeTouchId = -1;
#endif

    private void Start()
    {
        _mainCamera = Camera.main;
        _myLid = transform.GetChild(0);
        fishFood.Stop();
        _counter = 0f;
        _stepDuration = 0f;
    }

    private void Update()
    {
#if ANDROID
        HandleAndroidTouch();
#endif

        if (_isNearDropZone && _isCorrectFood)
        {
            Vector3 difference = rotationTarget.transform.position - transform.position;
            float angle = Mathf.Atan2(difference.y, difference.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.AngleAxis(angle - 90, Vector3.forward);

            fishFood.transform.position = _myLid.position;

            _counter += Time.deltaTime;

            if (_counter > 0.5f)
            {
                _counter -= 0.5f;

                if (_recordedMovement > 1f)
                {
                    fishFood.Play();
                    _stepDuration += 0.5f;

                    if (_stepDuration > 3f)
                    {
                        fishFood.Stop();
                        _isCorrectFood = false;
                        this.StartCoroutine(CoRotate());
                        owner.UpdateCompletedStep(_correctFoodIndex);
                    }
                }
                else
                {
                    fishFood.Stop();
                }

                _recordedMovement = 0f;
            }
        }
    }

#if ANDROID
    private void HandleAndroidTouch()
    {
        if (Input.touchCount <= 0) return;

        foreach (Touch touch in Input.touches)
        {
            Vector3 touchPosWorld = _mainCamera.ScreenToWorldPoint(touch.position);
            touchPosWorld.z = transform.position.z;

            if (touch.phase == TouchPhase.Began)
            {
                // Check if this specific feeder was touched
                RaycastHit2D hit = Physics2D.Raycast(touchPosWorld, Vector2.zero);
                if (hit.collider != null && hit.collider.gameObject == gameObject)
                {
                    _isBeingDragged = true;
                    _activeTouchId = touch.fingerId;
                    _mouseOffset = transform.position - touchPosWorld;
                    StopAllCoroutines(); // Stop snap-back if we grab it mid-air
                }
            }
            else if (touch.fingerId == _activeTouchId)
            {
                if (touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary)
                {
                    Vector3 newPos = touchPosWorld + _mouseOffset;
                    newPos.z = transform.position.z;
                    transform.position = newPos;

                    if (_isNearDropZone)
                    {
                        _recordedMovement += Mathf.Abs((newPos - _lastLocation).sqrMagnitude) * 1000;
                        _lastLocation = newPos;
                    }
                }
                else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    _isBeingDragged = false;
                    _activeTouchId = -1;
                    OnMouseUp(); // Reuse the return-to-shelf logic
                }
            }
        }
    }
#endif

    private void OnMouseDown()
    {
#if !ANDROID
        _mouseOffset = gameObject.transform.position - _mainCamera.ScreenToWorldPoint(Input.mousePosition);
#endif
    }

    private void OnMouseDrag()
    {
#if !ANDROID
        Vector3 position = _mainCamera.ScreenToWorldPoint(Input.mousePosition) + _mouseOffset;
        position.z = gameObject.transform.position.z;

        gameObject.transform.position = position;

        if (_isNearDropZone)
        {
            _recordedMovement += Mathf.Abs((position - _lastLocation).sqrMagnitude) * 1000;
            _lastLocation = position;
        }
#endif
    }

    private void OnMouseUp()
    {
        this.StartCoroutine(CoReturnToShelf());
        _recordedMovement = 0f;
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (collision == activatedArea)
        {
            if (!_isNearDropZone)
            {
                _isNearDropZone = true;
                fishFood.transform.position = _myLid.position;
                _lastLocation = transform.position;
            }
        }
    }

    private void OnTriggerExit2D(Collider2D collision)
    {
        if (collision == activatedArea)
        {
            if (_isNearDropZone)
            {
                _isNearDropZone = false;
                fishFood.Stop();
                fishFood.transform.localPosition = Vector3.zero;
                this.StartCoroutine(CoRotate());
            }
        }
    }

    public void SetCorrectFoodStatus(bool isCorrect, int index = -1)
    {
        _isCorrectFood = isCorrect;
        if (isCorrect) _correctFoodIndex = index;
    }

    [HideFromIl2Cpp]
    private IEnumerator CoRotate()
    {
        Quaternion targetRotation = Quaternion.identity;
        Quaternion currentRotation = transform.localRotation;

        for (float t = 0f; t <= SNAP_BACK_DURATION; t += Time.deltaTime)
        {
            float t2 = t / SNAP_BACK_DURATION;
            transform.localRotation = Quaternion.Slerp(currentRotation, targetRotation, t2);
            yield return null;
        }
        transform.localRotation = targetRotation;
    }

    [HideFromIl2Cpp]
    public IEnumerator CoReturnToShelf()
    {
        Vector3 currentPosition = transform.localPosition;
        Vector3 targetPosition = Vector3.zero;
        Quaternion targetRotation = Quaternion.identity;
        Quaternion currentRotation = transform.localRotation;
        targetPosition.z = transform.localPosition.z;

        for (float t = 0f; t <= SNAP_BACK_DURATION; t += Time.deltaTime)
        {
            float t2 = t / SNAP_BACK_DURATION;
            transform.localPosition = Vector3.Lerp(currentPosition, targetPosition, t2);
            transform.localRotation = Quaternion.Slerp(currentRotation, targetRotation, t2);
            yield return null;
        }

        transform.localPosition = targetPosition;
        transform.localRotation = targetRotation;
    }
}
