using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Hiccup.Samples
{
    /// <summary>
    /// Wires the HTML game UI (menu, HUD, inventory, settings, dialogs, toasts) to <see cref="SampleGame"/>.
    /// Everything here is plain DOM manipulation through <see cref="HtmlDocument"/> / <see cref="HtmlElement"/>.
    /// </summary>
    public class GameUIController : MonoBehaviour
    {
        public SampleGame Game;
        public HtmlDocument Document;

        private static readonly string[] s_screens = { "menu", "hud", "inventory", "settings" };
        private static readonly string[] s_tabs = { "tab-audio", "tab-graphics", "tab-controls" };

        private struct Item
        {
            public string Id, Name, Icon, Category, Description;
            public int Qty;
        }

        private static readonly Item[] s_items =
        {
            new Item { Id = "wrench", Name = "Hydro wrench", Icon = "🔧", Category = "tool", Qty = 1, Description = "Loosens anything, including your grip on reality." },
            new Item { Id = "torch", Name = "Plasma torch", Icon = "🔥", Category = "tool", Qty = 1, Description = "Cuts through hull plating. Keep away from the oxygen tanks." },
            new Item { Id = "scanner", Name = "Debris scanner", Icon = "📡", Category = "tool", Qty = 1, Description = "Marks salvage worth more than its weight." },
            new Item { Id = "magnet", Name = "Grapple magnet", Icon = "🧲", Category = "tool", Qty = 2, Description = "Pulls loose parts toward the drone." },
            new Item { Id = "plate", Name = "Hull plate", Icon = "🛡️", Category = "part", Qty = 6, Description = "Standard titanium plating. Repairs 25 hull." },
            new Item { Id = "coil", Name = "Field coil", Icon = "🌀", Category = "part", Qty = 3, Description = "Spare coil for the reactor shielding." },
            new Item { Id = "cell", Name = "Power cell", Icon = "🔋", Category = "part", Qty = 4, Description = "Restores 40 energy." },
            new Item { Id = "gyro", Name = "Gyro assembly", Icon = "⚙️", Category = "part", Qty = 1, Description = "Keeps the drone pointing the right way." },
            new Item { Id = "ration", Name = "Ration pack", Icon = "🍱", Category = "consumable", Qty = 8, Description = "Tastes like cardboard. Nutritious cardboard." },
            new Item { Id = "coffee", Name = "Coffee pod", Icon = "☕", Category = "consumable", Qty = 12, Description = "Mission critical." },
            new Item { Id = "medkit", Name = "Med kit", Icon = "🩹", Category = "consumable", Qty = 2, Description = "For when the wrench slips." },
            new Item { Id = "oxygen", Name = "O₂ canister", Icon = "🫧", Category = "consumable", Qty = 5, Description = "Twelve hours of breathing." },
        };

        private const string TooltipScript = @"
            var wraps = function () { return root.querySelectorAll('.tip-wrap'); };
            root.addEventListener('keydown', function (e) {
                if (e.key !== 'Escape') return;
                wraps().forEach(function (w) { w.classList.add('tip-dismissed'); });
            });
            var reveal = function (e) {
                var t = e.target, w = t && t.closest ? t.closest('.tip-wrap') : null;
                if (w) w.classList.remove('tip-dismissed');
            };
            root.addEventListener('pointerover', reveal);
            root.addEventListener('focusin', reveal);";

        private string _screen = "menu";
        private string _selectedItem;
        private int _toastId;
        private bool _wired;

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
            if (Game != null)
            {
                Game.StateChanged -= OnStateChanged;
                Game.StatsChanged -= UpdateHud;
                Game.Notify -= Toast;
                Game.TargetSalvaged -= OnSalvaged;
            }
        }

        private void Wire(HtmlDocument doc)
        {
            if (_wired)
                return;
            _wired = true;

            // ---- Navigation and menu actions (data-action="...")
            doc.OnAction("start", e => { Game.StartMission(); ShowScreen("hud"); });
            doc.OnAction("show", e => ShowScreen(e.GetData("screen") ?? "menu"));
            doc.OnAction("credits", e => doc.Q("#credits-dialog").ShowModal());
            doc.OnAction("close-credits", e => doc.Q("#credits-dialog").CloseModal());
            doc.OnAction("quit", e => { Toast("Quit requested. In a browser the tab stays open; Application.Quit() was called.", "warn"); Application.Quit(); });
            doc.OnAction("pause", e => Game.Pause());
            doc.OnAction("resume", e => Game.Resume());
            doc.OnAction("abandon", e => { doc.Q("#pause-dialog").CloseModal(); Game.Abandon(); ShowScreen("menu"); });
            doc.OnAction("repair", e => Game.Repair());
            doc.OnAction("tab", e => SelectTab(e.id, false));
            doc.OnAction("reset-settings", e => ResetSettings());
            doc.OnAction("use-item", OnUseItem);

            // ---- Live settings feedback
            doc.On("input", e =>
            {
                if (e.tag != "input")
                    return;
                doc.Q("#" + e.id + "-out").Text = e.value;   // <output> next to every range slider
                if (e.id == "spin")
                    Game.SpinSpeed = e.ValueAsFloat;
                if (e.id == "player-name")
                    doc.Q("#pilot").Text = string.IsNullOrWhiteSpace(e.value) ? "Pilot" : e.value;
            });
            doc.On("change", e =>
            {
                switch (e.id)
                {
                    case "theme": ApplyTheme(e.value); break;
                    case "quality": QualitySettings.SetQualityLevel(Mathf.Clamp(e.ValueAsInt, 0, QualitySettings.names.Length - 1), true); break;
                    case "fov":
                        if (Game.Camera != null)
                            Game.Camera.fieldOfView = Mathf.Clamp(e.ValueAsFloat, 40f, 110f);
                        break;
                    case "bloom": Game.Glow = e.isChecked; break;
                    case "mute": AudioListener.volume = e.isChecked ? 0f : 1f; Toast(e.isChecked ? "Audio muted" : "Audio on"); break;
                    case "inv-filter": FilterInventory(); break;
                }
                if (e.name == "fps")
                    Application.targetFrameRate = e.ValueAsInt <= 0 ? -1 : e.ValueAsInt;
            });
            doc.On("inv-search", "input", e => FilterInventory());
            doc.On("settings-form", "submit", e =>
            {
                ApplySettings();
                Toast("Settings applied", "ok");
                doc.Announce("Settings applied");
            });

            // ---- Keyboard: ARIA tabs (arrow keys), listbox roving focus, Escape for pause
            doc.On("settings-tabs", "keydown", e =>
            {
                int i = Array.IndexOf(s_tabs, e.id);
                if (i < 0)
                    return;
                int n = i;
                if (e.IsKey("ArrowRight"))
                    n = (i + 1) % s_tabs.Length;
                else if (e.IsKey("ArrowLeft"))
                    n = (i + s_tabs.Length - 1) % s_tabs.Length;
                else if (e.IsKey("Home"))
                    n = 0;
                else if (e.IsKey("End"))
                    n = s_tabs.Length - 1;
                else
                    return;
                SelectTab(s_tabs[n], true);
                e.Handled = true;
            });
            doc.On("inv-grid", "click", e =>
            {
                var id = FindItemId(e);
                if (id != null)
                    SelectItem(id, false);
            });
            doc.On("inv-grid", "keydown", e =>
            {
                var id = FindItemId(e);
                if (id == null)
                    return;
                var visible = VisibleItemIds();
                int i = visible.IndexOf(id);
                if (i < 0)
                    return;
                int n = i;
                if (e.IsKey("ArrowRight") || e.IsKey("ArrowDown"))
                    n = Mathf.Min(visible.Count - 1, i + 1);
                else if (e.IsKey("ArrowLeft") || e.IsKey("ArrowUp"))
                    n = Mathf.Max(0, i - 1);
                else if (e.IsKey("Home"))
                    n = 0;
                else if (e.IsKey("End"))
                    n = visible.Count - 1;
                else if (e.IsKey("Enter") || e.IsKey(" "))
                {
                    SelectItem(id, false);
                    e.Handled = true;
                    return;
                }
                else
                    return;
                SelectItem(visible[n], true);
                e.Handled = true;
            });
            doc.On("keydown", e =>
            {
                if (!e.IsKey("Escape"))
                    return;
                if (Game.Current == SampleGame.State.Paused)
                    Game.Resume();
                else if (Game.Current == SampleGame.State.Playing)
                    Game.Pause();
            });

            // ---- Tooltip dismissal (WAI-ARIA: Escape hides a tooltip; it comes back on the next hover/focus).
            // Done in-page through Eval so pointer-move traffic never crosses the bridge.
            doc.Eval(TooltipScript);

            // ---- Game -> UI
            Game.StateChanged += OnStateChanged;
            Game.StatsChanged += UpdateHud;
            Game.Notify += Toast;
            Game.TargetSalvaged += OnSalvaged;

            BuildInventory();
            UpdateStatus();
            UpdateHud();
            ShowScreen("menu");
        }

        // ------------------------------------------------------------------ screens

        private void ShowScreen(string name)
        {
            if (Array.IndexOf(s_screens, name) < 0)
                name = "menu";
            _screen = name;
            foreach (var s in s_screens)
                Document.Q("#screen-" + s).Hidden = s != name;
            foreach (var btn in Document.QAll(".nav-btn"))
            {
                bool current = btn.GetAttribute("data-screen") == name;
                if (current)
                    btn.SetAttribute("aria-current", "page");
                else
                    btn.RemoveAttribute("aria-current");
                btn.Dispose();
            }
            if (name != "hud")
            {
                // Move focus to the screen heading so keyboard and screen-reader users land on the new content.
                var heading = Document.Q("#screen-" + name + " h2");
                heading.SetAttribute("tabindex", "-1").Focus();
                heading.Dispose();
            }
            Document.Q("#pause-dialog").CloseModal();
        }

        private void OnStateChanged(SampleGame.State state)
        {
            switch (state)
            {
                case SampleGame.State.Paused:
                    Document.Q("#pause-dialog").ShowModal();
                    Document.Announce("Game paused", true);
                    break;
                case SampleGame.State.Playing:
                    Document.Q("#pause-dialog").CloseModal();
                    if (_screen != "hud")
                        ShowScreen("hud");
                    break;
                case SampleGame.State.Ended:
                    Document.Announce($"Mission over. Final score {Game.Score}.", true);
                    ShowScreen("menu");
                    break;
                case SampleGame.State.Menu:
                    break;
            }
        }

        private void OnSalvaged(SalvageTarget target)
        {
            Toast($"Salvaged {target.name} for {target.Value} points", "ok");
        }

        // ------------------------------------------------------------------ HUD

        private void UpdateHud()
        {
            var doc = Document;
            if (doc == null || !doc.IsCreated)
                return;
            doc.Q("#score").Text = Game.Score.ToString(CultureInfo.InvariantCulture);
            doc.Q("#timer").Text = Mathf.CeilToInt(Game.TimeLeft).ToString(CultureInfo.InvariantCulture);
            doc.Q("#targets").Text = Game.SalvagedCount.ToString(CultureInfo.InvariantCulture);

            int health = Mathf.RoundToInt(Game.Health);
            int energy = Mathf.RoundToInt(Game.Energy);
            doc.Q("#health").SetValue(health).Text = health + "%";
            doc.Q("#energy").SetValue(energy).Text = energy.ToString(CultureInfo.InvariantCulture);
            doc.Q("[data-action=repair]").Disabled = Game.Current != SampleGame.State.Playing || Game.Energy < 20f;
        }

        private void UpdateStatus()
        {
            var rt = Document.Runtime;
            string text;
            switch (Document.RenderMode)
            {
                case HtmlRenderMode.Texture:
                    text = $"HTML-in-Canvas · {rt.Features.textureApi} · {(rt.IsWebGPU ? "WebGPU" : "WebGL2")} · {rt.Features.geometryApi} · {rt.DevicePixelRatio:0.##}x";
                    break;
                case HtmlRenderMode.Overlay:
                    text = "DOM overlay fallback — enable chrome://flags/#canvas-draw-element for HTML-in-Canvas";
                    break;
                default:
                    text = "Editor preview";
                    break;
            }
            Document.Q("#status-mode").Text = text;
        }

        // ------------------------------------------------------------------ toasts

        private void Toast(string message, string kind = "")
        {
            if (Document == null || !Document.IsCreated)
                return;
            string id = "toast-" + (++_toastId);
            Document.Q("#toasts").Append($"<div class=\"toast {kind}\" id=\"{id}\">{Escape(message)}</div>");
            StartCoroutine(RemoveLater(id, 3.5f));
        }

        private IEnumerator RemoveLater(string id, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (Document != null && Document.IsCreated)
                Document.Q("#" + id).Remove();
        }

        // ------------------------------------------------------------------ inventory

        private void BuildInventory()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s_items.Length; i++)
            {
                var it = s_items[i];
                sb.Append($"<li id=\"item-{it.Id}\" class=\"item\" role=\"option\" aria-selected=\"false\" tabindex=\"{(i == 0 ? 0 : -1)}\" " +
                          $"data-cat=\"{it.Category}\" data-name=\"{Escape(it.Name.ToLowerInvariant())}\">" +
                          $"<span class=\"icon\" aria-hidden=\"true\">{it.Icon}</span>" +
                          $"<span class=\"name\">{Escape(it.Name)}</span>" +
                          $"<span class=\"qty\">×{it.Qty}</span></li>");
            }
            Document.Q("#inv-grid").InnerHtml = sb.ToString();
            FilterInventory();
        }

        private void FilterInventory()
        {
            string cat = Document.Q("#inv-filter").Value;
            string search = Document.Q("#inv-search").Value.Trim().ToLowerInvariant();
            int shown = 0;
            foreach (var it in s_items)
            {
                bool match = (cat == "all" || cat == it.Category) && (search.Length == 0 || it.Name.ToLowerInvariant().Contains(search));
                Document.Q("#item-" + it.Id).Hidden = !match;
                if (match)
                    shown++;
            }
            Document.Q("#inv-count").Text = $"{shown} of {s_items.Length} items";
        }

        private List<string> VisibleItemIds()
        {
            var list = new List<string>();
            foreach (var it in s_items)
            {
                if (!Document.Q("#item-" + it.Id).Hidden)
                    list.Add(it.Id);
            }
            return list;
        }

        private static string FindItemId(HtmlEvent e)
        {
            if (e.id != null && e.id.StartsWith("item-", StringComparison.Ordinal))
                return e.id.Substring(5);
            if (string.IsNullOrEmpty(e.path))
                return null;
            foreach (var id in e.path.Split(' '))
            {
                if (id.StartsWith("item-", StringComparison.Ordinal))
                    return id.Substring(5);
            }
            return null;
        }

        private void SelectItem(string id, bool focus)
        {
            _selectedItem = id;
            foreach (var it in s_items)
            {
                bool sel = it.Id == id;
                var el = Document.Q("#item-" + it.Id);
                el.SetAttribute("aria-selected", sel ? "true" : "false");
                el.SetAttribute("tabindex", sel ? "0" : "-1");
                if (sel && focus)
                    el.Focus();
                el.Dispose();
            }
            Document.Q("#inv-grid").SetAttribute("aria-activedescendant", "item-" + id);

            var item = Array.Find(s_items, i => i.Id == id);
            Document.Q("#inv-detail").InnerHtml =
                $"<h3>{item.Icon} {Escape(item.Name)}</h3>" +
                $"<p>{Escape(item.Description)}</p>" +
                $"<p class=\"muted\">Category: {item.Category} · Quantity: {item.Qty}</p>" +
                $"<button type=\"button\" class=\"btn\" data-action=\"use-item\" data-item=\"{item.Id}\">Use</button>";
        }

        private void OnUseItem(HtmlEvent e)
        {
            var id = e.GetData("item");
            var item = Array.Find(s_items, i => i.Id == id);
            if (item.Id == null)
                return;
            Toast($"Used {item.Name}. Nothing happened, but it felt good.", "ok");
            Document.Announce($"Used {item.Name}");
        }

        // ------------------------------------------------------------------ settings

        private void SelectTab(string tabId, bool focus)
        {
            foreach (var t in s_tabs)
            {
                bool selected = t == tabId;
                var tab = Document.Q("#" + t);
                tab.SetAttribute("aria-selected", selected ? "true" : "false");
                tab.SetAttribute("tabindex", selected ? "0" : "-1");
                if (selected && focus)
                    tab.Focus();
                var panel = Document.Q("#" + tab.GetAttribute("aria-controls"));
                panel.Hidden = !selected;
                tab.Dispose();
                panel.Dispose();
            }
        }

        private void ApplySettings()
        {
            var doc = Document;
            var name = doc.Q("#player-name").Value.Trim();
            Game.PilotName = string.IsNullOrEmpty(name) ? "Pilot" : name;
            doc.Q("#pilot").Text = Game.PilotName;
            Game.SpinSpeed = float.TryParse(doc.Q("#spin").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var spin) ? spin : 60f;
            if (Game.Camera != null && float.TryParse(doc.Q("#fov").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fov))
                Game.Camera.fieldOfView = Mathf.Clamp(fov, 40f, 110f);
            Game.Glow = doc.Q("#bloom").Checked;
            ApplyTheme(doc.Q("#theme").Value);
        }

        private void ResetSettings()
        {
            // Native form reset, then push the defaults back into the game.
            Document.Eval("root.querySelector('#settings-form').reset();");
            foreach (var id in new[] { "vol-master", "vol-music", "vol-sfx", "spin", "sens" })
                Document.Q("#" + id + "-out").Text = Document.Q("#" + id).Value;
            ApplySettings();
            Toast("Settings reset to defaults");
        }

        private void ApplyTheme(string theme)
        {
            var app = Document.Q("#app");
            app.RemoveClass("theme-dark").RemoveClass("theme-light").RemoveClass("theme-contrast");
            app.AddClass("theme-" + (string.IsNullOrEmpty(theme) ? "dark" : theme));
            app.Dispose();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
