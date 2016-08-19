using System;
using System.Runtime.InteropServices;
using System.Text;

namespace JunhyehokServer
{
    static class Serializer
    {
        public static byte[] StructureToByte(object obj)
        {
            int dataSize = Marshal.SizeOf(obj);
            IntPtr buff = Marshal.AllocHGlobal(dataSize);
            Marshal.StructureToPtr(obj, buff, false);
            byte[] data = new byte[dataSize];
            Marshal.Copy(buff, data, 0, dataSize);
            Marshal.FreeHGlobal(buff);
            return data;
        }


        public static object ByteToStructure(byte[] data, Type type)
        {
            IntPtr buff = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, buff, data.Length);
            object obj = Marshal.PtrToStructure(buff, type);
            Marshal.FreeHGlobal(buff);

            if (Marshal.SizeOf(obj) != data.Length)
            {
                return null;
            }

            return obj;
        }
        public static byte[] StringToBytes(string data)
        {
            return Encoding.UTF8.GetBytes(data.ToCharArray());
        }

        public static string BytesToString(byte[] data)
        {
            return Encoding.UTF8.GetString(data);
        }
    }
}