using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Hiccup.Samples
{
    /// <summary>Tiny "click the cubes" game so the HTML UI has real state to display and drive.</summary>
    public class SampleGame : MonoBehaviour
    {
        public enum State { Menu, Playing, Paused, Ended }

        public Camera Camera;
        public Light Sun;
        public readonly List<SalvageTarget> Targets = new List<SalvageTarget>();

        public State Current { get; private set; } = State.Menu;
        public int Score { get; private set; }
        public int SalvagedCount { get; private set; }
        public float TimeLeft { get; private set; } = 60f;
        public float Health { get; private set; } = 100f;
        public float Energy { get; private set; } = 80f;
        public string PilotName = "Pilot";
        public float SpinSpeed = 60f;
        public bool Glow = true;

        public event Action<State> StateChanged;
        public event Action StatsChanged;
        public event Action<SalvageTarget> TargetSalvaged;
        /// <summary>(message, kind) where kind is one of "", "ok", "warn", "danger".</summary>
        public event Action<string, string> Notify;

        private float _statsTimer;

        public void StartMission()
        {
            Score = 0; SalvagedCount = 0; TimeLeft = 60f; Health = 100f; Energy = 80f;
            foreach (var t in Targets)
                t.Respawn();
            SetState(State.Playing);
            Notify?.Invoke("Mission started. Salvage the cubes!", "ok");
            StatsChanged?.Invoke();
        }

        public void Pause()
        {
            if (Current == State.Playing)
                SetState(State.Paused);
        }
        public void Resume()
        {
            if (Current == State.Paused)
                SetState(State.Playing);
        }
        public void TogglePause()
        {
            if (Current == State.Playing)
                Pause(); else if (Current == State.Paused)
                Resume();
        }

        public void Abandon()
        {
            if (Current == State.Menu)
                return;
            SetState(State.Menu);
            Notify?.Invoke("Mission abandoned.", "warn");
        }

        public void Repair()
        {
            if (Current != State.Playing)
            {
                Notify?.Invoke("Start a mission first.", "warn");
                return;
            }
            if (Energy < 20f)
            {
                Notify?.Invoke("Not enough energy to repair.", "danger");
                return;
            }
            Energy -= 20f;
            Health = Mathf.Min(100f, Health + 25f);
            Notify?.Invoke("Hull repaired.", "ok");
            StatsChanged?.Invoke();
        }

        public void RandomizeColors()
        {
            foreach (var t in Targets)
                t.RandomizeColor();
        }

        private void SetState(State s)
        {
            if (Current == s)
                return;
            Current = s;
            StateChanged?.Invoke(s);
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                TogglePause();
            if (Current != State.Playing)
                return;

            float dt = Time.deltaTime;
            TimeLeft -= dt;
            Energy = Mathf.Min(100f, Energy + 4f * dt);
            Health = Mathf.Max(0f, Health - 1.5f * dt);

            if (TimeLeft <= 0f || Health <= 0f)
            {
                TimeLeft = Mathf.Max(0f, TimeLeft);
                SetState(State.Ended);
                Notify?.Invoke(Health <= 0f ? "Hull breached! Mission over." : $"Time is up. Final score {Score}.", "warn");
                StatsChanged?.Invoke();
                return;
            }

            HandleClick();

            _statsTimer += dt;
            if (_statsTimer >= 0.25f)
            {
                _statsTimer = 0f;
                StatsChanged?.Invoke();
            }
        }

        private void HandleClick()
        {
            var pointer = Pointer.current;
            if (pointer == null || Camera == null || !pointer.press.wasPressedThisFrame)
                return;

            // No Physics module: test the ray against each target's bounds ourselves and take the nearest.
            var ray = Camera.ScreenPointToRay(pointer.position.ReadValue());
            SalvageTarget target = null;
            float nearest = 200f;
            foreach (var t in Targets)
            {
                if (t.TryHit(ray, out float d) && d < nearest)
                {
                    nearest = d;
                    target = t;
                }
            }
            if (target == null)
                return;

            target.Salvage();
            Score += target.Value;
            SalvagedCount++;
            Health = Mathf.Min(100f, Health + 5f);
            TargetSalvaged?.Invoke(target);
            StatsChanged?.Invoke();
        }
    }
}
