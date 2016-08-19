using Junhaehok;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using static Junhaehok.HhhHelper;

namespace JunhyehokServer
{
    public static class SocketExtensions
    {
        public static bool SendBytes(this Socket so, Packet packet)
        {
            byte[] bytes = HhhHelper.PacketToBytes(packet);
            int bytecount;
            try
            {
                bytecount = so.Send(bytes);
                Console.WriteLine("==SEND: \n" + PacketDebug(packet));
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
