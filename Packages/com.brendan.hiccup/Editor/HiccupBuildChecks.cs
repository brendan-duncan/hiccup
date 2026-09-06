using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Hiccup.Editor
{
    /// <summary>Prints set-up reminders when building for the web.</summary>
    internal class HiccupBuildChecks : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
                return;

            var template = PlayerSettings.WebGL.template;
            if (!template.Contains("Hiccup"))
            {
                Debug.Log("[Hiccup] Tip: the 'Hiccup' WebGL template (Assets/WebGLTemplates/Hiccup) carries the Origin Trial meta tag and a full-window canvas. " +
                          $"Current template: {template}. Change it under Project Settings > Player > Resolution and Presentation.");
            }
            Debug.Log("[Hiccup] HTML-in-Canvas needs Chrome 148+ with chrome://flags/#canvas-draw-element enabled, or an Origin Trial token for your origin " +
                      "(https://developer.chrome.com/origintrials/#/view_trial/3478467762190286849). Other browsers fall back to the DOM overlay.");
        }
    }
}
