using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace JunhyehokServer
{
    public static class SocketExtensions
    {
        public static bool SendBytes(this Socket so, byte[] bytes)
        {
            int bytecount;
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
    }
}
