using System;
using UnityEngine;

namespace Hiccup.Samples
{
    /// <summary>Drives the "drone console" HTML panel that sits on a quad in the 3D scene.</summary>
    public class WorldPanelController : MonoBehaviour
    {
        public SampleGame Game;
        public HtmlDocument Document;

        private bool _wired;
        private bool _spinning = true;
        private float _statusTimer;
        private const int MaxLogLines = 14;

        private void OnEnable()
        {
            if (Document == null)
                Document = GetComponent<HtmlDocument>();
            if (Document == null)
                return;
            if (Document.IsCreated)
                Wire(Document);
            else
                Document.Created += Wire;
        }

        private void OnDisable()
        {
            if (Document != null)
                Document.Created -= Wire;
        }

        private void Wire(HtmlDocument doc)
        {
            if (_wired)
                return;
            _wired = true;

            doc.On("cmd-form", "submit", e =>
            {
                var input = doc.Q("#cmd");
                var cmd = input.Value.Trim();
                input.Value = string.Empty;
                input.Focus();
                Run(cmd);
            });

            doc.OnAction("spin", e =>
            {
                _spinning = !_spinning;
                Game.SpinSpeed = _spinning ? 60f : 0f;
                doc.Q("[data-action=spin]").SetAttribute("aria-pressed", _spinning ? "true" : "false");
                Log(_spinning ? "Debris spin enabled" : "Debris spin disabled", "ok");
            });

            doc.OnAction("color", e =>
            {
                Game.RandomizeColors();
                Log("Debris colors re-randomized", "ok");
            });

            doc.On("lights", "change", e =>
            {
                if (Game.Sun != null)
                    Game.Sun.enabled = e.isChecked;
                Log(e.isChecked ? "Lights on" : "Lights off", e.isChecked ? "ok" : "warn");
            });

            Log("Console online. Type 'help' for commands.", "ok");
            SetStatus("Idle");
        }

        private void Update()
        {
            if (!_wired || Game == null)
                return;
            _statusTimer += Time.deltaTime;
            if (_statusTimer < 1f)
                return;
            _statusTimer = 0f;
            int active = 0;
            foreach (var t in Game.Targets)
            {
                if (t.IsActive)
                active++;
            }
            SetStatus(Game.Current == SampleGame.State.Playing
                ? $"Tracking {active} targets · score {Game.Score} · {Mathf.CeilToInt(Game.TimeLeft)}s"
                : $"{Game.Current} · {active} targets in range");
        }

        private void Run(string cmd)
        {
            if (string.IsNullOrEmpty(cmd))
                return;
            Log("> " + cmd, "");
            switch (cmd.ToLowerInvariant())
            {
                case "help":
                    Log("Commands: help, scan, spin, color, status, clear", "ok");
                    break;
                case "scan":
                    float nearest = float.MaxValue;
                    int count = 0;
                    var cam = Game.Camera != null ? Game.Camera.transform.position : Vector3.zero;
                    foreach (var t in Game.Targets)
                    {
                        if (!t.IsActive)
                            continue;
                        count++;
                        nearest = Mathf.Min(nearest, Vector3.Distance(cam, t.transform.position));
                    }
                    Log(count > 0 ? $"{count} targets. Nearest at {nearest:0.0} m." : "No targets in range.", "ok");
                    break;
                case "spin":
                    Document.Q("[data-action=spin]").Click();
                    break;
                case "color":
                case "color":
                    Document.Q("[data-action=color]").Click();
                    break;
                case "status":
                    Log($"State {Game.Current}, score {Game.Score}, hull {Mathf.RoundToInt(Game.Health)}%, energy {Mathf.RoundToInt(Game.Energy)}", "ok");
                    break;
                case "clear":
                    Document.Q("#log").InnerHtml = string.Empty;
                    break;
                default:
                    Log($"Unknown command '{cmd}'. Try 'help'.", "warn");
                    break;
            }
        }

        private void Log(string text, string kind)
        {
            var log = Document.Q("#log");
            log.Prepend($"<li class=\"{kind}\">{DateTime.Now:HH:mm:ss} {Escape(text)}</li>");
            var lines = Document.QAll("#log li");
            for (int i = 0; i < lines.Count; i++)
            {
                if (i >= MaxLogLines)
                    lines[i].Remove();
                else
                    lines[i].Dispose();
            }
            log.Dispose();
        }

        private void SetStatus(string text) => Document.Q("#drone-status").Text = text;

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
