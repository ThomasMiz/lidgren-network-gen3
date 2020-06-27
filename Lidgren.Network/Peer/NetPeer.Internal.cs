﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        private object InitMutex { get; } = new object();

        private Thread? _networkThread;
        private EndPoint _senderRemote;
        private uint _frameCounter;
        private TimeSpan _lastHeartbeat;
        private TimeSpan _lastSocketBind = TimeSpan.MinValue;
        private AutoResetEvent? _messageReceivedEvent;
        private List<(SynchronizationContext SyncContext, SendOrPostCallback Callback)>? _receiveCallbacks;
        internal NetIncomingMessage? _readHelperMessage;
        internal byte[] _sendBuffer = Array.Empty<byte>();
        internal byte[] _receiveBuffer = Array.Empty<byte>();

        private NetQueue<NetIncomingMessage> ReleasedIncomingMessages { get; } =
            new NetQueue<NetIncomingMessage>(4);

        internal NetQueue<(IPEndPoint EndPoint, NetOutgoingMessage Message)> UnsentUnconnectedMessages { get; } =
            new NetQueue<(IPEndPoint, NetOutgoingMessage)>(2);

        internal Dictionary<IPEndPoint, NetConnection> Handshakes { get; } =
            new Dictionary<IPEndPoint, NetConnection>();

        internal bool _executeFlushSendQueue;

        /// <summary>
        /// Gets the socket.
        /// </summary>
        public Socket? Socket { get; private set; }

        /// <summary>
        /// Call this to register a callback for when a new message arrives
        /// </summary>
        public void RegisterReceivedCallback(SendOrPostCallback callback, SynchronizationContext? syncContext = null)
        {
            if (syncContext == null)
                syncContext = SynchronizationContext.Current;

            if (syncContext == null)
                throw new LidgrenException("Need a SynchronizationContext to register callback on correct thread!");

            if (_receiveCallbacks == null)
                _receiveCallbacks = new List<(SynchronizationContext, SendOrPostCallback)>(1);

            _receiveCallbacks.Add((syncContext, callback));
        }

        /// <summary>
        /// Call this to unregister a callback, but remember to do it in the same synchronization context!
        /// </summary>
        public void UnregisterReceivedCallback(SendOrPostCallback callback)
        {
            if (_receiveCallbacks == null)
                return;

            // remove all callbacks regardless of sync context
            _receiveCallbacks.RemoveAll((x) => x.Callback.Equals(callback));
        }

        internal void ReleaseMessage(NetIncomingMessage message)
        {
            LidgrenException.Assert(message.MessageType != NetIncomingMessageType.Error);

            if (message.IsFragment)
            {
                HandleReleasedFragment(message);
                return;
            }

            ReleasedIncomingMessages.Enqueue(message);

            _messageReceivedEvent?.Set();

            if (_receiveCallbacks == null)
                return;

            foreach (var (SyncContext, Callback) in _receiveCallbacks)
            {
                try
                {
                    SyncContext.Post(Callback, this);
                }
                catch (Exception ex)
                {
                    LogWarning("Receive callback exception:" + ex);
                }
            }
        }

        private Socket BindSocket(bool reuseAddress)
        {
            var now = NetTime.Now;
            if (Socket != null && now - _lastSocketBind < TimeSpan.FromSeconds(1.0))
            {
                LogDebug("Suppressed socket rebind; last bound " + (now - _lastSocketBind) + " ago");
                return Socket; // only allow rebind once every second
            }
            _lastSocketBind = now;

            var mutex = new Mutex(false, "Global\\lidgrenSocketBind");
            try
            {
                mutex.WaitOne();

                if (Socket == null)
                    Socket = new Socket(
                        Configuration.LocalAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

                if (reuseAddress)
                    Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                Socket.ReceiveBufferSize = Configuration.ReceiveBufferSize;
                Socket.SendBufferSize = Configuration.SendBufferSize;
                Socket.Blocking = false;

                if (Configuration.DualStack)
                {
                    if (Configuration.LocalAddress.AddressFamily != AddressFamily.InterNetworkV6)
                    {
                        LogWarning(
                            "Configuration specifies dual stack but " +
                            "does not use IPv6 local address; dual stack will not work.");
                    }
                    else
                    {
                        Socket.DualMode = true;
                    }
                }

                var ep = new IPEndPoint(Configuration.LocalAddress, reuseAddress ? Port : Configuration.Port);
                Socket.Bind(ep);

                try
                {
                    const uint IOC_IN = 0x80000000;
                    const uint IOC_VENDOR = 0x18000000;
                    uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                    Socket.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);
                }
                catch
                {
                    // ignore; SIO_UDP_CONNRESET not supported on this platform
                }
            }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }

            var boundEp = (IPEndPoint)Socket.LocalEndPoint;
            LogDebug("Socket bound to " + boundEp + ": " + Socket.IsBound);
            Port = boundEp.Port;
            return Socket;
        }

        private void InitializeNetwork()
        {
            lock (InitMutex)
            {
                Configuration.Lock();

                if (Status == NetPeerStatus.Running)
                    return;

                if (Configuration._enableUPnP)
                    UPnP = new NetUPnP(this);

                InitializePools();

                ReleasedIncomingMessages.Clear();
                UnsentUnconnectedMessages.Clear();
                Handshakes.Clear();

                // bind to socket
                var socket = BindSocket(false);

                _receiveBuffer = new byte[Configuration.ReceiveBufferSize];
                _sendBuffer = new byte[Configuration.SendBufferSize];
                _readHelperMessage = new NetIncomingMessage(NetIncomingMessageType.Error);
                _readHelperMessage.Data = _receiveBuffer;

                var epBytes = MemoryMarshal.AsBytes(socket.LocalEndPoint.ToString().AsSpan());
                var macBytes = NetUtility.GetPhysicalAddress()?.GetAddressBytes() ?? Array.Empty<byte>();
                var combined = new byte[epBytes.Length + macBytes.Length];
                epBytes.CopyTo(combined);
                macBytes.CopyTo(combined.AsSpan(epBytes.Length));

                Span<byte> hash = stackalloc byte[NetBitWriter.ByteCountForBits(NetUtility.Sha256.HashSize)];
                if (!NetUtility.Sha256.TryComputeHash(combined, hash, out _))
                    throw new Exception();
                UniqueIdentifier = BitConverter.ToInt64(hash);

                Status = NetPeerStatus.Running;
            }
        }

        private void NetworkLoop()
        {
            AssertIsOnLibraryThread();

            LogDebug("Network thread started");

            //
            // Network loop
            //
            do
            {
                try
                {
                    Heartbeat();
                }
                catch (Exception ex)
                {
                    LogWarning(ex.ToString());
                }
            } while (Status == NetPeerStatus.Running);

            //
            // perform shutdown
            //
            ExecutePeerShutdown();
        }

        private void ExecutePeerShutdown()
        {
            AssertIsOnLibraryThread();

            LogDebug("Shutting down...");

            // disconnect and make one final heartbeat
            var connections = NetConnectionListPool.Rent();
            try
            {
                lock (Connections)
                    connections.AddRange(Connections);

                lock (Handshakes)
                    connections.AddRange(Handshakes.Values);

                foreach (NetConnection conn in connections)
                    conn?.Shutdown(_shutdownReason);
            }
            finally
            {
                NetConnectionListPool.Return(connections);
            }

            FlushDelayedPackets();

            // one final heartbeat, will send stuff and do disconnect
            Heartbeat();

            Thread.Sleep(10);

            lock (InitMutex)
            {
                try
                {
                    if (Socket != null)
                    {
                        try
                        {
                            Socket.Shutdown(SocketShutdown.Receive);
                        }
                        catch (Exception ex)
                        {
                            LogDebug("Socket.Shutdown exception: " + ex.ToString());
                        }

                        try
                        {
                            Socket.Close(2); // 2 seconds timeout
                        }
                        catch (Exception ex)
                        {
                            LogDebug("Socket.Close exception: " + ex.ToString());
                        }
                    }
                }
                finally
                {
                    Socket = null;
                    Status = NetPeerStatus.NotRunning;
                    LogDebug("Shutdown complete");

                    // wake up any threads waiting for server shutdown
                    _messageReceivedEvent?.Set();
                }

                _receiveBuffer = Array.Empty<byte>();
                _sendBuffer = Array.Empty<byte>();
                UnsentUnconnectedMessages.Clear();
                Connections.Clear();
                ConnectionLookup.Clear();
                Handshakes.Clear();
            }
        }

        private void Heartbeat()
        {
            AssertIsOnLibraryThread();

            TimeSpan now = NetTime.Now;
            TimeSpan delta = now - _lastHeartbeat;

            int maxCHBpS = 1250 - Connections.Count;
            if (maxCHBpS < 250)
                maxCHBpS = 250;

            // max connection heartbeats/second max
            if (delta > TimeSpan.FromTicks(TimeSpan.TicksPerSecond / maxCHBpS) ||
                delta < TimeSpan.Zero)
            {
                _frameCounter++;
                _lastHeartbeat = now;

                // do handshake heartbeats
                if ((_frameCounter % 3) == 0)
                {
                    lock (Handshakes)
                    {
                        foreach (var kvp in Handshakes)
                        {
                            var conn = kvp.Value;
                            conn.UnconnectedHeartbeat(now);

                            if (conn._internalStatus == NetConnectionStatus.Connected ||
                                conn._internalStatus == NetConnectionStatus.Disconnected)
                            {
#if DEBUG
                                // sanity check
                                if (conn._internalStatus == NetConnectionStatus.Disconnected &&
                                    Handshakes.ContainsKey(conn.RemoteEndPoint))
                                {
                                    LogWarning("Sanity fail! Handshakes list contained disconnected connection!");
                                    Handshakes.Remove(conn.RemoteEndPoint);
                                }
#endif
                                break; // collection has been modified
                            }
                        }
                    }
                }

                SendDelayedPackets();

                // update m_executeFlushSendQueue
                if (Configuration._autoFlushSendQueue)
                    _executeFlushSendQueue = true;

                // do connection heartbeats
                lock (Connections)
                {
                    foreach (NetConnection conn in Connections)
                    {
                        conn.Heartbeat(now, _frameCounter);
                        if (conn._internalStatus == NetConnectionStatus.Disconnected)
                        {
                            //
                            // remove connection
                            //
                            Connections.Remove(conn);
                            ConnectionLookup.Remove(conn.RemoteEndPoint);
                            break; // can't continue iteration here
                        }
                    }
                }
                _executeFlushSendQueue = false;

                // send unsent unconnected messages
                while (UnsentUnconnectedMessages.TryDequeue(out var unsent))
                {
                    NetOutgoingMessage om = unsent.Message;

                    int len = om.Encode(_sendBuffer, 0, 0);
                    SendPacket(len, unsent.EndPoint, 1, out bool connReset);

                    Interlocked.Decrement(ref om._recyclingCount);
                    if (om._recyclingCount <= 0)
                        Recycle(om);
                }
            }

            //
            // read from socket
            //
            if (Socket == null)
                return;

            // wait up to 10 ms for data to arrive
            if (!Socket.Poll(10000, SelectMode.SelectRead))
                return;

            //if (m_socket.Available < 1)
            //	return;

            // update now
            now = NetTime.Now;

            do
            {
                int bytesReceived = 0;
                try
                {
                    bytesReceived = Socket.ReceiveFrom(
                        _receiveBuffer, 0, _receiveBuffer.Length, SocketFlags.None, ref _senderRemote);
                }
                catch (SocketException sx)
                {
                    switch (sx.SocketErrorCode)
                    {
                        case SocketError.ConnectionReset:
                            // connection reset by peer, aka connection forcibly closed aka "ICMP port unreachable" 
                            // we should shut down the connection; but m_senderRemote seemingly cannot be trusted,
                            // so which connection should we shut down?!
                            // So, what to do?
                            LogWarning("ConnectionReset");
                            return;

                        case SocketError.NotConnected:
                            // socket is unbound; try to rebind it (happens on mobile when process goes to sleep)
                            BindSocket(true);
                            return;

                        default:
                            LogWarning("Socket exception: " + sx.ToString());
                            return;
                    }
                }

                if (bytesReceived < NetConstants.HeaderByteSize)
                    return;

                //LogVerbose("Received " + bytesReceived + " bytes");

                var senderEndPoint = (IPEndPoint)_senderRemote;

                if (UPnP != null && UPnP.Status == UPnPStatus.Discovering)
                    if (SetupUpnp(UPnP, now, _receiveBuffer.AsSpan(0, bytesReceived)))
                        return;

                ConnectionLookup.TryGetValue(senderEndPoint, out NetConnection? sender);

                //
                // parse packet into messages
                //
                int numMessages = 0;
                int numFragments = 0;
                int offset = 0;
                while ((bytesReceived - offset) >= NetConstants.HeaderByteSize)
                {
                    // decode header
                    //  8 bits - NetMessageType
                    //  1 bit  - Fragment?
                    // 15 bits - Sequence number
                    // 16 bits - Payload bit length

                    numMessages++;

                    var type = (NetMessageType)_receiveBuffer[offset++];

                    byte low = _receiveBuffer[offset++];
                    byte high = _receiveBuffer[offset++];

                    bool isFragment = (low & 1) == 1;
                    var sequenceNumber = (ushort)((low >> 1) | (high << 7));

                    numFragments++;

                    var payloadBitLength = (ushort)(_receiveBuffer[offset++] | (_receiveBuffer[offset++] << 8));
                    int payloadByteLength = NetBitWriter.ByteCountForBits(payloadBitLength);

                    if (bytesReceived - offset < payloadByteLength)
                    {
                        LogWarning(
                            "Malformed packet; stated payload length " + payloadByteLength +
                            ", remaining bytes " + (bytesReceived - offset));
                        return;
                    }

                    if (type >= NetMessageType.Unused1 && type <= NetMessageType.Unused29)
                    {
                        ThrowOrLog("Unexpected NetMessageType: " + type);
                        return;
                    }

                    try
                    {
                        if (type >= NetMessageType.LibraryError)
                        {
                            if (sender != null)
                                sender.ReceivedLibraryMessage(type, offset, payloadByteLength);
                            else
                                ReceivedUnconnectedLibraryMessage(now, senderEndPoint, type, offset, payloadByteLength);
                        }
                        else
                        {
                            if (sender == null &&
                                !Configuration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData))
                                return; // dropping unconnected message since it's not enabled

                            var msg = CreateIncomingMessage(NetIncomingMessageType.Data, payloadByteLength);
                            msg._baseMessageType = type;
                            msg.IsFragment = isFragment;
                            msg.ReceiveTime = now;
                            msg.SequenceNumber = sequenceNumber;
                            msg.SenderConnection = sender;
                            msg.SenderEndPoint = senderEndPoint;
                            msg.BitLength = payloadBitLength;

                            Buffer.BlockCopy(_receiveBuffer, offset, msg.Data, 0, payloadByteLength);

                            if (sender != null)
                            {
                                if (type == NetMessageType.Unconnected)
                                {
                                    // We're connected; but we can still send unconnected messages to this peer
                                    msg.MessageType = NetIncomingMessageType.UnconnectedData;
                                    ReleaseMessage(msg);
                                }
                                else
                                {
                                    // connected application (non-library) message
                                    sender.ReceivedMessage(msg);
                                }
                            }
                            else
                            {
                                // at this point we know the message type is enabled
                                // unconnected application (non-library) message
                                msg.MessageType = NetIncomingMessageType.UnconnectedData;
                                ReleaseMessage(msg);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError("Packet parsing error: " + ex.Message + " from " + senderEndPoint);
                    }
                    offset += payloadByteLength;
                }

                Statistics.PacketReceived(bytesReceived, numMessages, numFragments);
                sender?.Statistics.PacketReceived(bytesReceived, numMessages, numFragments);

            } while (Socket.Available > 0);
        }

        private bool SetupUpnp(NetUPnP upnp, TimeSpan now, ReadOnlySpan<byte> data)
        {
            if (now >= upnp.DiscoveryDeadline ||
                data.Length <= 32)
                return false;

            // is this an UPnP response?
            string resp = System.Text.Encoding.ASCII.GetString(data); // TODO: optimize with stackalloc
            if (resp.Contains("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) ||
                resp.Contains("UPnP/1.0", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var locationLine = resp.AsSpan()
                        .Slice(resp.IndexOf("location:", StringComparison.OrdinalIgnoreCase) + 9);

                    var location = locationLine
                        .Slice(0, locationLine.IndexOf("\r", StringComparison.Ordinal))
                        .Trim();

                    upnp.ExtractServiceUri(new Uri(location.ToString()));
                }
                catch (Exception ex)
                {
                    LogDebug("Failed to parse UPnP response: " + ex.ToString());

                    // don't try to parse this packet further
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// You need to call this to send queued messages if
        /// <see cref="NetPeerConfiguration.AutoFlushSendQueue"/> is false.
        /// </summary>
        public void FlushSendQueue()
        {
            _executeFlushSendQueue = true;
        }

        internal void HandleIncomingDiscoveryRequest(
            TimeSpan now, IPEndPoint senderEndPoint, int offset, int payloadByteLength)
        {
            if (!Configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryRequest))
                return;

            var dm = CreateIncomingMessage(NetIncomingMessageType.DiscoveryRequest, payloadByteLength);
            if (payloadByteLength > 0)
                Buffer.BlockCopy(_receiveBuffer, offset, dm.Data, 0, payloadByteLength);

            dm.ReceiveTime = now;
            dm.SenderEndPoint = senderEndPoint;
            dm.BitLength = payloadByteLength * 8;
            ReleaseMessage(dm);
        }

        internal void HandleIncomingDiscoveryResponse(
            TimeSpan now, IPEndPoint senderEndPoint, int offset, int payloadByteLength)
        {
            if (!Configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryResponse))
                return;

            var dr = CreateIncomingMessage(NetIncomingMessageType.DiscoveryResponse, payloadByteLength);
            if (payloadByteLength > 0)
                Buffer.BlockCopy(_receiveBuffer, offset, dr.Data, 0, payloadByteLength);

            dr.ReceiveTime = now;
            dr.SenderEndPoint = senderEndPoint;
            dr.BitLength = payloadByteLength * 8;
            ReleaseMessage(dr);
        }

        private void ReceivedUnconnectedLibraryMessage(
            TimeSpan now, IPEndPoint senderEndPoint, NetMessageType tp, int offset, int payloadByteLength)
        {
            if (Handshakes.TryGetValue(senderEndPoint, out NetConnection? shake))
            {
                shake.ReceivedHandshake(now, tp, offset, payloadByteLength);
                return;
            }

            //
            // Library message from a completely unknown sender; lets just accept Connect
            //
            switch (tp)
            {
                case NetMessageType.Discovery:
                    HandleIncomingDiscoveryRequest(now, senderEndPoint, offset, payloadByteLength);
                    return;

                case NetMessageType.DiscoveryResponse:
                    HandleIncomingDiscoveryResponse(now, senderEndPoint, offset, payloadByteLength);
                    return;

                case NetMessageType.NatIntroduction:
                    if (Configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatIntroduction(offset);
                    return;

                case NetMessageType.NatPunchMessage:
                    if (Configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatPunch(offset, senderEndPoint);
                    return;

                case NetMessageType.ConnectResponse:
                    lock (Handshakes)
                    {
                        foreach (var hs in Handshakes)
                        {
                            if (!hs.Key.Address.Equals(senderEndPoint.Address) ||
                                !hs.Value._connectionInitiator)
                                continue;

                            //
                            // We are currently trying to connection to XX.XX.XX.XX:Y
                            // ... but we just received a ConnectResponse from XX.XX.XX.XX:Z
                            // Lets just assume the router decided to use this port instead
                            //
                            var hsconn = hs.Value;
                            ConnectionLookup.Remove(hs.Key);
                            Handshakes.Remove(hs.Key);

                            LogDebug("Detected host port change; rerouting connection to " + senderEndPoint);
                            hsconn.MutateEndPoint(senderEndPoint);

                            ConnectionLookup.Add(senderEndPoint, hsconn);
                            Handshakes.Add(senderEndPoint, hsconn);

                            hsconn.ReceivedHandshake(now, tp, offset, payloadByteLength);
                            return;
                        }
                    }

                    LogWarning("Received unhandled library message " + tp + " from " + senderEndPoint);
                    return;

                case NetMessageType.Connect:
                    if (!Configuration.AcceptIncomingConnections)
                    {
                        LogWarning("Received Connect, but we're not accepting incoming connections.");
                        return;
                    }
                    // handle connect
                    // It's someone wanting to shake hands with us!

                    int reservedSlots = Handshakes.Count + Connections.Count;
                    if (reservedSlots >= Configuration._maximumConnections)
                    {
                        // server full
                        NetOutgoingMessage full = CreateMessage("Server full");
                        full._messageType = NetMessageType.Disconnect;
                        SendLibraryMessage(full, senderEndPoint);
                        return;
                    }

                    // Ok, start handshake!
                    NetConnection conn = new NetConnection(this, senderEndPoint);
                    conn._internalStatus = NetConnectionStatus.ReceivedInitiation;
                    Handshakes.Add(senderEndPoint, conn);
                    conn.ReceivedHandshake(now, tp, offset, payloadByteLength);
                    return;

                case NetMessageType.Disconnect:
                    // this is probably ok
                    LogVerbose("Received Disconnect from unconnected source: " + senderEndPoint);
                    return;

                default:
                    LogWarning("Received unhandled library message " + tp + " from " + senderEndPoint);
                    return;
            }
        }

        internal void AcceptConnection(NetConnection conn)
        {
            // LogDebug("Accepted connection " + conn);
            conn.InitExpandMTU(NetTime.Now);

            if (!Handshakes.Remove(conn.RemoteEndPoint))
                LogWarning("AcceptConnection called but m_handshakes did not contain it!");

            lock (Connections)
            {
                if (Connections.Contains(conn))
                {
                    LogWarning("AcceptConnection called but m_connection already contains it!");
                }
                else
                {
                    Connections.Add(conn);
                    ConnectionLookup.Add(conn.RemoteEndPoint, conn);
                }
            }
        }

        [Conditional("DEBUG")]
        internal void AssertIsOnLibraryThread()
        {
            var ct = Thread.CurrentThread;
            if (ct != _networkThread)
                throw new LidgrenException(
                    "Executing on wrong thread. " +
                    "Should be library thread (is " + ct.Name + ", ManagedThreadId " + ct.ManagedThreadId + ")");
        }

        internal NetIncomingMessage SetupReadHelperMessage(int offset, int payloadLength)
        {
            AssertIsOnLibraryThread();

            if (_readHelperMessage == null)
                throw new InvalidOperationException("The peer is not initialized.");

            _readHelperMessage.BitLength = (offset + payloadLength) * 8;
            _readHelperMessage.BitPosition = offset * 8;
            return _readHelperMessage;
        }
    }
}