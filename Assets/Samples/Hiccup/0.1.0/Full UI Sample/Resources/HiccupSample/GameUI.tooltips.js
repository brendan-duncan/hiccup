// Tooltip dismissal for the game UI, following the WAI-ARIA tooltip pattern: Escape hides every open
// tooltip, and hovering or focusing a trigger brings it back. It runs in the page, so pointer-move traffic
// never crosses the bridge to C#. Like every HtmlDocument script, this is a function body with `panel`,
// `root` and `HUI` in scope; it runs once the document is created and again after Reload().
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
root.addEventListener('focusin', reveal);
