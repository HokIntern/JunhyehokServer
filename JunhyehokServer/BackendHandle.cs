using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;
using System.Threading;

namespace JunhyehokServer
{
    class BackendHandle
    {
        bool debug = true;
        int heartbeatMiss = 0;

        string host;
        int port;
        Socket so;
        ReceiveHandle recvHandler;

        public Socket So { get { return so; } }

        public BackendHandle(string info)
        {
            string[] hostport = info.Split(':');
            host = hostport[0];
            if (!int.TryParse(hostport[1], out port))
            {
                Console.Error.WriteLine("port must be int. given: {0}", hostport[1]);
                Environment.Exit(0);
            }
        }

        public void Connect()
        {
            Socket so = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPAddress ipAddress = IPAddress.Parse(host);

            Console.WriteLine("[Backend] Establishing connection to {0}:{1} ...", host, port);

            try
            {
                so.Connect(ipAddress, port);
                Console.WriteLine("[Server] Connection established.\n");
            }
            catch (Exception)
            {
                Console.WriteLine("Peer is not alive.");
            }
        }

        private void start()
        {
            string remoteHost = ((IPEndPoint)so.RemoteEndPoint).Address.ToString();
            string remotePort = ((IPEndPoint)so.RemoteEndPoint).Port.ToString();
            Console.WriteLine("[Server] Connection established with {0}:{1}\n", remoteHost, remotePort);

            for (;;)
            {
                //=========================Receive==============================
                Header recvHeader;
                Packet recvRequest;

                //========================get HEADER============================
                byte[] headerBytes = getBytes(HEADER_SIZE);
                if (null == headerBytes)
                    break;
                recvHeader = BytesToHeader(headerBytes);
                recvRequest.header = recvHeader;

                //========================get DATA==============================
                byte[] dataBytes = getBytes(recvHeader.size);
                if (null == dataBytes)
                    break;
                recvRequest.data = dataBytes;

                //=================Process Request/Get Response=================
                ClientHandle surrogateClient;
                recvHandler = new ReceiveHandler(this, recvRequest);
                Packet respPacket = recvHandler.GetResponse(out surrogateClient);

                //=======================Send Response==========================
                if (-1 != respPacket.header.comm)
                {
                    byte[] respBytes = PacketToBytes(respPacket);
                    bool sendSuccess = false;
                    if (surrogateClient == null)
                        sendSuccess = sendBytes(respBytes);
                    else
                        sendSuccess = sendBytes(surrogateClient.So, respBytes);

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
            //=================RemoveServer/Close Connection/Exit Thread=================
            ReceiveHandler.RemoveServer(this);
            Console.WriteLine("Closing connection with {0}:{1}", remoteHost, remotePort);
            so.Shutdown(SocketShutdown.Both);
            so.Close();
            Console.WriteLine("Connection closed\n");
        }

        public bool Send(Packet p)
        {
            byte[] respBytes = PacketToBytes(p);
            return sendBytes(respBytes);
        }

        public void EchoSend(Packet echoPacket)
        {
            if (debug)
                Console.WriteLine("==SEND: \n" + PacketDebug(echoPacket));
            byte[] echoBytes = PacketToBytes(echoPacket);
            bool echoSuccess = sendBytes(echoBytes);
            if (!echoSuccess)
            {
                string remoteHost = ((IPEndPoint)so.RemoteEndPoint).Address.ToString();
                string remotePort = ((IPEndPoint)so.RemoteEndPoint).Port.ToString();
                Console.WriteLine("FAIL: Relay message to server {0}:{1} failed", remoteHost, remotePort);
            }
        }

        private byte[] getBytes(int length)
        {
            byte[] bytes = new byte[length];
            if (length != 0) //this check has to exist. otherwise Receive timeouts for 60seconds while waiting for nothing
            {
                try
                {
                    so.ReceiveTimeout = 10000;
                    int bytecount = so.Receive(bytes);

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

                            //puts Comm.SS into 1st and 2nd bytes (COMM)
                            byte[] noRespBytes = BitConverter.GetBytes(Comm.SS);
                            bytes[0] = noRespBytes[0];
                            bytes[1] = noRespBytes[1];
                            //puts -1 bytes into 3rd and 4th bytes (CODE)
                            noRespBytes = BitConverter.GetBytes((short)-1);
                            bytes[2] = noRespBytes[0];
                            bytes[3] = noRespBytes[1];
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
                int bytecount = so.Send(bytes);
            }
            catch (Exception e)
            {
                Console.WriteLine("\n" + e.Message);
                return false;
            }
            return true;
        }

        private bool sendBytes(Socket so, byte[] bytes)
        {
            try
            {
                int bytecount = so.Send(bytes);
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
