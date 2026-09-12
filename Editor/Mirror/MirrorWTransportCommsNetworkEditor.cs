using Dissonance.Editor;
using Dissonance.Integrations.MirrorWTransport;
using Mirror;
using UnityEditor;
using UnityEngine;

namespace Dissonance.Web.Editor
{
    /// <summary>
    /// Inspector for the Mirror comms network: Dissonance's own logo and live
    /// connection stats, plus the two channel ids, which have to be drawn here
    /// because Dissonance's base editor does not draw serialized fields.
    /// </summary>
    [CustomEditor(typeof(MirrorWTransportCommsNetwork))]
    public class MirrorWTransportCommsNetworkEditor
        : BaseDissonnanceCommsNetworkEditor<
            MirrorWTransportCommsNetwork,
            MirrorWTransportServer,
            MirrorWTransportClient,
            MirrorConn,
            Unit,
            Unit>
    {
        private SerializedProperty _reliableChannel;
        private SerializedProperty _unreliableChannel;

        private void OnEnable()
        {
            _reliableChannel = serializedObject.FindProperty("_reliableChannel");
            _unreliableChannel = serializedObject.FindProperty("_unreliableChannel");
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Channels", EditorStyles.boldLabel);

            serializedObject.Update();
            EditorGUILayout.PropertyField(_reliableChannel, new GUIContent("Reliable Channel"));
            EditorGUILayout.PropertyField(_unreliableChannel, new GUIContent("Unreliable Channel"));
            serializedObject.ApplyModifiedProperties();

            DrawChannelAdvice();
        }

        /// <summary>
        /// Says what the current channel ids will actually do, because the
        /// consequence of getting them wrong is a slow drift in voice latency
        /// rather than anything that looks like a misconfiguration.
        /// </summary>
        private void DrawChannelAdvice()
        {
            var reliable = _reliableChannel.intValue;
            var unreliable = _unreliableChannel.intValue;

            if (reliable == unreliable)
            {
                EditorGUILayout.HelpBox(
                    "Both channels are the same id. Dissonance needs one channel delivered reliably and another unreliably, so these " +
                    "have to differ.",
                    MessageType.Error
                );
                return;
            }

            if (reliable < 0 || unreliable < 0)
            {
                EditorGUILayout.HelpBox("A Mirror channel id cannot be negative.", MessageType.Error);
                return;
            }

            if (unreliable == Channels.Reliable)
            {
                EditorGUILayout.HelpBox(
                    "Voice is set to Mirror's reliable channel (0). Mirror sends spawn and scene messages there and transports are not " +
                    "allowed to make it unreliable, so voice would be retransmitted and would fall behind on a lossy connection.",
                    MessageType.Error
                );
                return;
            }

            var usingDefaults = reliable == DissonanceChannels.DefaultReliable
                             && unreliable == DissonanceChannels.DefaultUnreliable;

            if (usingDefaults)
            {
                EditorGUILayout.HelpBox(
                    "Using Mirror's own two channels, which every transport already delivers correctly. Nothing to configure.\n\n" +
                    "Give Dissonance channel ids of its own if you would rather voice was batched and accounted for separately from the " +
                    "rest of the game's traffic - and then add matching entries to your transport's channel list.",
                    MessageType.None
                );
                return;
            }

            var transport = Transport.active;
            var transportName = transport != null ? transport.GetType().Name : "your transport";

            EditorGUILayout.HelpBox(
                $"These are channel ids beyond Mirror's own two, so {transportName} has to be told about them: its channel list needs at " +
                $"least {Mathf.Max(reliable, unreliable) + 1} entries, with element {reliable} Reliable and element {unreliable} " +
                "Unreliable. A channel id past the end of that list is delivered reliably, which would make voice drift behind on a " +
                "lossy connection.\n\nDissonance checks this at startup for transports that report their channel delivery, " +
                "MirrorWTransport included.",
                MessageType.Info
            );
        }
    }
}
