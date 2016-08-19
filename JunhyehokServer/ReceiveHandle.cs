using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Junhaehok;
using static Junhaehok.Packet;
using static Junhaehok.HhhHelper;

namespace JunhyehokServer
{
    class ReceiveHandle
    {
        ClientHandle client;
        Packet recvPacket;

        static Socket backend;
        static List<string> awaitingInit;
        static Dictionary<string, ClientHandle> lobbyClients;
        static Dictionary<long, Room> rooms;
        readonly Header NoResponseHeader = new Header(ushort.MaxValue, 0);
        readonly Packet NoResponsePacket = new Packet(new Header(ushort.MaxValue, 0), null);

        public ReceiveHandle(Socket backendSocket)
        {
            backend = backendSocket;
            awaitingInit = new List<string>();
            lobbyClients = new Dictionary<string, ClientHandle>();
            rooms = new Dictionary<long, Room>();
        }

        public ReceiveHandle(ClientHandle client, Packet recvPacket)
        {
            this.client = client;
            this.recvPacket = recvPacket;
        }

        public static void RemoveClient(ClientHandle client)
        {
            Header requestHeader;
            Packet requestPacket;
            if (client.Status == ClientHandle.State.Room)
            {
                //TODO: make struct for leave room
                requestHeader = new Header(Code.LEAVE_ROOM, 0);
                requestPacket = new Packet(requestHeader, null);

                bool success = backend.SendBytes(PacketToBytes(requestPacket));
                if(!success)
                {
                    Console.WriteLine("ERR: RemoveClient send to backend failed");
                    return;
                }

                //TODO: make struct for leave room
                requestHeader = new Header(Code.SIGNOUT, 0);
                requestPacket = new Packet(requestHeader, null);

                success = backend.SendBytes(PacketToBytes(requestPacket));
                if (!success)
                {
                    Console.WriteLine("ERR: RemoveClient send to backend failed");
                    return;
                }

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
                //TODO: make struct for leave room
                requestHeader = new Header(Code.SIGNOUT, 0);
                requestPacket = new Packet(requestHeader, null);

                bool success = backend.SendBytes(PacketToBytes(requestPacket));
                if (!success)
                {
                    Console.WriteLine("ERR: RemoveClient send to backend failed");
                    return;
                }

                lock (lobbyClients)
                    lobbyClients.Remove(client.UserId);
            }
            else
                Console.WriteLine("ERROR: REMOVECLIENT - you messed up");
        }

        //===========================================INITIALIZE 250==============================================
        //===========================================INITIALIZE 250==============================================
        //===========================================INITIALIZE 250==============================================
        public Packet ResponseInitialize(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            string cookie = Encoding.UTF8.GetString(recvPacket.data);
            bool authorized = false;
            lock (awaitingInit)
            {
                if (awaitingInit.Contains(cookie))
                {
                    awaitingInit.Remove(cookie);
                    authorized = true;
                }
            }
            if (authorized)
            {
                lock (lobbyClients)
                    lobbyClients.Add(cookie, client);
            }

            returnData = null;
            returnHeader = new Header(Code.INITIALIZE_SUCCESS, 0);
            response = new Packet(returnHeader, returnData);

            return response;
        }
        //==============================================CREATE 700===============================================
        //==============================================CREATE 700===============================================
        //==============================================CREATE 700===============================================
        public Packet ResponseCreate(Packet recvPacket)
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
                returnHeader = new Header(Code.CREATE_ROOM_FAIL, 0);
                returnData = null;
                response = new Packet(returnHeader, returnData);
            }
            else
            {
                //add room to dictionary
                Room requestedRoom = new Room(roomId, roomname);
                lock (rooms)
                    rooms.Add(roomId, requestedRoom);

                //make packet for room create success
                /*
                byte[] roomIdBytes = BitConverter.GetBytes(roomId);
                returnHeader = new Header(Code.CREATE_ROOM_SUCCESS, (ushort)roomIdBytes.Length);
                returnData = roomIdBytes;
                */

                response = ResponseJoin(recvPacket, true);
            }
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
        //========================================SIGNIN_SUCCESS   ===============================================
        //========================================SIGNIN_SUCCESS   ===============================================
        //========================================SIGNIN_SUCCESS   ===============================================
        public Packet ResponseSigninSuccess(Packet recvPacket)
        {
            string cookie = Encoding.UTF8.GetString(recvPacket.data);
            lock (awaitingInit)
            {
                if (awaitingInit.Contains(cookie))
                    Console.WriteLine("SigninSuccess : someone fucked up");
                else
                    awaitingInit.Add(cookie);
            }

            return NoResponsePacket;
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
        public Packet GetResponse()
        {
            Packet responsePacket = new Packet();

            string remoteHost = "";
            string remotePort = "";
            bool debug = true;

            if (debug && recvPacket.header.code != Code.HEARTBEAT && recvPacket.header.code != Code.HEARTBEAT_SUCCESS && recvPacket.header.code != ushort.MaxValue)
            {
                remoteHost = ((IPEndPoint)client.So.RemoteEndPoint).Address.ToString();
                remotePort = ((IPEndPoint)client.So.RemoteEndPoint).Port.ToString();
                Console.WriteLine("\n[Client] {0}:{1}", remoteHost, remotePort);
                Console.WriteLine("==RECEIVED: \n" + PacketDebug(recvPacket));
            }

                bool createAndJoin = false; //keep this! don't delete (in case we want to Create and Join together)

            switch (recvPacket.header.code)
            {
                //------------No action from client----------
                case ushort.MaxValue:
                    responsePacket = new Packet(new Header(Code.HEARTBEAT, 0), null);
                    break;

                //------------CREATE------------
                case Code.CREATE_ROOM:
                    //CL -> FE side
                    responsePacket = ResponseCreate(recvPacket);
                    break;

                //------------DESTROY------------
                case Code.DESTROY_ROOM:
                    //CL -> FE side
                    break;

                //------------HEARTBEAT------------
                case Code.HEARTBEAT:
                    //FE -> CL side
                    responsePacket = new Packet(new Header(Code.HEARTBEAT_SUCCESS, 0), null);
                    break;
                case Code.HEARTBEAT_SUCCESS:
                    //CL -> FE side
                    responsePacket = NoResponsePacket;
                    break;

                //------------JOIN------------
                case Code.JOIN:
                    //CL -> FE side
                    responsePacket = ResponseJoin(recvPacket, createAndJoin);
                    break;

                //------------LEAVE------------
                case Code.LEAVE_ROOM:
                    //CL -> FE side
                    responsePacket = ResponseLeave(recvPacket);
                    break;

                //------------LIST------------
                case Code.ROOM_LIST:
                    //CL -> FE side
                    responsePacket = ResponseList(recvPacket);
                    break;

                //------------MSG------------
                case Code.MSG:
                    //CL <--> FE side
                    responsePacket = ResponseMsg(recvPacket);
                    break;
                case Code.MSG_FAIL:
                    //CL <--> FE side
                    break;

                //------------SIGNIN------------
                case Code.SIGNIN:
                    //CL -> FE -> BE side
                    responsePacket = ResponseSignin(recvPacket);
                    break;
                case Code.SIGNIN_SUCCESS:
                    responsePacket = ResponseSigninSuccess(recvPacket);
                    break;
                case Code.DUMMY_SIGNIN:
                    //CL -> FE
                    responsePacket = ResponseSigninDummy(recvPacket);
                    break;

                //------------SIGNUP------------
                case Code.SIGNUP:
                    //CL -> FE -> BE side
                    responsePacket = ResponseSignup(recvPacket);
                    break;

                default:
                    if (debug)
                        Console.WriteLine("Unknown code: {0}\n", recvPacket.header.code);
                    break;
            }

            //===============Build Response/Set Surrogate/Return================
            if (debug && responsePacket.header.code != ushort.MaxValue && responsePacket.header.code != Code.HEARTBEAT && responsePacket.header.code != Code.HEARTBEAT_SUCCESS)
            {
                Console.WriteLine("==SEND: \n" + PacketDebug(responsePacket));
                Console.WriteLine("^[Client] {0}:{1}", remoteHost, remotePort);
            }

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
    }
}
