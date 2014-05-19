﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CitizenMP.Server.Game
{
    class GameServer
    {
        private Socket m_gameSocket;

        private SocketAsyncEventArgs m_asyncEventArgs;

        private byte[] m_receiveBuffer;

        private Client m_host;

        public bool UseAsync { get; set; }

        private Resources.ResourceManager m_resourceManager;

        private Configuration m_configuration;

        public GameServer(Configuration config, Resources.ResourceManager resManager)
        {
            m_configuration = config;

            m_resourceManager = resManager;
            m_resourceManager.SetGameServer(this);

            UseAsync = true;
        }

        public void Start()
        {
            this.Log().Info("Starting game server on port {0}", m_configuration.ListenPort);

            m_gameSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);
            m_gameSocket.Bind(new IPEndPoint(IPAddress.Any, m_configuration.ListenPort));

            m_gameSocket.Blocking = false;

            m_receiveBuffer = new byte[2048];

            if (UseAsync)
            {
                m_asyncEventArgs = new SocketAsyncEventArgs();
                m_asyncEventArgs.SetBuffer(m_receiveBuffer, 0, m_receiveBuffer.Length);

                m_asyncEventArgs.RemoteEndPoint = new IPEndPoint(IPAddress.None, 0);

                m_asyncEventArgs.Completed += m_asyncEventArgs_Completed;

                if (!m_gameSocket.ReceiveFromAsync(m_asyncEventArgs))
                {
                    m_asyncEventArgs_Completed(this, m_asyncEventArgs);
                }
            }
        }

        void ProcessOOB(IPEndPoint remoteEP, byte[] buffer, int length)
        {
            var commandText = Encoding.UTF8.GetString(buffer, 4, length - 4);
            var command = commandText.Split(' ')[0];

            if (command == "connect")
            {
                ProcessConnectCommand(remoteEP, commandText);
            }
            else if (command == "rcon")
            {
                ProcessRconCommand(remoteEP, commandText);
            }
        }

        private Dictionary<IPEndPoint, int> m_lastRconTimes = new Dictionary<IPEndPoint, int>();

        void ProcessRconCommand(IPEndPoint remoteEP, string commandText)
        {
            // tokenize command text
            var command = Utils.Tokenize(commandText);

            if (command.Length < 3)
            {
                return;
            }

            // get last rcon time (flood protection)
            if (m_lastRconTimes.ContainsKey(remoteEP))
            {
                var time = m_lastRconTimes[remoteEP];

                if (m_serverTime < (time + 100))
                {
                    return;
                }
            }

            RconPrint.StartRedirect(this, remoteEP);

            // check for the password being valid
            if (m_configuration.RconPassword != null)
            {
                if (m_configuration.RconPassword != command[1])
                {
                    RconPrint.Print("Invalid rcon password.\n");

                    this.Log().Warn("Bad rcon from {0}", remoteEP);
                }
                else
                {
                    var arguments = command.Skip(3).ToList();

                    try
                    {
                        m_resourceManager.TriggerEvent("rconCommand", -1, command[2], arguments);
                    }
                    catch (Exception e)
                    {
                        this.Log().Error(() => "error handling rcon: " + e.Message, e);
                        RconPrint.Print(e.Message);
                    }
                }
            }
            else
            {
                RconPrint.Print("No rcon password was set on this server.\n");
            }

            RconPrint.EndRedirect();
        }

        void ProcessConnectCommand(IPEndPoint remoteEP, string commandText)
        {
            var argumentString = commandText.Substring("connect ".Length);
            var arguments = Utils.ParseQueryString(argumentString);

            if (arguments.ContainsKey("token") && arguments.ContainsKey("guid"))
            {
                var clientKV = ClientInstances.Clients.FirstOrDefault(cl => cl.Value.Guid == arguments["guid"] && cl.Value.Token == arguments["token"]);

                if (clientKV.Equals(default(KeyValuePair<string,Client>)))
                {
                    return;
                }

                var client = clientKV.Value;
                client.Touch();

                client.NetChannel = new NetChannel(client);
                client.NetID = ClientInstances.AssignNetID();

                client.RemoteEP = remoteEP;
                client.Socket = m_gameSocket;

                SendOutOfBand(remoteEP, "connectOK {0} {1} {2}", client.NetID, (m_host != null) ? m_host.NetID : -1, (m_host != null) ? m_host.Base : -1);
            }
        }

        public void SendOutOfBand(IPEndPoint remoteEP, string text, params object[] data)
        {
            var outString = "    " + string.Format(text, data);
            var outMessage = Encoding.UTF8.GetBytes(outString);

            outMessage[0] = 0xFF; outMessage[1] = 0xFF; outMessage[2] = 0xFF; outMessage[3] = 0xFF;

            m_gameSocket.SendTo(outMessage, remoteEP);
        }

        void ProcessClientMessage(Client client, BinaryReader reader)
        {
            // touch the client
            client.Touch();

            // acknowledge any reliable commands
            var curReliableAck = reader.ReadUInt32();

            if (curReliableAck != client.OutReliableAcknowledged)
            {
                for (int i = client.OutReliableCommands.Count - 1; i >= 0; i--)
                {
                    if (client.OutReliableCommands[i].ID <= curReliableAck)
                    {
                        client.OutReliableCommands.RemoveAt(i);
                    }
                }

                client.OutReliableAcknowledged = curReliableAck;
            }

            // now read the actual message
            try
            {
                while (true)
                {
                    var messageType = reader.ReadUInt32();

                    if (messageType == 0xCA569E63) // 'msgEnd'
                    {
                        return;
                    }

                    if (messageType == 0xE938445B) // 'msgRoute'
                    {
                        ProcessRoutingMessage(client, reader);
                    } 
                    else if (messageType == 0xB3EA30DE) // 'msgIHost'
                    {
                        ProcessIHostMessage(client, reader);
                    }
                    else // reliable command
                    {
                        ProcessReliableMessage(client, messageType, reader);
                    }
                }
            }
            catch (EndOfStreamException)
            {
                this.Log().Debug("end of stream for client {0}", client.NetID);
            }
        }

        void ProcessRoutingMessage(Client client, BinaryReader reader)
        {
            client.SentData = true;

            // find the target client
            var targetNetID = reader.ReadUInt16();

            // read the actual packet first so our stream doesn't end up broken
            var dataLength = reader.ReadUInt16();
            var data = reader.ReadBytes(dataLength);

            var targetClientKV = ClientInstances.Clients.FirstOrDefault(c => c.Value.NetID == targetNetID);

            if (targetClientKV.Equals(default(KeyValuePair<string,Client>)))
            {
                this.Log().Info("no target netID {0}", targetNetID);

                return;
            }

            var targetClient = targetClientKV.Value;

            lock (targetClient)
            {
                // create a new message
                var stream = new MemoryStream();
                var writer = new BinaryWriter(stream);

                targetClient.WriteReliableBuffer(writer);

                writer.Write(0xE938445B);
                writer.Write(client.NetID);
                writer.Write(dataLength);
                writer.Write(data);
                writer.Write(0xCA569E63);

                //this.Log().Debug("routing {0}-byte packet to {1}", dataLength, targetNetID);

                targetClient.NetChannel.Send(stream.ToArray());
            }
        }

        void ProcessIHostMessage(Client client, BinaryReader reader)
        {
            var baseNum = reader.ReadInt32();
            var allowHost = false;

            if (m_host == null)
            {
                allowHost = true;
            }
            else
            {
                if ((DateTime.UtcNow - m_host.LastSeen).TotalSeconds > 15)
                {
                    allowHost = true;
                }
            }

            if (allowHost)
            {
                client.Base = baseNum;
                SetNewHost(client);
            }
        }

        private void SetNewHost(Client client)
        {
            m_host = client;

            var outMsg = new MemoryStream();
            var outWriter = new BinaryWriter(outMsg);

            outWriter.Write(client.NetID);
            outWriter.Write(client.Base);

            // send a is-host message to everyone
            foreach (var targetClient in ClientInstances.Clients)
            {
                var tCl = targetClient.Value;

                tCl.SendReliableCommand(0xB3EA30DE, outMsg.ToArray()); // 'msgIHost'
            }

            // all votes are irrelevant now
            m_hostVotes.Clear();
        }

        private void BeforeDropClient(Client client)
        {
            if (m_host != null && client.NetID == m_host.NetID)
            {
                m_host = null;

                // tell current clients we've got no host
                var outMsg = new MemoryStream();
                var outWriter = new BinaryWriter(outMsg);

                outWriter.Write((ushort)65535);
                outWriter.Write(65535);

                foreach (var targetClient in ClientInstances.Clients)
                {
                    var tCl = targetClient.Value;

                    tCl.SendReliableCommand(0xB3EA30DE, outMsg.ToArray()); // 'msgIHost'
                }
            }
        }

        private Dictionary<uint, int> m_hostVotes = new Dictionary<uint, int>();

        void HandleReliableCommand(Client client, uint messageType, BinaryReader reader, int size)
        {
            //this.Log().Debug("that's a fairly nice {0}", messageType);

            if (messageType == 0x522CADD1) // msgIQuit
            {
                this.Log().Info("Client {0} quit.", client.Name);

                BeforeDropClient(client);

                ClientInstances.RemoveClient(client);
                return;
            }

            if (messageType == 0x86E9F87B) // msgHeHost
            {
                // we need to maintain some consensus on who's becoming host; if >=33% (rounded up) of connected clients say one's host, then so be it
                var allegedNetID = reader.ReadUInt32();

                if (m_host != null && allegedNetID == m_host.NetID)
                {
                    this.Log().Info("Got a late vote for {0}; they are our current host", allegedNetID);

                    return;
                }

                var newBase = reader.ReadUInt16();

                var clientCount = ClientInstances.Clients.Count(c => c.Value.SentData == true);
                var votesNeeded = (clientCount > 0) ? ((clientCount / 3) + (((clientCount % 3) > 0) ? 1 : 0)) : 0;

                int curVotes;
                
                if (!m_hostVotes.TryGetValue(allegedNetID, out curVotes))
                {
                    curVotes = 1;
                }
                else
                {
                    curVotes++;
                }

                this.Log().Info("Received a vote for {0}; current votes {1}, needed {2}", allegedNetID, curVotes, votesNeeded);

                // do the big check
                if (curVotes >= votesNeeded)
                {
                    var newHost = ClientInstances.Clients.FirstOrDefault(a => a.Value.NetID == allegedNetID);

                    if (newHost.Equals(default(KeyValuePair<string, Client>)))
                    {
                        this.Log().Warn("The vote was rigged! Nobody is host! Bad politics!");
                        return;
                    }

                    this.Log().Info("Net ID {0} won the election; they are the new host-elect.", allegedNetID);

                    newHost.Value.Base = newBase;

                    SetNewHost(newHost.Value);
                }
                else
                {
                    m_hostVotes[allegedNetID] = curVotes;
                }

                return;
            }

            if (messageType == 0x7337FD7A || messageType == 0xFA776E18) // msgNetEvent; msgServerEvent
            {
                var targetNetID = (messageType != 0xFA776E18) ? reader.ReadUInt16() : 0;
                var nameLength = reader.ReadUInt16();

                var eventName = "";

                for (int i = 0; i < (nameLength - 1); i++)
                {
                    eventName += (char)reader.ReadByte();
                }

                // null terminator
                reader.ReadByte();

                //this.Log().Debug("client sent a {0}", eventName);

                // data length
                var dataLength = size - nameLength - 4;

                // if this is a client event
                if (messageType == 0x7337FD7A)
                {
                    var data = reader.ReadBytes(dataLength);

                    TriggerClientEvent(eventName, data, targetNetID, client.NetID);
                }
                else
                {
                    var data = reader.ReadBytes(dataLength + 2); // as there's no network ID in it

                    if (!m_whitelistedEvents.Contains(eventName))
                    {
                        this.Log().Warn("A client tried to send an event of type {0}, but it was not greenlit for client invocation. You may need to call RegisterServerEvent from your script.", eventName);
                        return;
                    }

                    var dataSB = new StringBuilder(data.Length);

                    foreach (var b in data)
                    {
                        dataSB.Append((char)b);
                    }

                    // TODO: make source equal the game-side client ID, and not the net ID
                    QueueCallback(() => m_resourceManager.TriggerEvent(eventName, dataSB.ToString(), client.NetID));
                }
            }
        }

        public void TriggerClientEvent(string eventName, string data, int targetNetID, int sourceNetID)
        {
            var dataArray = new byte[data.Length];
            var i = 0;

            foreach (var c in data)
            {
                dataArray[i] = (byte)c;
                i++;
            }

            TriggerClientEvent(eventName, dataArray, targetNetID, sourceNetID);
        }

        public void TriggerClientEvent(string eventName, byte[] data, int targetNetID, int sourceNetID)
        {
            // create an output packet
            var outMsg = new MemoryStream();
            var outWriter = new BinaryWriter(outMsg);

            outWriter.Write((ushort)sourceNetID);
            outWriter.Write((ushort)(eventName.Length + 1));

            for (int i = 0; i < eventName.Length; i++)
            {
                outWriter.Write((byte)eventName[i]);
            }

            outWriter.Write((byte)0);

            outWriter.Write(data);

            var buffer = outMsg.ToArray();

            // and send it to all clients
            if (targetNetID == 65535 || targetNetID == -1)
            {
                foreach (var targetClient in ClientInstances.Clients)
                {
                    var tCl = targetClient.Value;

                    tCl.SendReliableCommand(0x7337FD7A, buffer);
                }
            }
            else
            {
                var targetClient = ClientInstances.Clients.Where(a => a.Value.NetID == targetNetID).Select(a => a.Value).FirstOrDefault();

                if (targetClient != null)
                {
                    targetClient.SendReliableCommand(0x7337FD7A, buffer);
                }
            }
        }

        private HashSet<string> m_whitelistedEvents = new HashSet<string>();

        public void WhitelistEvent(string eventName)
        {
            if (!m_whitelistedEvents.Contains(eventName))
            {
                m_whitelistedEvents.Add(eventName);
            }
        }

        void ProcessReliableMessage(Client client, uint messageType, BinaryReader reader)
        {
            var id = reader.ReadUInt32();
            int size;

            if ((id & 0x80000000) != 0)
            {
                size = reader.ReadInt32();

                id &= ~0x80000000;
            }
            else
            {
                size = reader.ReadInt16();
            }

            var basePos = reader.BaseStream.Position;

            if (id > client.LastReceivedReliable)
            {
                HandleReliableCommand(client, messageType, reader, size);

                client.LastReceivedReliable = id;
            }

            // if the handler doesn't fully read, still recover position
            reader.BaseStream.Position = basePos + size;
        }

        void ProcessIncomingPacket(byte[] buffer, int length, IPEndPoint remoteEP)
        {
            using (var stream = new MemoryStream(buffer))
            {
                var reader = new BinaryReader(stream);

                var sequence = reader.ReadUInt32();

                if (sequence == 0xFFFFFFFF)
                {
                    ProcessOOB(remoteEP, buffer, length);
                }
                else
                {
                    var clientKV = ClientInstances.Clients.FirstOrDefault(c => c.Value.RemoteEP != null && c.Value.RemoteEP.Equals(remoteEP));

                    if (clientKV.Equals(default(KeyValuePair<string,Client>)))
                    {
                        this.Log().Info("Received a packet from an unknown source ({0})", remoteEP);
                        return;
                    }

                    var client = clientKV.Value;

                    if (client.NetChannel.Process(buffer, ref reader))
                    {
                        ProcessClientMessage(client, reader);
                    }
                }
            }
        }

        void m_asyncEventArgs_Completed(object sender, SocketAsyncEventArgs e)
        {
            try
            {
                if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
                {
                    ProcessIncomingPacket(e.Buffer, e.BytesTransferred, (IPEndPoint)e.RemoteEndPoint);
                }
            }
            catch (Exception ex)
            {
                this.Log().Error(() => "incoming packet failed", ex);
            }

            // this may very well result in a stack overflow ;/
            if (!m_gameSocket.ReceiveFromAsync(e))
            {
                m_asyncEventArgs_Completed(this, e);
            }
        }

        private Queue<Action> m_mainCallbacks = new Queue<Action>();

        void QueueCallback(Action cb)
        {
            lock (m_mainCallbacks)
            {
                m_mainCallbacks.Enqueue(cb);
            }
        }

        private int m_residualTime;
        private int m_serverTime;

        public void Tick(int msec)
        {
            if (!UseAsync)
            {
                // network reception
                EndPoint receiveEP = new IPEndPoint(IPAddress.None, 0);

                while (true)
                {
                    try
                    {
                        int length = m_gameSocket.ReceiveFrom(m_receiveBuffer, ref receiveEP);

                        if (length > 0)
                        {
                            ProcessIncomingPacket(m_receiveBuffer, length, (IPEndPoint)receiveEP);
                        }
                        else
                        {
                            break;
                        }
                    }
                    catch (SocketException e)
                    {
                        if (e.SocketErrorCode != SocketError.WouldBlock)
                        {
                            this.Log().Warn("socket error {0}", e.Message);
                        }

                        break;
                    }
                }
            }

            while (m_mainCallbacks.Count > 0)
            {
                Action cb;

                lock (m_mainCallbacks)
                {
                    cb = m_mainCallbacks.Dequeue();
                }

                cb();
            }

            // is it time for a server frame yet?
            m_residualTime += msec;

            // 20 FPS = 50msec intervals
            while (m_residualTime > 50)
            {
                m_residualTime -= 50;
                m_serverTime += 50;

                ProcessServerFrame();
            }
        }

        private int m_lastSenselessReliableSent;

        private void ProcessServerFrame()
        {
            // process client timeouts
            var curTime = DateTime.UtcNow;

            var toRemove = new List<Client>();

            foreach (var client in ClientInstances.Clients)
            {
                var timeout = (client.Value.SentData) ? 15 : 60;

                if ((curTime - client.Value.LastSeen).TotalSeconds > timeout)
                {
                    this.Log().Info("disconnected a client for timing out");

                    toRemove.Add(client.Value);
                }
            }

            foreach (var client in toRemove)
            {
                BeforeDropClient(client);

                ClientInstances.RemoveClient(client);
            }

            // time for another senseless reliable?
            var sendSenselessReliable = false;

            if ((m_serverTime - m_lastSenselessReliableSent) > 5000)
            {
                sendSenselessReliable = true;

                m_lastSenselessReliableSent = m_serverTime;
            }

            // and then just send reliable buffers
            foreach (var client in ClientInstances.Clients)
            {
                var cl = client.Value;

                if (sendSenselessReliable)
                {
                    if (cl.NetChannel != null)
                    {
                        var bytes = new byte[] { 0x12, 0x34, 0x56, 0x78 };

                        cl.SendReliableCommand(0x1234, bytes);
                    }
                }

                if (cl.NetChannel != null && cl.OutReliableCommands.Count > 0)
                {
                    lock (cl)
                    {
                        // create a new message
                        var stream = new MemoryStream();
                        var writer = new BinaryWriter(stream);

                        cl.WriteReliableBuffer(writer);
                        writer.Write(0xCA569E63);

                        cl.NetChannel.Send(stream.ToArray());
                    }
                }
            }
        }
    }
}
