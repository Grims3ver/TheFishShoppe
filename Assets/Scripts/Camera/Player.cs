using UnityEngine;
using UnityEngine.InputSystem;

public class Player : MonoBehaviour
{
    [SerializeField] Transform cameraTarget;

    [SerializeField] float moveSpeed; 
    #region Input
    Vector2 moveInput;
    Vector2 scrollInput;
    Vector2 lookInput; 
    private void OnMove(InputValue input) { 
        moveInput = input.Get<Vector2>();


    }

    void OnLook(InputValue input)
    {
        lookInput = input.Get<Vector2>();
    }

    void OnScrollWheel(InputValue input)
    {
        scrollInput = input.Get<Vector2>();
    }
    #endregion

    #region Unity Methods
    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        UpdateMovement(dt);
    }
    #endregion
    #region Control Methods

    void UpdateMovement(float deltaTime)
    {
        Vector3 forward = Camera.main.transform.forward;
        forward.y = 0f; 
        forward.Normalize();

        Vector3 motion = forward * moveSpeed * deltaTime;

        cameraTarget.position += motion; 
    }
    #endregion
}
