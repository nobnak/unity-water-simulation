using UnityEngine;

namespace Nobnak.WaterSimulation {
    /// <summary>
    /// Lightweight fly/orbit camera controller for water sample scenes.
    /// Supports WASD + mouse look, altitude controls, and preset viewpoints.
    /// </summary>
    public class SimpleCameraController : MonoBehaviour {
        #region serialized fields
        [Header("Movement")]
        [SerializeField] private float moveSpeed = 15.0f;
        [SerializeField] private float fastMultiplier = 2.5f;
        [SerializeField] private float lookSensitivity = 2.0f;

        [Header("View Preset")]
        [SerializeField] private Vector3 defaultPosition = new Vector3(0f, 15f, -25f);
        [SerializeField] private Vector3 defaultEulerAngles = new Vector3(30f, 0f, 0f);
        [SerializeField] private Vector3 lowAnglePosition = new Vector3(0f, 1.5f, -20f);
        [SerializeField] private Vector3 lowAngleEulerAngles = new Vector3(5f, 0f, 0f);
        #endregion

        #region private fields
        private float yaw;
        private float pitch;
        private bool isFreeLook;
        #endregion

        #region Unity lifecycle
        private void Start() {
            Vector3 euler = transform.eulerAngles;
            pitch = euler.x;
            yaw = euler.y;
        }

        private void Update() {
            HandleInput();
        }
        #endregion

        #region public interface
        public void ResetToDefaultView() {
            transform.position = defaultPosition;
            transform.eulerAngles = defaultEulerAngles;
            pitch = defaultEulerAngles.x;
            yaw = defaultEulerAngles.y;
        }

        public void SetLowAngleView() {
            transform.position = lowAnglePosition;
            transform.eulerAngles = lowAngleEulerAngles;
            pitch = lowAngleEulerAngles.x;
            yaw = lowAngleEulerAngles.y;
        }
        #endregion

        #region private methods
        private void HandleInput() {
#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var kb = UnityEngine.InputSystem.Keyboard.current;

            if (mouse != null) {
                isFreeLook = mouse.rightButton.isPressed;
                if (isFreeLook) {
                    Vector2 delta = mouse.delta.ReadValue() * 0.1f * lookSensitivity;
                    yaw += delta.x;
                    pitch = Mathf.Clamp(pitch - delta.y, -85f, 85f);
                    transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                }
            }

            float speed = moveSpeed;
            Vector3 move = Vector3.zero;

            if (kb != null) {
                if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) speed *= fastMultiplier;
                if (kb.wKey.isPressed) move += transform.forward;
                if (kb.sKey.isPressed) move -= transform.forward;
                if (kb.aKey.isPressed) move -= transform.right;
                if (kb.dKey.isPressed) move += transform.right;
                if (kb.eKey.isPressed) move += Vector3.up;
                if (kb.qKey.isPressed) move -= Vector3.up;
            }

            if (move.sqrMagnitude > 0.001f) {
                transform.position += move.normalized * (speed * Time.deltaTime);
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetMouseButtonDown(1)) {
                isFreeLook = true;
            } else if (Input.GetMouseButtonUp(1)) {
                isFreeLook = false;
            }

            if (isFreeLook) {
                float mouseX = Input.GetAxis("Mouse X") * lookSensitivity;
                float mouseY = Input.GetAxis("Mouse Y") * lookSensitivity;
                yaw += mouseX;
                pitch = Mathf.Clamp(pitch - mouseY, -85f, 85f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? fastMultiplier : 1.0f);
            Vector3 move = Vector3.zero;

            if (Input.GetKey(KeyCode.W)) move += transform.forward;
            if (Input.GetKey(KeyCode.S)) move -= transform.forward;
            if (Input.GetKey(KeyCode.A)) move -= transform.right;
            if (Input.GetKey(KeyCode.D)) move += transform.right;
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;

            if (move.sqrMagnitude > 0.001f) {
                transform.position += move.normalized * (speed * Time.deltaTime);
            }
#endif
        }
        #endregion
    }
}
