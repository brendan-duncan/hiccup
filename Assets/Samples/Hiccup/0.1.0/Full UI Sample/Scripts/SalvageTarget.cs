using UnityEngine;

namespace Hiccup.Samples
{
    /// <summary>A drifting, spinning cube the player clicks to score.</summary>
    public class SalvageTarget : MonoBehaviour
    {
        public SampleGame Game;
        public int Value = 10;
        public bool IsActive { get; private set; } = true;

        private static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int s_Color = Shader.PropertyToID("_Color");
        private static readonly int s_Emission = Shader.PropertyToID("_EmissionColor");

        private Renderer _renderer;
        private MaterialPropertyBlock _block;
        private Vector3 _velocity;
        private Vector3 _spinAxis;
        private Color _color;
        private float _respawnAt;
        private static readonly Bounds s_bounds = new Bounds(new Vector3(0f, 1.2f, 1f), new Vector3(10f, 4f, 6f));

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _block = new MaterialPropertyBlock();
            Respawn();
        }

        public void Respawn()
        {
            IsActive = true;
            transform.position = new Vector3(
                Random.Range(s_bounds.min.x, s_bounds.max.x),
                Random.Range(s_bounds.min.y, s_bounds.max.y),
                Random.Range(s_bounds.min.z, s_bounds.max.z));
            transform.localScale = Vector3.one * Random.Range(0.5f, 0.9f);
            _velocity = Random.insideUnitSphere * 0.6f;
            _spinAxis = Random.onUnitSphere;
            Value = Mathf.RoundToInt(20f - transform.localScale.x * 15f);
            RandomizeColor();
            _renderer.enabled = true;
        }

        public void RandomizeColor()
        {
            _color = Color.HSVToRGB(Random.value, 0.65f, 1f);
            ApplyColor();
        }

        private void ApplyColor()
        {
            bool glow = Game == null || Game.Glow;
            _appliedGlow = glow;
            _renderer.GetPropertyBlock(_block);
            _block.SetColor(s_BaseColor, _color);
            _block.SetColor(s_Color, _color);
            _block.SetColor(s_Emission, glow ? _color * 0.8f : Color.black);
            _renderer.SetPropertyBlock(_block);
        }

        /// <summary>Ray test against the cube's world bounds; the sample does not use the Physics module.</summary>
        public bool TryHit(Ray ray, out float distance)
        {
            distance = 0f;
            return IsActive && _renderer != null && _renderer.bounds.IntersectRay(ray, out distance);
        }

        public void Salvage()
        {
            if (!IsActive)
                return;
            IsActive = false;
            _renderer.enabled = false;
            _respawnAt = Time.time + Random.Range(1.5f, 3f);
        }

        private void Update()
        {
            if (!IsActive)
            {
                if (Time.time >= _respawnAt)
                    Respawn();
                return;
            }

            float speed = Game != null ? Game.SpinSpeed : 60f;
            transform.Rotate(_spinAxis, speed * Time.deltaTime, Space.World);

            var p = transform.position + _velocity * Time.deltaTime;
            if (p.x < s_bounds.min.x || p.x > s_bounds.max.x)
                _velocity.x = -_velocity.x;
            if (p.y < s_bounds.min.y || p.y > s_bounds.max.y)
                _velocity.y = -_velocity.y;
            if (p.z < s_bounds.min.z || p.z > s_bounds.max.z)
                _velocity.z = -_velocity.z;
            transform.position = s_bounds.ClosestPoint(p);

            if (Game != null && Game.Glow != _appliedGlow)
                ApplyColor();
        }

        private bool _appliedGlow = true;
    }
}
