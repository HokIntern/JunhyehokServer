using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace JunhyehokServer
{
    class Program
    {
        static void Main(string[] args)
        {
            string host = null;     //Default
            string clientPort = "30000";  //Default
            TcpServer echoc;

            //=========================GET ARGS=================================
            if (args.Length == 0)
            {
                Console.WriteLine("Format: JunhyehokServer -cp [client port] -sp [server port]");
                Environment.Exit(0);
            }

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help":
                        Console.WriteLine("Format: JunhyehokServer -cp [client port] -sp [server port]");
                        Environment.Exit(0);
                        break;
                    case "-cp":
                        clientPort = args[++i];
                        break;
                    default:
                        Console.Error.WriteLine("ERROR: incorrect inputs \nFormat: IrccServer -cp [client port] -sp [server port]");
                        Environment.Exit(0);
                        break;
                }
            }

            //======================SOCKET BIND/LISTEN==========================
            /* if only given port, host is ANY */
            echoc = new TcpServer(host, clientPort);

            //======================BACKEND CONNECT===============================
            Console.WriteLine("Connecting to Backend...");
            string backendInfo = "";
            try { backendInfo = System.IO.File.ReadAllText("backend.conf"); }
            catch (Exception e) { Console.WriteLine("\n" + e.Message); Environment.Exit(0); }
            Socket so = Connect(backendInfo);

            ClientHandle backend = new ClientHandle(backendInfo, "backend");
            backend.Connect();

            //======================INITIALIZE==================================
            Console.WriteLine("Initializing lobby and rooms...");
            ReceiveHandle recvHandle = new ReceiveHandle();

            //===================CLIENT SOCKET ACCEPT===========================
            while (true)
            {
                Socket s = echoc.so.Accept();
                ClientHandle client = new ClientHandle(s);
                client.StartSequence();
            }
        }
        public Socket Connect(string hostport)
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
    }
}
