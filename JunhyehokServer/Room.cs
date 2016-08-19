using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JunhyehokServer
{
    class Room
    {
        private long roomId;
        private string roomname;
        private List<ClientHandle> clients;

        public long RoomId { get { return roomId; } }
        public string Roomname { get { return roomname; } }
        public List<ClientHandle> Clients { get { return clients; } }

        public Room(long roomId, string roomname)
        {
            this.roomId = roomId;
            this.roomname = roomname;
            clients = new List<ClientHandle>();
        }

        public void AddClient(ClientHandle client)
        {
            clients.Add(client);
        }
        public void RemoveClient(ClientHandle client)
        {
            clients.Remove(client);
        }
    }
}
