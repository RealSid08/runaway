using UnityEngine;
using UnityEngine.InputSystem;

namespace Runaway.App
{
    /// <summary>Orbit camera: right-drag to orbit, middle-drag or WASD to pan, scroll to zoom. Directors can take over.</summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public Vector3 Target = new Vector3(4, 0, 2);
        public float Yaw = 205f, Pitch = 34f, Distance = 92f;
        public bool InputEnabled = true;
        public System.Func<bool> PointerOverUi;
        Vector3 _t; float _y, _p, _d;
        Camera _cam;
        public Camera Cam => _cam;
        public float CurrentDistance => _d;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            _t = Target; _y = Yaw; _p = Pitch; _d = Distance;
            Apply();
        }

        public void Snap() { _t = Target; _y = Yaw; _p = Pitch; _d = Distance; Apply(); }

        public void Focus(Vector3 p, float distance = -1)
        {
            Target = p;
            if (distance > 0) Distance = distance;
        }

        void LateUpdate()
        {
            if (InputEnabled) HandleInput();
            float k = 1 - Mathf.Exp(-Time.unscaledDeltaTime * 5f);
            _t = Vector3.Lerp(_t, Target, k);
            _y = Mathf.LerpAngle(_y, Yaw, k);
            _p = Mathf.Lerp(_p, Pitch, k);
            _d = Mathf.Lerp(_d, Distance, k);
            Apply();
        }

        void Apply()
        {
            var rot = Quaternion.Euler(_p, _y, 0);
            transform.position = _t + rot * new Vector3(0, 0, -_d);
            transform.rotation = rot;
        }

        void HandleInput()
        {
            var m = Mouse.current; var kb = Keyboard.current;
            if (m == null) return;
            bool overUi = PointerOverUi != null && PointerOverUi();
            var delta = m.delta.ReadValue();
            if (m.rightButton.isPressed && !overUi)
            {
                Yaw += delta.x * 0.18f;
                Pitch = Mathf.Clamp(Pitch - delta.y * 0.14f, 12f, 82f);
            }
            if (m.middleButton.isPressed && !overUi)
            {
                var right = transform.right; right.y = 0; right.Normalize();
                var fwd = Vector3.Cross(right, Vector3.up);
                Target -= (right * delta.x + fwd * -delta.y) * Distance * 0.0016f;
            }
            var scroll = m.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f && !overUi) Distance = Mathf.Clamp(Distance * (1 - Mathf.Sign(scroll) * 0.1f), 18f, 520f);
            if (kb != null)
            {
                var move = Vector2.zero;
                if (kb.wKey.isPressed) move.y += 1;
                if (kb.sKey.isPressed) move.y -= 1;
                if (kb.aKey.isPressed) move.x -= 1;
                if (kb.dKey.isPressed) move.x += 1;
                if (move != Vector2.zero)
                {
                    var right = transform.right; right.y = 0; right.Normalize();
                    var fwd = Vector3.Cross(right, Vector3.up);
                    Target += (right * move.x + fwd * move.y) * Distance * 0.9f * Time.unscaledDeltaTime;
                }
            }
        }
    }
}
