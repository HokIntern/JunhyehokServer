using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace JunhyehokServer
{
    class ReceiveHandle
    {
        string accessor;
        ClientHandle client;
        ServerHandle server;
        Packet recvPacket;

        static BackendHandle backend;
        static Dictionary<long, ClientHandle> lobbyClients;
        static List<ServerHandle> peerServers;
        static Dictionary<long, Room> rooms;
        static Dictionary<long, int[]> peerRespWait; //key: userId, value [0]: seqc, [1]: current seq count, [2]: success
        readonly Header NoResponseHeader = new Header(-1, 0, 0);
        readonly Packet NoResponsePacket = new Packet(new Header(-1, 0, 0), null);

        /*
        public static ReceiveHandler()
        {
            lobbyClients = new Dictionary<long, ClientHandle>();
            peerServers = new List<ServerHandle>();
            rooms = new Dictionary<long, Room>();
            peerRespWait = new Dictionary<long, int[]>();
        }
        */

        public ReceiveHandle()
        {
            lobbyClients = new Dictionary<long, ClientHandle>();
            peerServers = new List<ServerHandle>();
            rooms = new Dictionary<long, Room>();
            peerRespWait = new Dictionary<long, int[]>();
        }

        public ReceiveHandle(ClientHandle client, Packet recvPacket, RedisHelper redis)
        {
            this.accessor = "client";
            this.client = client;
            this.recvPacket = recvPacket;
            this.redis = redis;
        }

        public ReceiveHandle(ServerHandle server, Packet recvPacket)
        {
            this.accessor = "server";
            this.server = server;
            this.recvPacket = recvPacket;
        }

        public bool SetPeerServers(string[] peerInfo, string thisServerPort)
        {
            bool havePeers = false;
            foreach (string peerAddress in peerInfo)
            {
                Socket so = ConnectToPeer(peerAddress, thisServerPort);
                if (null != so)
                {
                    ServerHandle peer = new ServerHandle(so);
                    AddPeerServer(peer);
                    havePeers = true;
                }
            }

            return havePeers;
        }

        public void AddPeerServer(ServerHandle peer)
        {
            if (!peerServers.Contains(peer))
                peerServers.Add(peer);
        }

        public static void RemoveClient(ClientHandle client)
        {
            //TODO: implement sending signout packet to backend
            if (client.Status == ClientHandle.State.Room)
            {
                Room requestedRoom;
                lock (rooms)
                {
                    if (!rooms.TryGetValue(client.RoomId, out requestedRoom))
                        Console.WriteLine("ERROR: REMOVECLIENT - room doesn't exist {0}", client.RoomId);
                    else
                        requestedRoom.RemoveClient(client);
                }
            }
            else if (client.Status == ClientHandle.State.Lobby)
            {
                lock (lobbyClients)
                    lobbyClients.Remove(client.UserId);
            }
            else
                Console.WriteLine("ERROR: REMOVECLIENT - you messed up");
        }

        public static void RemoveServer(ServerHandle server)
        {
            lock (peerServers)
            {
                if (!peerServers.Remove(server))
                    Console.WriteLine("ERROR: REMOVESERVER - server was already removed");
            }

            lock (rooms)
            {
                foreach (KeyValuePair<long, Room> room in rooms)
                    room.Value.RemoveServer(server);
            }
        }
        //==============================================CREATE 700===============================================
        //==============================================CREATE 700===============================================
        //==============================================CREATE 700===============================================
        public Packet ResponseCreate(Packet recvPacket, RedisHelper redis)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;
            // bytes to roomname
            int dataSize = recvPacket.header.size;
            byte[] roomnameBytes = new byte[dataSize];
            Array.Copy(recvPacket.data, 0, roomnameBytes, 0, dataSize);

            string roomname = Encoding.UTF8.GetString(roomnameBytes).Trim();
            long roomId = redis.CreateRoom(roomname);

            if (-1 == roomId)
            {
                //make packet for room duplicate
                returnHeader = new Header(Comm.CS, Code.CREATE_DUPLICATE_ERR, 0);
                returnData = null;
            }
            else
            {
                //add room to dictionary
                Room requestedRoom = new Room(roomId, roomname);
                lock (rooms)
                    rooms.Add(roomId, requestedRoom);

                //make packet for room create success
                byte[] roomIdBytes = BitConverter.GetBytes(roomId);
                returnHeader = new Header(Comm.CS, Code.CREATE_RES, roomIdBytes.Length);
                returnData = roomIdBytes;
                /*
                createAndJoin = true;
                goto case Code.JOIN;
                */
            }
            response = new Packet(returnHeader, returnData);
            return response;
        }
        //================================================JOIN 500===============================================
        //================================================JOIN 500===============================================
        //================================================JOIN 500===============================================
        public Packet ResponseJoin(Packet recvPacket, bool createAndJoin)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            long roomId = 0;
            byte[] roomIdBytes;
            Room requestedRoom;

            if (createAndJoin)
                createAndJoin = false; //reuse roomId already set and set flag to false
            else
                roomId = ToInt64(recvPacket.data, 0);

            //TODO: allow client to join other room while in another room?
            //      or just allow to join when client is in lobby?

            lock (rooms)
            {
                if (!rooms.TryGetValue(roomId, out requestedRoom))
                {
                    byte[] sreqUserId = BitConverter.GetBytes(client.UserId);
                    byte[] sreqRoomId = BitConverter.GetBytes(roomId);
                    byte[] sreqData = new byte[sreqUserId.Length + sreqRoomId.Length];
                    Array.Copy(sreqUserId, 0, sreqData, 0, sreqUserId.Length);
                    Array.Copy(sreqRoomId, 0, sreqData, sreqUserId.Length, sreqRoomId.Length);

                    Header sreqHeader = new Header(Comm.SS, Code.SJOIN, sreqData.Length, (short)peerServers.Count);
                    Packet sreqPacket = new Packet(sreqHeader, sreqData);

                    if (peerServers.Count == 0)
                    {
                        returnHeader = new Header(Comm.CS, Code.JOIN_NULL_ERR, 0);
                        returnData = null;
                    }
                    else
                    {
                        //put user into peerRespWait so when peer response arrives
                        //there is a way to check if room doesn't exist.
                        lock (peerRespWait)
                        {
                            if (peerRespWait.ContainsKey(client.UserId))
                                Console.WriteLine("ERROR: JOIN - client already exists in peerRespWait");
                            else
                                peerRespWait.Add(client.UserId, new int[] { peerServers.Count, 0, 0 });
                        }

                        //room not in local server. check other servers
                        foreach (ServerHandle peer in peerServers)
                        {
                            bool success = peer.Send(sreqPacket);

                            if (!success)
                                Console.WriteLine("ERROR: SJOIN send failed");
                        }

                        returnHeader = NoResponseHeader;
                        returnData = null;
                    }
                }
                else
                {
                    lobbyClients.Remove(client.UserId);
                    requestedRoom.AddClient(client);
                    client.Status = ClientHandle.State.Room;
                    client.RoomId = roomId;

                    roomIdBytes = BitConverter.GetBytes(roomId);
                    returnHeader = new Header(Comm.CS, Code.JOIN_RES, roomIdBytes.Length);
                    returnData = roomIdBytes;
                }
            }

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //==============================================LEAVE 600===============================================
        //==============================================LEAVE 600===============================================
        //==============================================LEAVE 600===============================================
        public Packet ResponseLeave(Packet recvPacket)
        {
            Packet response;
            Header returnHeader = new Header();
            byte[] returnData = null;

            byte[] roomIdBytes;
            Room requestedRoom;

            if (client.Status != ClientHandle.State.Room)
            {
                //can't leave if client is in the lobby. must be in a room
                returnHeader = new Header(Comm.CS, Code.LEAVE_ERR, 0);
                returnData = null;
            }
            else
            {
                bool roomEmpty = false;
                lock (rooms)
                {
                    if (!rooms.TryGetValue(client.RoomId, out requestedRoom))
                    {
                        Console.WriteLine("ERROR: Client is in a room that doesn't exist. WTF you fucked up.");
                        returnHeader = new Header(Comm.CS, Code.LEAVE_ERR, 0);
                        returnData = null;

                        response = new Packet(returnHeader, returnData);
                        return response;
                    }
                    else
                    {
                        requestedRoom.RemoveClient(client);
                        client.RoomId = 0;
                        client.Status = ClientHandle.State.Lobby;

                        if (requestedRoom.Clients.Count == 0)
                        {
                            rooms.Remove(requestedRoom.RoomId);
                            roomEmpty = true;

                            returnHeader = new Header(Comm.CS, Code.LEAVE_RES, 0);
                            returnData = null;
                        }
                    }
                }

                lock (lobbyClients)
                {
                    if (lobbyClients.ContainsKey(client.UserId))
                        Console.WriteLine("ERROR: LEAVE - User already exists in lobby");
                    else
                        lobbyClients.Add(client.UserId, client);
                }

                if (roomEmpty)
                {
                    Packet reqPacket;
                    Header reqHeader;
                    roomIdBytes = BitConverter.GetBytes(requestedRoom.RoomId);
                    reqHeader = new Header(Comm.SS, Code.SLEAVE, roomIdBytes.Length);
                    reqPacket.header = reqHeader;
                    reqPacket.data = roomIdBytes;

                    foreach (ServerHandle peerServer in requestedRoom.Servers)
                        peerServer.EchoSend(reqPacket);
                }
            }

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //==============================================LIST 400===============================================
        //==============================================LIST 400===============================================
        //==============================================LIST 400===============================================
        public Packet ResponseList(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            if (!lobbyClients.ContainsKey(client.UserId))
                Console.WriteLine("you ain't in the lobby to list biatch");

            byte[] slistUserId = BitConverter.GetBytes(client.UserId);
            byte[] slistData = new byte[slistUserId.Length];

            Header slistHeader = new Header(Comm.SS, Code.SLIST, slistUserId.Length, (short)(peerServers.Count + 1)); // +1, because need to include self because server that will give LIST_RES response
            Packet slistPacket = new Packet(slistHeader, slistUserId);
            int peerServerCount = 1; //start is 1, because need to include self as server that will give LIST_RES response
                                     //room not in local server. check other servers
            foreach (ServerHandle peer in peerServers)
            {
                bool success = peer.Send(slistPacket);

                if (!success)
                    Console.WriteLine("ERROR: SJOIN send failed");
                else
                    peerServerCount++;
            }

            byte[] listBytes = GetRoomBytes();
            returnHeader = new Header(Comm.CS, Code.LIST_RES, listBytes.Length, (short)peerServerCount);
            returnData = listBytes;

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //==============================================MLIST 420===============================================
        //==============================================MLIST 420===============================================
        //==============================================MLIST 420===============================================
        public Packet ResponseMlist(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;
            client.Status = ClientHandle.State.Monitoring;

            int currentRoomCount = rooms.Count;
            int currentUserCount = lobbyClients.Count;
            List<long> roomIdList = new List<long>();


            if (0 > currentRoomCount || 0 > currentUserCount)
            {
                returnHeader = new Header(Comm.CS, Code.MLIST_ERR, 0);
                returnData = null;
            }
            else
            {
                // Get roomId of current room

                if (0 != rooms.Count)
                {
                    foreach (KeyValuePair<long, Room> pair in rooms)
                        roomIdList.Add(pair.Key);
                }
                else roomIdList.Clear();


                byte[] roomBytes = new byte[0];
                byte[] userBytes = BitConverter.GetBytes(currentUserCount);

                // list<long> to byte
                if (0 != roomIdList.Count)
                {
                    foreach (long id in roomIdList)
                        roomBytes = roomBytes.Concat(BitConverter.GetBytes(id)).ToArray();
                }
                else roomBytes = BitConverter.GetBytes((long)0);

                // returnData = new byte[ 4 + (8 * roomIdList.Count) ]
                returnData = new byte[userBytes.Length + roomBytes.Length];
                returnHeader = new Header(Comm.CS, Code.MLIST_RES, returnData.Length, recvPacket.header.sequence);

                if (userBytes != null || roomBytes != null)
                    returnData = userBytes.Concat(roomBytes).ToArray();
                else
                {
                    returnHeader = new Header(Comm.CS, Code.MLIST_ERR, 0);
                    returnData = null;
                }
            }

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //================================================MSG 200===============================================
        //================================================MSG 200===============================================
        //================================================MSG 200===============================================
        public Packet ResponseMsg(Packet recvPacket, RedisHelper redis)
        {
            Packet response;
            Header returnHeader = new Header();
            byte[] returnData = null;

            Room requestedRoom;

            //TODO: update user chat count. make it so that it increments value in redis
            if (!client.IsDummy)
            {
                client.ChatCount++;
                redis.IncrementUserChatCount(client.UserId);
                client.ChatCount = 0;
            }

            lock (rooms)
            {
                if (!rooms.TryGetValue(client.RoomId, out requestedRoom))
                {
                    Console.WriteLine("ERROR: Msg - Room doesn't exist");
                    // room doesnt exist error
                }
                else
                {
                    foreach (ClientHandle peerClient in requestedRoom.Clients)
                        peerClient.EchoSend(recvPacket);

                    recvPacket.header.comm = Comm.SS;
                    recvPacket.header.code = Code.SMSG;
                    foreach (ServerHandle peerServer in requestedRoom.Servers)
                        peerServer.EchoSend(recvPacket);

                    returnHeader = NoResponseHeader;
                    returnData = null;
                }
            }

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //===========================================SIGNIN 320===============================================
        //===========================================SIGNIN 320===============================================
        //===========================================SIGNIN 320===============================================
        public Packet ResponseSignin(Packet recvPacket, RedisHelper redis)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            byte[] usernameBytes;
            byte[] passwordBytes;
            string username;
            string password;
            long userId;

            //bytes to string
            usernameBytes = new byte[12];
            passwordBytes = new byte[18];
            Array.Copy(recvPacket.data, 0, usernameBytes, 0, 12);
            Array.Copy(recvPacket.data, 12, passwordBytes, 0, 18);

            username = Encoding.UTF8.GetString(usernameBytes).Trim();
            password = Encoding.UTF8.GetString(passwordBytes).Trim();
            userId = redis.SignIn(username, password);

            if (-1 == userId)
            {
                //make packet for signin error
                returnHeader = new Header(Comm.CS, Code.SIGNIN_ERR, 0);
                returnData = null;
            }
            else
            {
                try
                {
                    lobbyClients.Add(userId, client);
                    client.UserId = userId;
                    client.Status = ClientHandle.State.Lobby;

                    //make packet for signin success
                    returnHeader = new Header(Comm.CS, Code.SIGNIN_RES, 0);
                    returnData = null;
                }
                catch (Exception)
                {
                    Console.WriteLine("same userId added to lobby - you fucked up");
                    //make packet for signin fail
                    returnHeader = new Header(Comm.CS, Code.SIGNIN_ERR, 0);
                    returnData = null;
                }
            }

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //=========================================SIGNIN_DUMMY 330===============================================
        //=========================================SIGNIN_DUMMY 330===============================================
        //=========================================SIGNIN_DUMMY 330===============================================
        public Packet ResponseSigninDummy(Packet recvPacket, RedisHelper redis)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            long userId;

            userId = redis.CreateDummy();
            userId = redis.SignInDummy(userId);
            client.IsDummy = true;
            client.UserId = userId;
            client.Status = ClientHandle.State.Lobby;
            lobbyClients.Add(userId, client);
            //make packet for signin success
            returnHeader = new Header(Comm.CS, Code.SIGNIN_RES, 0);
            returnData = null;

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //===========================================SIGNUP 310=================================================
        //===========================================SIGNUP 310=================================================
        //===========================================SIGNUP 310=================================================
        public Packet ResponseSignup(Packet recvPacket, RedisHelper redis)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            byte[] usernameBytes;
            byte[] passwordBytes;
            string username;
            string password;
            long userId;

            //bytes to string
            usernameBytes = new byte[12];
            passwordBytes = new byte[18];
            Array.Copy(recvPacket.data, 0, usernameBytes, 0, 12);
            Array.Copy(recvPacket.data, 12, passwordBytes, 0, 18);

            username = Encoding.UTF8.GetString(usernameBytes).Trim();
            password = Encoding.UTF8.GetString(passwordBytes).Trim();
            userId = redis.CreateUser(username, password);

            if (-1 == userId)
            {
                //make packet for signup error
                returnHeader = new Header(Comm.CS, Code.SIGNUP_ERR, 0);
                returnData = null;
            }
            else
            {
                client.UserId = userId;
                //make packet for signup success
                returnHeader = new Header(Comm.CS, Code.SIGNUP_RES, 0);
                returnData = null;
            }

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //==============================================SJOIN 550===============================================
        //==============================================SJOIN 550===============================================
        //==============================================SJOIN 550===============================================
        public Packet ResponseSjoin(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            Room requestedRoom;
            long recvRoomId;
            string roomname = "";
            byte[] roomnameBytes;

            //receive UserID (8byte) + RoomId (8byte)
            recvRoomId = ToInt64(recvPacket.data, 8);
            bool haveRoom;
            lock (rooms)
            {
                haveRoom = rooms.ContainsKey(recvRoomId);
                if (haveRoom)
                {
                    if (rooms.TryGetValue(recvRoomId, out requestedRoom))
                    {
                        requestedRoom.AddServer(server);
                        roomname = requestedRoom.Roomname;
                    }
                }
            }

            if (haveRoom)
            {
                roomnameBytes = Encoding.UTF8.GetBytes(roomname);
                byte[] respBytes = new byte[recvPacket.data.Length + roomnameBytes.Length];
                Array.Copy(recvPacket.data, 0, respBytes, 0, 16);
                Array.Copy(roomnameBytes, 0, respBytes, 16, roomnameBytes.Length);
                returnHeader = new Header(Comm.SS, Code.SJOIN_RES, respBytes.Length, recvPacket.header.sequence);
                returnData = respBytes; //need to send back room id. so receiver can check again if they are the same id
            }
            else
            {
                returnHeader = new Header(Comm.SS, Code.SJOIN_ERR, recvPacket.data.Length, recvPacket.header.sequence);
                returnData = recvPacket.data;
            }
            //response should include roomid

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //===========================================SJOIN_RES 552===============================================
        //===========================================SJOIN_RES 552===============================================
        //===========================================SJOIN_RES 552===============================================
        public Packet ResponseSjoinRes(Packet recvPacket, out ClientHandle surrogateCandidate)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            Room requestedRoom;
            long userId;
            long recvRoomId;
            string recvRoomname;
            byte[] roomIdBytes;
            byte[] roomnameBytes;
            bool clientInLobby = true;

            //need to get 'client' because this function will return to
            //a ServerHandle instance, which will have a SS socket
            //so need to set surrogateClient so that the returnPacket is 
            //send through the surrogateClient
            userId = ToInt64(recvPacket.data, 0);
            recvRoomId = ToInt64(recvPacket.data, 8);
            roomnameBytes = new byte[recvPacket.data.Length - 16]; //16 because 8bytes for userid, 8bytes for roomid
            Array.Copy(recvPacket.data, 16, roomnameBytes, 0, roomnameBytes.Length);
            recvRoomname = Encoding.UTF8.GetString(roomnameBytes);

            lock (lobbyClients)
            {
                if (!lobbyClients.TryGetValue(userId, out client))
                {
                    //Console.WriteLine("ERROR: SJOIN_RES - user no longer exists");
                    clientInLobby = false;
                }
            }

            lock (rooms)
            {
                bool roomExists = rooms.TryGetValue(recvRoomId, out requestedRoom);
                if (clientInLobby && !roomExists)
                {
                    //first SJOIN_RES from peer servers
                    Room newJoinRoom = new Room(recvRoomId, recvRoomname);
                    lobbyClients.Remove(client.UserId);
                    newJoinRoom.AddClient(client);
                    newJoinRoom.AddServer(server);

                    rooms.Add(recvRoomId, newJoinRoom);

                    client.Status = ClientHandle.State.Room;
                    client.RoomId = recvRoomId;

                    //only the first SJOIN_RES will give the client a JOIN_RES response
                    //the others will use NoResponseHeader (see 'else' returnHeader assignment)
                    roomIdBytes = BitConverter.GetBytes(recvRoomId);
                    returnHeader = new Header(Comm.CS, Code.JOIN_RES, roomIdBytes.Length);
                    returnData = roomIdBytes;
                }
                else
                {
                    //room already made by previous iteration
                    //also, if clientInLobby == false, then add the server to the relay list
                    requestedRoom.AddServer(server);

                    //only the first SJOIN_RES will give the client a JOIN_RES response
                    //the others will use NoResponseHeader (see 'if' returnHeader assignment)
                    returnHeader = NoResponseHeader;
                    returnData = null;
                }
            }

            lock (peerRespWait)
            {
                int[] respProgress;
                if (!peerRespWait.TryGetValue(userId, out respProgress))
                {
                    Console.WriteLine("ERROR: SJOIN_RES - respProgress no longer exists");
                    returnHeader = NoResponseHeader;
                    returnData = null;
                }

                peerRespWait.Remove(userId); //just remove client.user_id when first success is received
            }
            //room exists in other peer irc servers
            //created one in local and connected with peers
            surrogateCandidate = client;

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //===========================================SJOIN_ERR 555===============================================
        //===========================================SJOIN_ERR 555===============================================
        //===========================================SJOIN_ERR 555===============================================
        public Packet ResponseSjoinErr(Packet recvPacket, out ClientHandle surrogateCandidate)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            long userId;
            long recvRoomId;
            //no such key exists error (no such room error)
            //need to get 'client' because this function will return to
            //a ServerHandle instance, which will have a SS socket
            //so need to set surrogateClient so that the returnPacket is 
            //send through the surrogateClient
            userId = ToInt64(recvPacket.data, 0);
            recvRoomId = ToInt64(recvPacket.data, 8);

            lock (lobbyClients)
            {
                if (!lobbyClients.TryGetValue(userId, out client))
                {
                    Console.WriteLine("ERROR: SJOIN_RES - user no longer exists");
                    returnHeader = NoResponseHeader;
                    returnData = null;
                    surrogateCandidate = null;

                    response = new Packet(returnHeader, returnData);
                    return response;
                }
            }

            lock (peerRespWait)
            {
                int[] respProgress;
                if (!peerRespWait.TryGetValue(userId, out respProgress))
                {
                    //respProgress no longer exists because success was sent and the key was deleted.
                    returnHeader = NoResponseHeader;
                    returnData = null;

                    surrogateCandidate = client;
                    response = new Packet(returnHeader, returnData);
                    return response;
                }

                respProgress[1]++; //increment progress
                respProgress[2] = respProgress[2] == 1 ? 1 : 0; //set join success to true
                if (respProgress[0] == respProgress[1] && respProgress[2] == 0)
                {
                    peerRespWait.Remove(userId);
                    returnHeader = new Header(Comm.CS, Code.JOIN_NULL_ERR, 0);
                    returnData = null;
                }
                else
                {
                    returnHeader = NoResponseHeader;
                    returnData = null;
                }
            }

            surrogateCandidate = client;
            response = new Packet(returnHeader, returnData);
            return response;
        }
        //==============================================SLEAVE 650===============================================
        //==============================================SLEAVE 650===============================================
        //==============================================SLEAVE 650===============================================
        public Packet ResponseSleave(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            Room requestedRoom;
            long recvRoomId;

            recvRoomId = ToInt64(recvPacket.data, 0);

            lock (rooms)
            {
                if (rooms.ContainsKey(recvRoomId))
                {
                    if (rooms.TryGetValue(recvRoomId, out requestedRoom))
                        requestedRoom.RemoveServer(server);
                }
            }

            //returnHeader = new Header(Comm.SS, Code.SLEAVE_RES, 0);
            returnHeader = NoResponseHeader;
            returnData = null;

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //==============================================SLIST 450===============================================
        //==============================================SLIST 450===============================================
        //==============================================SLIST 450===============================================
        public Packet ResponseSlist(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            byte[] roomBytes = GetRoomBytes();
            byte[] listBytes = new byte[roomBytes.Length + recvPacket.data.Length];
            Array.Copy(recvPacket.data, 0, listBytes, 0, recvPacket.data.Length);
            Array.Copy(roomBytes, 0, listBytes, recvPacket.data.Length, roomBytes.Length);

            returnHeader = new Header(Comm.SS, Code.SLIST_RES, listBytes.Length, recvPacket.header.sequence);
            returnData = listBytes;

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //==============================================SLIST_RES 452===============================================
        //==============================================SLIST_RES 452===============================================
        //==============================================SLIST_RES 452===============================================
        public Packet ResponseSlistRes(Packet recvPacket, out ClientHandle surrogateCandidate)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            long userId;

            userId = ToInt64(recvPacket.data, 0);

            lock (lobbyClients)
            {
                if (!lobbyClients.TryGetValue(userId, out client))
                    Console.WriteLine("ERROR: SJOIN_RES - user no longer exists");
            }

            surrogateCandidate = client;
            byte[] clientListBytes = new byte[recvPacket.data.Length - 8];
            Array.Copy(recvPacket.data, 8, clientListBytes, 0, clientListBytes.Length);
            returnHeader = new Header(Comm.CS, Code.LIST_RES, clientListBytes.Length, recvPacket.header.sequence);
            returnData = clientListBytes;

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //==============================================SMSG 250===============================================
        //==============================================SMSG 250===============================================
        //==============================================SMSG 250===============================================
        public Packet ResponseSmsg(Packet recvPacket)
        {
            Packet response;
            Header returnHeader = new Header();
            byte[] returnData = null;

            Room requestedRoom;
            byte[] roomIdBytes;

            roomIdBytes = new byte[8];
            Array.Copy(recvPacket.data, 0, roomIdBytes, 0, 8);
            long roomId = ToInt64(roomIdBytes, 0);

            lock (rooms)
            {
                if (!rooms.TryGetValue(roomId, out requestedRoom))
                {
                    Console.WriteLine("ERROR: SMSG - room doesn't exist {0}", roomId);
                }
                else
                {
                    recvPacket.header.comm = Comm.CS;
                    recvPacket.header.code = Code.MSG;
                    foreach (ClientHandle peerClient in requestedRoom.Clients)
                        peerClient.EchoSend(recvPacket);

                    returnHeader = NoResponseHeader;
                    returnData = null;
                }
            }

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //=============================================SWITCH CASE============================================
        //=============================================SWITCH CASE============================================
        //=============================================SWITCH CASE============================================
        public Packet GetResponse(out ClientHandle surrogateClient)
        {
            Packet responsePacket = new Packet();
            ClientHandle surrogateCandidate = null; //null is default

            string remoteHost = "";
            string remotePort = "";

            bool debug = true;

            if (debug && recvPacket.header.code != Code.HEARTBEAT && recvPacket.header.code != Code.HEARTBEAT_RES && recvPacket.header.code != Code.MLIST && recvPacket.header.code != -1)
            {
                if ("client" == accessor)
                {
                    remoteHost = ((IPEndPoint)client.So.RemoteEndPoint).Address.ToString();
                    remotePort = ((IPEndPoint)client.So.RemoteEndPoint).Port.ToString();
                    Console.WriteLine("\n[Client] {0}:{1}", remoteHost, remotePort);
                }
                else if ("server" == accessor)
                {
                    remoteHost = ((IPEndPoint)server.So.RemoteEndPoint).Address.ToString();
                    remotePort = ((IPEndPoint)server.So.RemoteEndPoint).Port.ToString();
                    Console.WriteLine("\n[Server] {0}:{1}", remoteHost, remotePort);
                }
                else
                    Console.WriteLine("ERROR: RECVHANDLER - accessor is not set");
                Console.WriteLine("==RECEIVED: \n" + PacketDebug(recvPacket));
            }

            //=============================COMM CS==============================
            if (Comm.CS == recvPacket.header.comm)
            {
                bool createAndJoin = false; //keep this! don't delete (in case we want to Create and Join together)

                switch (recvPacket.header.code)
                {
                    //------------No action from client----------
                    case -1:
                        responsePacket = new Packet(new Header(Comm.CS, Code.HEARTBEAT, 0), null);
                        break;

                    //------------CREATE------------
                    case Code.CREATE:
                        //CL -> FE side
                        responsePacket = ResponseCreate(recvPacket, redis);
                        break;

                    //------------DESTROY------------
                    case Code.DESTROY:
                        //CL -> FE side
                        break;

                    //------------FAIL------------
                    case Code.FAIL:
                        responsePacket = NoResponsePacket;
                        break;

                    //------------HEARTBEAT------------
                    case Code.HEARTBEAT_RES:
                        //CL -> FE side
                        responsePacket = NoResponsePacket;
                        break;

                    //------------JOIN------------
                    case Code.JOIN:
                        //CL -> FE side
                        responsePacket = ResponseJoin(recvPacket, createAndJoin);
                        break;

                    //------------LEAVE------------
                    case Code.LEAVE:
                        //CL -> FE side
                        responsePacket = ResponseLeave(recvPacket);
                        break;

                    //------------LIST------------
                    case Code.LIST:
                        //CL -> FE side
                        responsePacket = ResponseList(recvPacket);
                        break;

                    //------------MLIST-----------
                    case Code.MLIST:
                        //MCL -> FE side
                        responsePacket = ResponseMlist(recvPacket);
                        break;

                    //------------MSG------------
                    case Code.MSG:
                        //CL <--> FE side
                        responsePacket = ResponseMsg(recvPacket, redis);
                        break;
                    case Code.MSG_ERR:
                        //CL <--> FE side
                        break;

                    //------------SIGNIN------------
                    case Code.SIGNIN:
                        //CL -> FE -> BE side
                        responsePacket = ResponseSignin(recvPacket, redis);
                        break;
                    case Code.SIGNIN_DUMMY:
                        //CL -> FE
                        responsePacket = ResponseSigninDummy(recvPacket, redis);
                        break;

                    //------------SIGNUP------------
                    case Code.SIGNUP:
                        //CL -> FE -> BE side
                        responsePacket = ResponseSignup(recvPacket, redis);
                        break;

                    //------------SUCCESS------------
                    case Code.SUCCESS:
                        responsePacket = NoResponsePacket;
                        break;

                    default:
                        if (debug)
                            Console.WriteLine("Unknown code: {0}\n", recvPacket.header.code);
                        break;
                }
                surrogateCandidate = client;
            }
            //=============================COMM SS==============================
            else if (Comm.SS == recvPacket.header.comm)
            {
                switch (recvPacket.header.code)
                {
                    //------------No action from client----------
                    case -1:
                        responsePacket = new Packet(new Header(Comm.SS, Code.HEARTBEAT, 0), null);
                        break;

                    //------------HEARTBEAT------------
                    case Code.HEARTBEAT:
                        //FE -> CL side
                        responsePacket = new Packet(new Header(Comm.SS, Code.HEARTBEAT_RES, 0), null);
                        break;
                    case Code.HEARTBEAT_RES:
                        //CL -> FE side
                        responsePacket = NoResponsePacket;
                        break;

                    //------------SDESTROY------------
                    case Code.SDESTROY:
                        //FE side
                        break;
                    case Code.SDESTROY_ERR:
                        //FE side
                        break;

                    //------------SJOIN------------
                    case Code.SJOIN:
                        //FE side
                        responsePacket = ResponseSjoin(recvPacket);
                        break;
                    case Code.SJOIN_RES:
                        //FE side
                        responsePacket = ResponseSjoinRes(recvPacket, out surrogateCandidate);
                        break;
                    case Code.SJOIN_ERR:
                        //FE side
                        responsePacket = ResponseSjoinErr(recvPacket, out surrogateCandidate);
                        break;

                    //------------SLEAVE-----------
                    case Code.SLEAVE:
                        //FE side
                        responsePacket = ResponseSleave(recvPacket);
                        break;
                    case Code.SLEAVE_ERR:
                        //FE side
                        break;
                    case Code.SLEAVE_RES:
                        //FE side
                        break;

                    //------------SLIST------------
                    case Code.SLIST:
                        //FE side
                        responsePacket = ResponseSlist(recvPacket);
                        break;
                    case Code.SLIST_ERR:
                        //FE side
                        break;

                    case Code.SLIST_RES:
                        //FE side
                        responsePacket = ResponseSlistRes(recvPacket, out surrogateCandidate);
                        break;

                    //------------SMSG------------                
                    case Code.SMSG:
                        //FE side
                        responsePacket = ResponseSmsg(recvPacket);
                        break;
                    case Code.SMSG_ERR:
                        //FE side
                        break;
                }
            }
            //=============================COMM DS==============================
            else if (Comm.DUMMY == recvPacket.header.comm)
            {

            }

            //===============Build Response/Set Surrogate/Return================
            if (debug && responsePacket.header.comm != -1 && responsePacket.header.code != Code.HEARTBEAT && responsePacket.header.code != Code.HEARTBEAT_RES && responsePacket.header.code != Code.MLIST_RES && responsePacket.header.code != Code.MLIST_ERR)
            {
                Console.WriteLine("==SEND: \n" + PacketDebug(responsePacket));
                if ("client" == accessor)
                    Console.WriteLine("^[Client] {0}:{1}", remoteHost, remotePort);
                else if ("server" == accessor)
                {
                    if (null == surrogateCandidate)
                        Console.WriteLine("^[Server] {0}:{1}", remoteHost, remotePort);
                    else
                    {
                        remoteHost = ((IPEndPoint)surrogateCandidate.So.RemoteEndPoint).Address.ToString();
                        remotePort = ((IPEndPoint)surrogateCandidate.So.RemoteEndPoint).Port.ToString();
                        Console.WriteLine("^[Client] {0}:{1}", remoteHost, remotePort);
                    }
                }
                else
                    Console.WriteLine("ERROR: RECVHANDLER - accessor is not set");
            }

            surrogateClient = surrogateCandidate;
            return responsePacket;
        }

        private byte[] GetRoomBytes()
        {
            string[] pairArr = new string[rooms.Count];
            int length = 0;
            int i = 0;
            lock (rooms)
            {
                foreach (KeyValuePair<long, Room> entry in rooms)
                {
                    string pair = entry.Key + ":" + entry.Value.Roomname + ";";
                    pairArr[i] = pair;
                    i++;
                    length += Encoding.UTF8.GetByteCount(pair);
                }
            }

            byte[] listBytes = new byte[length];
            int prev = 0;
            for (int j = 0; j < pairArr.Length; j++)
            {
                byte[] pairBytes;
                string pair = pairArr[j];
                if (j == pairArr.Length - 1)
                    pairBytes = Encoding.UTF8.GetBytes(pair.Substring(0, pair.Length - 1)); //remove the last semicolon
                else
                    pairBytes = Encoding.UTF8.GetBytes(pair);

                Array.Copy(pairBytes, 0, listBytes, prev, pairBytes.Length);
                prev += pairBytes.Length;
            }

            return listBytes;
        }

        private long ToInt64(byte[] bytes, int startIndex)
        {
            long result = 0;
            try
            {
                result = BitConverter.ToInt64(bytes, startIndex);
            }
            catch (Exception)
            {
                Console.WriteLine("bytes to int64: fuck you. you messsed up");
            }

            return result;
        }

        private Socket ConnectToPeer(string info, string thisServerPort)
        {
            string[] hostport = info.Split(':');

            if ("127.0.0.1" == hostport[0] && thisServerPort == hostport[1]) //if its trying to connect to itself
                return null;
            else
            {
                string host;
                int port;

                host = hostport[0];
                if (!int.TryParse(hostport[1], out port))
                {
                    Console.Error.WriteLine("port must be int. given: {0}", hostport[1]);
                    Environment.Exit(0);
                }

                Socket so = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IPAddress ipAddress = IPAddress.Parse(host);
                Console.WriteLine("[Server] Establishing connection to {0}:{1} ...", host, port);

                try
                {
                    so.Connect(ipAddress, port);
                    //Console.WriteLine("[Server] Connection established.\n");
                }
                catch (Exception)
                {
                    Console.WriteLine("Peer is not alive.");
                    return null;
                }

                return so;
            }
        }
    }
}
