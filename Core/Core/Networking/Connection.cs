using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Prometheus;
using QuantumCore.Core.Constants;
using QuantumCore.Core.Packets;
using QuantumCore.Core.Utils;
using QuantumCore.API;
using Serilog;

namespace QuantumCore.Core.Networking
{
    public abstract class Connection : IConnection
    {
        private TcpClient _client;

        private IPacketManager _packetManager;

        private BinaryWriter _writer;

        private long _lastHandshakeTime;

        private static Histogram _packetsReceived;
        private static Histogram _packetsSent;

        public Guid Id { get; }
        public uint Handshake { get; private set; }
        public bool Handshaking { get; private set; }
        public EPhases Phase { get; private set; }
        
        protected Connection()
        {
            Id = Guid.NewGuid();

            if (_packetsReceived == null)
            {
                CreateStaticMetrics();
            }
        }

        private static void CreateStaticMetrics()
        {
            if (_packetsReceived != null)
            {
                return;
            }
            
            _packetsReceived =
                Metrics.CreateHistogram("packets_received_bytes", "Received packets in bytes");
            _packetsSent =
                Metrics.CreateHistogram("packets_sent_bytes", "Sent packets in bytes");
        }

        public void Init(TcpClient client, IPacketManager packetManager)
        {
            _client = client;
            _packetManager = packetManager;
        }

        protected abstract void OnHandshakeFinished();

        protected abstract void OnClose();

        protected abstract void OnReceive(object packet);

        protected abstract long GetServerTime();
        
        public async void Start()
        {
            Log.Information($"New connection from {_client.Client.RemoteEndPoint}");

            var stream = _client.GetStream();
            _writer = new BinaryWriter(stream);

            StartHandshake();

            var buffer = new byte[1];
            var packetTotalSize = 1;

            while (true)
            {
                try
                {
                    var read = await stream.ReadAsync(buffer, 0, 1);
                    if (read != 1)
                    {
                        Log.Information("Failed to read, closing connection");
                        _client.Close();
                        break;
                    }

                    packetTotalSize = 1;
                    
                    var packetDetails = _packetManager.GetIncomingPacket(buffer[0]);
                    if (packetDetails == null)
                    {
                        Log.Information($"Received unknown header {buffer[0]:X2}");
                        _client.Close();
                        break;
                    }

                    var data = new byte[packetDetails.Size - 1];
                    read = await stream.ReadAsync(data, 0, data.Length);

                    packetTotalSize += read;

                    if (read != data.Length)
                    {
                        Log.Information("Failed to read, closing connection");
                        _client.Close();
                        break;
                    }

                    var packet = Activator.CreateInstance(packetDetails.Type);
                    var subHeader = packetDetails.Deserialize(packet, data);

                    if (packetDetails.IsSubHeader)
                    {
                        packetDetails =
                            _packetManager.GetIncomingPacket((ushort) (packetDetails.Header << 8 | subHeader));
                        if (packetDetails == null)
                        {
                            Log.Information($"Received unknown sub header {subHeader:X2} for header {buffer[0]:X2}");
                            _client.Close();
                            break;
                        }

                        packet = Activator.CreateInstance(packetDetails.Type);

                        var subData = new byte[packetDetails.Size - data.Length - 1];
                        read = await stream.ReadAsync(subData, 0, subData.Length);
                        
                        packetTotalSize += read;

                        packetDetails.Deserialize(packet, data.Concat(subData).ToArray());
                    }
                    
                    // Check if packet has dynamic data
                    if (packetDetails.IsDynamic)
                    {
                        // Calculate dynamic size
                        var size = packetDetails.GetDynamicSize(packet) - (int)packetDetails.Size;
                        
                        // Read dynamic data
                        var dynamicData = new byte[size];
                        read = await stream.ReadAsync(dynamicData, 0, size);
                        packetTotalSize += read;
                        if (read != size)
                        {
                            Log.Information($"Failed to read dynamic data read {read} but expected {size}");
                            _client.Close();
                            break;
                        }
                        
                        // Copy and deserialize dynamic data into the packet object
                        packetDetails.DeserializeDynamic(packet, dynamicData);
                    }
                    
                    // Check if packet has a sequence
                    if (packetDetails.HasSequence)
                    {
                        var sequence = new byte[1];
                        read = await stream.ReadAsync(sequence, 0, 1);
                        packetTotalSize += read;
                        if (read != 1)
                        {
                            _client.Close();
                            break;
                        }
                        //Log.Debug($"Read sequence {sequence[0]:X2}");
                    }
                    
                    //Log.Debug($"Recv {packet}");
                    _packetsReceived.Observe(packetTotalSize);

                    OnReceive(packet);
                }
                catch (Exception e)
                {
                    Log.Information(e, "Failed to process network data");
                    _client.Close();
                    break;
                }
            }

            Close();
        }

        public void Close()
        {
            _client.Close();
            OnClose();
        }

        public void Send(object packet)
        {
            if (!_client.Connected)
            {
                Log.Warning("Tried to send data to a closed connection");
                return;
            }
            
            // Verify that the packet is a packet and registered
            var attr = packet.GetType().GetCustomAttribute<PacketAttribute>();
            if (attr == null) throw new ArgumentException("Given packet is not a packet", nameof(packet));

            if (!_packetManager.IsRegisteredOutgoing(packet.GetType()))
                throw new ArgumentException("Given packet is not a registered outgoing packet", nameof(packet));

            var packetDetails = _packetManager.GetOutgoingPacket(attr.Header);
            
            // Check if packet have sub packets
            if (packetDetails.IsSubHeader)
            {
                var subAttr = packet.GetType().GetCustomAttribute<SubPacketAttribute>();
                Debug.Assert(subAttr != null);
                packetDetails = _packetManager.GetOutgoingPacket((ushort) (attr.Header << 8 | subAttr.SubHeader));
            }
            
            // Check if packet has dynamic data
            if (packetDetails.IsDynamic)
            {
                packetDetails.UpdateDynamicSize(packet, packetDetails.Size);
            }

            // Serialize object
            var data = packetDetails.Serialize(packet);
            Log.Debug($"Sent {string.Join(" ", data.Select(b => Convert.ToString(b, 16)))}");
            
            _packetsSent.Observe(data.Length);
            _writer.Write(data);
            _writer.Flush();
        }

        public void StartHandshake()
        {
            if (Handshaking) return;

            // Generate random handshake and start the handshaking
            Handshake = CoreRandom.GenerateUInt32();
            Handshaking = true;
            SetPhase(EPhases.Handshake);
            SendHandshake();
        }

        public bool HandleHandshake(GCHandshake handshake)
        {
            if (!Handshaking)
            {
                // We wasn't handshaking!
                Log.Information("Received handshake while not handshaking!");
                _client.Close();
                return false;
            }

            if (handshake.Handshake != Handshake)
            {
                // We received a wrong handshake
                Log.Information($"Received wrong handshake ({Handshake} != {handshake.Handshake})");
                _client.Close();
                return false;
            }

            var time = GetServerTime();
            var difference = time - (handshake.Time + handshake.Delta);
            if (difference >= 0 && difference <= 50)
            {
                // if we difference is less than or equal to 50ms the handshake is done and client time is synced enough
                Log.Information("Handshake done");
                Handshaking = false;

                OnHandshakeFinished();
            }
            else
            {
                // calculate new delta 
                var delta = (time - handshake.Time) / 2;
                if (delta < 0)
                {
                    delta = (time - _lastHandshakeTime) / 2;
                    Log.Debug($"Delta is too low, retry with last send time");
                }

                SendHandshake((uint) time, (uint) delta);
            }

            return true;
        }

        public void SetPhase(EPhases phase)
        {
            Phase = phase;
            Send(new GCPhase
            {
                Phase = (byte) phase
            });
        }

        private void SendHandshake()
        {
            var time = GetServerTime();
            _lastHandshakeTime = time;
            Send(new GCHandshake
            {
                Handshake = Handshake,
                Time = (uint) time
            });
        }

        private void SendHandshake(uint time, uint delta)
        {
            _lastHandshakeTime = time;
            Send(new GCHandshake
            {
                Handshake = Handshake,
                Time = time,
                Delta = delta
            });
        }
    }
}