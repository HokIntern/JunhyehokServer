using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;

namespace JunhyehokServer
{
    class TcpClient
    {
        string host;
        int port;
        public Socket so;

        public TcpClient(string hname, string pname)
        {
            host = hname;
            if (!int.TryParse(pname, out port))
            {
                Console.Error.WriteLine("port must be int. given: {0}", pname);
                Environment.Exit(0);
            }

            so = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPAddress ipAddress = IPAddress.Parse(host);
            Console.WriteLine("Establishing connection to {0}:{1} ...", host, port);

            so.Connect(ipAddress, port);
            Console.WriteLine("Connection established.\n");
        }
    }
}
