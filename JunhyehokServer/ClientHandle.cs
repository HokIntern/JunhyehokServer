using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Junhaehok;
using static Junhaehok.HhhHelper;
using static Junhaehok.Packet;

namespace JunhyehokServer
{
    class ClientHandle
    {
        bool debug = true;

        Socket so;
        int bytecount;
        int heartbeatMiss = 0;

        private string cookie;
        private long userId;
        private State status;
        private bool isDummy;
        private long roomId;
        private int chatCount;
        ReceiveHandle recvHandler;

        string remoteHost;
        string remotePort;

        public Socket So { get; }
        public string Cookie { get; set; }
        public long UserId { get; set; }
        public State Status { get; set; }
        public bool IsDummy { get; set; }
        public long RoomId { get; set; }
        public int ChatCount { get; set; }

        public enum State
        {
            Offline, Online, Lobby, Room, Monitoring, Error
        }

        public ClientHandle(Socket s)
        {
            so = s;
            status = State.Online;
            remoteHost = ((IPEndPoint)so.RemoteEndPoint).Address.ToString();
            remotePort = ((IPEndPoint)so.RemoteEndPoint).Port.ToString();
        }

        public async void StartSequence()
        {
            while (true)
            {
                Packet recvRequest = await SocketRecvAsync();

                if (ushort.MaxValue == recvRequest.header.code)
                    break;

                //=================Process Request/Get Response=================
                ReceiveHandle recvHandle = new ReceiveHandle(this, recvRequest);
                Packet respPacket = recvHandle.GetResponse();

                //=======================Send Response==========================
                if (ushort.MaxValue != respPacket.header.code)
                {
                    byte[] respBytes = PacketToBytes(respPacket);
                    bool sendSuccess = sendBytes(respBytes);
                    if (!sendSuccess)
                    {
                        Console.WriteLine("Send failed.");
                        break;
                    }
                }

                //=======================Check Connection=======================
                if (!isConnected())
                {
                    Console.WriteLine("Connection lost with {0}:{1}", remoteHost, remotePort);
                    break;
                }
            }
            CloseConnection();
        }
        
        private async Task<Packet> SocketRecvAsync()
        {
            Packet disconnectedFlagPacket = new Packet(new Header(ushort.MaxValue, 0), null);
            //=========================Receive==============================
            Header recvHeader;
            Packet recvRequest;

            //========================get HEADER============================
            byte[] headerBytes = await GetBytesAsync(HEADER_SIZE);
            if (null == headerBytes)
                return disconnectedFlagPacket;
            recvHeader = BytesToHeader(headerBytes);
            recvRequest.header = recvHeader;

            //========================get DATA==============================
            byte[] dataBytes = await GetBytesAsync(recvHeader.size);
            if (null == dataBytes)
                return disconnectedFlagPacket;
            recvRequest.data = dataBytes;

            return recvRequest;
        }
        
        private void CloseConnection()
        {
            //=================Signout/Close Connection/Exit Thread==================
            Signout();
            Console.WriteLine("Closing connection with {0}:{1}", remoteHost, remotePort);
            so.Shutdown(SocketShutdown.Both);
            so.Close();
            Console.WriteLine("Connection closed\n");
        }

        /*
        public void EchoSend(Packet echoPacket)
        {
            if (debug)
                Console.WriteLine("==SEND: \n" + PacketDebug(echoPacket));
            byte[] echoBytes = PacketToBytes(echoPacket);
            bool echoSuccess = sendBytes(echoBytes);
            if (!echoSuccess)
            {
                Console.WriteLine("FAIL: Relay message to client {0} failed", userId);
            }
        }
        */

        private void Signout()
        {
            if (status == State.Lobby || status == State.Room)
            {
                ReceiveHandle.RemoveClient(this);
                this.roomId = 0;
            }
            this.status = State.Offline;
        }

        private async Task<byte[]> GetBytesAsync(int length)
        {
            byte[] bytes = new byte[length];
            if (length != 0) //this check has to exist. otherwise Receive timeouts for 60seconds while waiting for nothing
            {
                try
                {
                    so.ReceiveTimeout = 120000;
                    bytecount = await Task.Run(() => so.Receive(bytes));

                    //assumes that the line above(so.Receive) will throw exception 
                    //if times out, so the line below(reset hearbeatMiss) will not be reached
                    //if an exception is thrown.
                    heartbeatMiss = 0;
                }
                catch (Exception e)
                {
                    if (!isConnected())
                    {
                        Console.WriteLine("\n" + e.Message);
                        return null;
                    }
                    else
                    {
                        if (bytes.Length != 0)
                        {
                            heartbeatMiss++;
                            if (heartbeatMiss == 2)
                                return null;

                            //puts -1 bytes into 1st and  bytes (CODE)
                            byte[] noRespBytes = BitConverter.GetBytes((ushort)ushort.MaxValue-1);
                            bytes[0] = noRespBytes[0];
                            bytes[1] = noRespBytes[1];
                        }
                    }
                }
            }
            return bytes;
        }

        private bool sendBytes(byte[] bytes)
        {
            try
            {
                bytecount = so.Send(bytes);
            }
            catch (Exception e)
            {
                Console.WriteLine("\n" + e.Message);
                return false;
            }
            return true;
        }

        private bool isConnected()
        {
            try
            {
                return !(so.Poll(1, SelectMode.SelectRead) && so.Available == 0);
            }
            catch (SocketException) { return false; }
        }
    }
}
