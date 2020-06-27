﻿using System;

namespace Lidgren.Network
{
    public partial class NetConnection
    {
        private TimeSpan _sentPingTime;
        private TimeSpan _timeoutDeadline = TimeSpan.MaxValue;
        private byte _sentPingNumber;

        /// <summary>
        /// Gets the current average roundtrip time.
        /// </summary>
        public TimeSpan AverageRoundtripTime { get; private set; }

        /// <summary>
        /// Time offset between this peer and the remote peer.
        /// </summary>
        // local time value + m_remoteTimeOffset = remote time value
        public TimeSpan RemoteTimeOffset { get; internal set; }

        // this might happen more than once
        internal void InitializeRemoteTimeOffset(TimeSpan remoteSendTime)
        {
            RemoteTimeOffset = remoteSendTime + (AverageRoundtripTime / 2.0) - NetTime.Now;
        }

        /// <summary>
        /// Gets local time value comparable to <see cref="NetTime.Now"/> from a remote value.
        /// </summary>
        public TimeSpan GetLocalTime(TimeSpan remoteTimestamp)
        {
            return remoteTimestamp - RemoteTimeOffset;
        }

        /// <summary>
        /// Gets the remote time value for a local time value produced by <see cref="NetTime.Now"/>.
        /// </summary>
        public TimeSpan GetRemoteTime(TimeSpan localTimestamp)
        {
            return localTimestamp + RemoteTimeOffset;
        }

        internal void InitializePing()
        {
            var now = NetTime.Now;

            // randomize ping sent time (0.25 - 1.0 x ping interval)
            _sentPingTime = now;
            _sentPingTime -= _peerConfiguration.PingInterval * 0.25; // delay ping for a little while
            _sentPingTime -= MWCRandom.Global.NextSingle() * (_peerConfiguration.PingInterval * 0.75);

            // initially allow a little more time
            _timeoutDeadline = now + (_peerConfiguration._connectionTimeout * 2.0f);

            // make it better, quick :-)
            SendPing();
        }

        internal void SendPing()
        {
            Peer.AssertIsOnLibraryThread();

            _sentPingNumber++;

            _sentPingTime = NetTime.Now;
            NetOutgoingMessage om = Peer.CreateMessage(1);
            om.Write((byte)_sentPingNumber); // truncating to 0-255
            om._messageType = NetMessageType.Ping;

            int len = om.Encode(Peer._sendBuffer, 0, 0);
            Peer.SendPacket(len, RemoteEndPoint, 1, out _);

            Statistics.PacketSent(len, 1);
            Peer.Recycle(om);
        }

        internal void SendPong(byte pingNumber)
        {
            Peer.AssertIsOnLibraryThread();

            NetOutgoingMessage om = Peer.CreateMessage(5);
            om.Write(pingNumber);

            // we should update this value to reflect the exact point in time the packet is SENT
            om.Write(NetTime.Now);
            
            om._messageType = NetMessageType.Pong;

            int len = om.Encode(Peer._sendBuffer, 0, 0);
            Peer.SendPacket(len, RemoteEndPoint, 1, out _);

            Statistics.PacketSent(len, 1);
            Peer.Recycle(om);
        }

        internal void ReceivedPong(TimeSpan now, byte pongNumber, TimeSpan remoteSendTime)
        {
            if (pongNumber != _sentPingNumber)
            {
                Peer.LogVerbose("Ping/Pong mismatch; dropped message?");
                return;
            }

            _timeoutDeadline = now + _peerConfiguration._connectionTimeout;

            var rtt = now - _sentPingTime;
            LidgrenException.Assert(rtt.TotalSeconds >= 0);

            var diff = remoteSendTime + (rtt / 2.0) - now;

            if (AverageRoundtripTime < TimeSpan.Zero)
            {
                RemoteTimeOffset = diff;
                AverageRoundtripTime = rtt;
                Peer.LogDebug(
                    "Initiated average roundtrip time to " + 
                    NetTime.ToReadable(AverageRoundtripTime) + " Remote time is: " + (now + diff));
            }
            else
            {
                AverageRoundtripTime = (AverageRoundtripTime * 0.7) + rtt * 0.3;

                RemoteTimeOffset = ((RemoteTimeOffset * (_sentPingNumber - 1)) + diff) / _sentPingNumber;
                Peer.LogVerbose(
                    "Updated average roundtrip time to " + NetTime.ToReadable(AverageRoundtripTime) + 
                    ", remote time to " + (now + RemoteTimeOffset) + " (ie. diff " + RemoteTimeOffset + ")");
            }

            // update resend delay for all channels
            var resendDelay = ResendDelay;
            foreach (var chan in _sendChannels)
            {
                if (chan is NetReliableSenderChannel rchan)
                    rchan.ResendDelay = resendDelay;
            }

            // m_peer.LogVerbose("Timeout deadline pushed to  " + m_timeoutDeadline);

            // notify the application that average rtt changed
            if (Peer.Configuration.IsMessageTypeEnabled(NetIncomingMessageType.ConnectionLatencyUpdated))
            {
                var updateMsg = Peer.CreateIncomingMessage(NetIncomingMessageType.ConnectionLatencyUpdated, 4);
                updateMsg.SenderConnection = this;
                updateMsg.SenderEndPoint = RemoteEndPoint;
                updateMsg.Write(rtt);
                Peer.ReleaseMessage(updateMsg);
            }
        }
    }
}