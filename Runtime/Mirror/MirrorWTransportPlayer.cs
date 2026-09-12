using Mirror;
using UnityEngine;

namespace Dissonance.Integrations.MirrorWTransport
{
    /// <summary>
    /// Put this on the player prefab so Dissonance can follow each player around
    /// the scene for positional voice.
    /// </summary>
    /// <remarks>
    /// Dissonance identifies players by name, and Mirror identifies them by
    /// network object, so something has to tie the two together on every peer.
    /// The player name is a SyncVar, which means a client joining a game in
    /// progress has the right name for everyone already spawned without any
    /// further round trips.
    /// </remarks>
    [RequireComponent(typeof(NetworkIdentity))]
    public class MirrorWTransportPlayer
        : NetworkBehaviour, IDissonancePlayer
    {
        private static readonly Log Log = Logs.Create(LogCategory.Network, "Mirror WTransport Player");

        private DissonanceComms _comms;

        public bool IsTracking { get; private set; }

        [SyncVar] private string _playerId;

        public string PlayerId => _playerId;

        public virtual Vector3 Position => transform.position;

        public virtual Quaternion Rotation => transform.rotation;

        public NetworkPlayerType Type
        {
            get
            {
                if (_comms == null || _playerId == null)
                    return NetworkPlayerType.Unknown;

                return _comms.LocalPlayerName == _playerId ? NetworkPlayerType.Local : NetworkPlayerType.Remote;
            }
        }

        public void OnEnable()
        {
            _comms = FindComms();
        }

        public void OnDisable()
        {
            if (IsTracking)
                StopTracking();
        }

        public void OnDestroy()
        {
            if (_comms != null)
                _comms.LocalPlayerNameChanged -= SetPlayerName;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // A player that is already named - anyone who was in the game before
            // this client joined - can be tracked immediately.
            if (!string.IsNullOrEmpty(PlayerId))
                StartTracking();
        }

        public override void OnStartLocalPlayer()
        {
            base.OnStartLocalPlayer();

            var comms = FindComms();
            if (comms == null)
            {
                throw Log.CreateUserErrorException(
                    "cannot find the DissonanceComms component in the scene",
                    "not placing a DissonanceComms component on a game object in the scene",
                    "https://dissonance.readthedocs.io/en/latest/Basics/Quick-Start-MirrorIgnorance/",
                    "e0a1c4f6-3d49-4f71-8f1a-9c4c2c1ab8d7"
                );
            }

            _comms = comms;

            Log.Debug("Tracking the local player as '{0}'", comms.LocalPlayerName);

            if (comms.LocalPlayerName != null)
                SetPlayerName(comms.LocalPlayerName);

            // Subscribing also covers the case where the name has not been set
            // yet: the event fires as soon as it is.
            comms.LocalPlayerNameChanged += SetPlayerName;
        }

        private void SetPlayerName(string playerName)
        {
            // Every peer needs the name before it can start tracking, so the
            // owning client tells the server and the server tells everyone:
            // client -> server -> clients.

            if (IsTracking)
                StopTracking();

            _playerId = playerName;
            StartTracking();

            if (isLocalPlayer)
                CmdSetPlayerName(playerName);
        }

        [Command]
        private void CmdSetPlayerName(string playerName)
        {
            _playerId = playerName;

            RpcSetPlayerName(playerName);
        }

        [ClientRpc]
        private void RpcSetPlayerName(string playerName)
        {
            // The owner already applied this locally before sending the command.
            if (!isLocalPlayer)
                SetPlayerName(playerName);
        }

        private void StartTracking()
        {
            if (IsTracking)
                throw Log.CreatePossibleBugException("Attempted to start player tracking, but tracking is already started", "9a6b3f1e-0c84-4c0e-9f2a-6b1f4d3a5c77");

            if (_comms == null)
                return;

            _comms.TrackPlayerPosition(this);
            IsTracking = true;
        }

        private void StopTracking()
        {
            if (!IsTracking)
                throw Log.CreatePossibleBugException("Attempted to stop player tracking, but tracking is not started", "b4c2e5a8-91d7-4a63-8f10-2d5e7c9b4f31");

            // Clear the flag even if the comms object has gone; leaving it set
            // would make the next StartTracking throw.
            IsTracking = false;

            if (_comms != null)
                _comms.StopTracking(this);
        }

        private static DissonanceComms FindComms()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindAnyObjectByType<DissonanceComms>();
#else
            return Object.FindObjectOfType<DissonanceComms>();
#endif
        }
    }
}
