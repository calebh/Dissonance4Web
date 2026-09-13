using UnityEditor;
using UnityEngine;

namespace Dissonance.Web.Editor
{
    /// <summary>
    /// Inspector for <see cref="DissonanceWebAudio"/>. Says what the component
    /// will and will not do on the active build target, and reports the live state
    /// of the browser microphone while playing in a Web player.
    /// </summary>
    [CustomEditor(typeof(DissonanceWebAudio))]
    public class DissonanceWebAudioEditor
        : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying;
        }

        public override void OnInspectorGUI()
        {
            var targetingWeb = EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;

            if (targetingWeb)
            {
                EditorGUILayout.HelpBox(
                    "WebGL is the active build target, so this component will replace Dissonance's microphone capture, preprocessing " +
                    "and playback in the player. It does nothing in the editor, where Dissonance's own pipeline works.",
                    MessageType.Info
                );

                if (!DissonanceCorePatcher.RequiredPatchesApplied(out _))
                {
                    EditorGUILayout.HelpBox(
                        "Dissonance's core has not been patched for the web yet. Run Tools > Dissonance 4 Web > Patch Dissonance For Web.",
                        MessageType.Error
                    );

                    if (GUILayout.Button("Patch Dissonance For Web"))
                        DissonanceCorePatcher.Apply();
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "The active build target is not WebGL, so this component does nothing. Leave it in the scene: it is what makes the " +
                    "same scene work in a browser.",
                    MessageType.None
                );
            }

            EditorGUILayout.Space();

            DrawDefaultInspector();

            if (!Application.isPlaying)
                return;

            var microphone = ((DissonanceWebAudio)target).Microphone;
            if (microphone == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Browser microphone", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Access", microphone.IsAccessRequested ? "Requested" : "Not requested (listening only)");
            EditorGUILayout.LabelField("State", microphone.State.ToString());

            var error = microphone.Error;
            if (!string.IsNullOrEmpty(error))
                EditorGUILayout.HelpBox(error, MessageType.Warning);
        }
    }
}
