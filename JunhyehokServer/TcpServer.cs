using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;

namespace JunhyehokServer
{
    class TcpServer
    {
        string host;
        int port;
        public Socket so;

        public TcpServer(string hname, string pname)
        {
            host = hname;
            if (!Int32.TryParse(pname, out port))
            {
                Console.Error.WriteLine("Error: Port arg must be int. given: \"{0}\"", pname);
                Environment.Exit(0);
            }

            so = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            IPAddress ipAddress;
            if (host == null)
                ipAddress = IPAddress.Any;
            else
                ipAddress = IPAddress.Parse(host);

            IPEndPoint ipLocalEP = new IPEndPoint(ipAddress, port);
            Console.WriteLine("Binding to {0}:{1} ...", (host == null ? "ANY" : host), port);
            so.Bind(ipLocalEP);
            so.Listen(5);
            Console.WriteLine("Listening...");
        }
    }
}
