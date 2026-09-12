using Dissonance.Editor;
using Dissonance.Integrations.MirrorWTransport;
using UnityEditor;

namespace Dissonance.Web.Editor
{
    /// <summary>
    /// Gives the Mirror comms network the same inspector Dissonance's own
    /// integrations get: the logo, the live connection status and the peer list.
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
    }
}
