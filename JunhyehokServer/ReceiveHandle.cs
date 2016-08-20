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
        //TODO: ALWAYS CHECK IF CONNECTION IN AWAITINGINIT
        ClientHandle client;
        Packet recvPacket;

        static Socket backend;
        static Dictionary<string, long> awaitingInit;
        static Dictionary<long, ClientHandle> clients;
        static Dictionary<int, Room> rooms;
        readonly Header NoResponseHeader = new Header(ushort.MaxValue, 0);
        readonly Packet NoResponsePacket = new Packet(new Header(ushort.MaxValue, 0), null);

        public ReceiveHandle(Socket backendSocket)
        {
            backend = backendSocket;
            awaitingInit = new Dictionary<string, long>();
            clients = new Dictionary<long, ClientHandle>();
            rooms = new Dictionary<int, Room>();
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
            byte[] requestData;
            if (client.Status == ClientHandle.State.Room || client.Status == ClientHandle.State.Lobby)
            {
                bool success = false;
                if (client.Status == ClientHandle.State.Room)
                {
                    FBRoomLeaveRequest fbRoomLeaveReq;
                    fbRoomLeaveReq.cookie = client.CookieChar;
                    requestData = Serializer.StructureToByte(fbRoomLeaveReq);
                    requestHeader = new Header(Code.LEAVE_ROOM, (ushort)requestData.Length);
                    requestPacket = new Packet(requestHeader, requestData);

                    success = backend.SendBytes(requestPacket);
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

                FBSignoutRequest fbSignoutReq;
                fbSignoutReq.cookie = client.CookieChar;
                requestData = Serializer.StructureToByte(fbSignoutReq);
                requestHeader = new Header(Code.SIGNOUT, (ushort)requestData.Length);
                requestPacket = new Packet(requestHeader, requestData);

                success = backend.SendBytes(requestPacket);
                if (!success)
                {
                    Console.WriteLine("ERR: RemoveClient send to backend failed");
                    return;
                }

                lock (clients)
                    clients.Remove(client.UserId);
            }
            else
                Console.WriteLine("ERROR: REMOVECLIENT - you messed up");
        }
        //========================================CONNECTION_PASS 650=============================================
        //========================================CONNECTION_PASS 650=============================================
        //========================================CONNECTION_PASS 650=============================================
        public Packet ResponseConnectionPass(Packet recvPacket)
        {
            FBConnectionPassResponse fbConnectionPassResp = (FBConnectionPassResponse)Serializer.ByteToStructure(recvPacket.data, typeof(FBConnectionPassResponse));
            char[] cookieChar = fbConnectionPassResp.cookie;
            string cookie = cookieChar.ToString();
            lock (awaitingInit)
            {
                if (awaitingInit.ContainsKey(cookie))
                    awaitingInit[cookie] = recvPacket.header.uid;
                else
                    awaitingInit.Add(cookie, recvPacket.header.uid);
            }

            return NoResponsePacket;
        }
        //===========================================INITIALIZE 250==============================================
        //===========================================INITIALIZE 250==============================================
        //===========================================INITIALIZE 250==============================================
        public Packet ResponseInitialize(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            CFInitializeRequest cfInitializeReq = (CFInitializeRequest)Serializer.ByteToStructure(recvPacket.data, typeof(CFInitializeRequest));
            char[] cookieChar = cfInitializeReq.cookie;
            string cookie = cookieChar.ToString();
            long uid;
            bool authorized = false;
            lock (awaitingInit)
            {
                if (awaitingInit.TryGetValue(cookie, out uid))
                {
                    awaitingInit.Remove(cookie);
                    authorized = true;
                }
            }
            if (authorized)
            {
                client.UserId = uid;
                client.CookieChar = cookieChar;
                lock (clients)
                    clients.Add(client.UserId, client);

                returnData = null;
                returnHeader = new Header(Code.INITIALIZE_SUCCESS, 0);
                response = new Packet(returnHeader, returnData);

                Header backendReqHeader = new Header(Code.CONNECTION_PASS_SUCCESS, 0, client.UserId);
                Packet backendReqPacket = new Packet(backendReqHeader, null);
                backend.SendBytes(backendReqPacket);
            }
            else
            {
                returnData = null;
                returnHeader = new Header(Code.INITIALIZE_FAIL, 0);
                response = new Packet(returnHeader, returnData);
            }

            return response;
        }
        //==============================================CREATE 500===============================================
        //==============================================CREATE 500===============================================
        //==============================================CREATE 500===============================================
        public Packet ResponseCreate(Packet recvPacket)
        {
            Header backendReqHeader = new Header(Code.CREATE_ROOM, 0, client.UserId);
            Packet backendReqPacket = new Packet(backendReqHeader, null);
            backend.SendBytes(backendReqPacket);
            return NoResponsePacket;
        }
        //=======================================CREATE_SUCCESS 502==============================================
        //=======================================CREATE_SUCCESS 502==============================================
        //=======================================CREATE_SUCCESS 502==============================================
        public Packet ResponseCreateSuccess(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                FBRoomCreateResponse fbRoomCreateResp = (FBRoomCreateResponse)Serializer.ByteToStructure(recvPacket.data, typeof(FBRoomCreateResponse));
                int roomId = fbRoomCreateResp.roomNum;
                //add room to dictionary
                Room requestedRoom = new Room(roomId);
                lock (rooms)
                {
                    rooms.Add(roomId, requestedRoom);
                    requestedRoom.AddClient(client);
                    client.Status = ClientHandle.State.Room;
                    client.RoomId = roomId;
                }
                
                //send CREATE_ROOM_SUCCESS back to client
                Header clientRespHeader = new Header(Code.CREATE_ROOM_SUCCESS, 0);
                Packet clientRespPacket = new Packet(clientRespHeader, null);
                clientToSend.So.SendBytes(clientRespPacket);

                //send JOIN to Backend
                FBRoomJoinRequest fbRoomJoinReq;
                fbRoomJoinReq.cookie = clientToSend.CookieChar;
                fbRoomJoinReq.roomNum = roomId;
                byte[] fbRoomJoinReqBytes = Serializer.StructureToByte(fbRoomJoinReq);
                returnHeader = new Header(Code.JOIN, (ushort)fbRoomJoinReqBytes.Length, clientToSend.UserId);
                response = new Packet(returnHeader, fbRoomJoinReqBytes);
            }
            else
                response = NoResponsePacket;
            
            //joinsuccess -> remove from lobby and add to room (no response to backend, no response to client)
            return response;
        }
        //=======================================CREATE_FAIL 505=================================================
        //=======================================CREATE_FAIL 505=================================================
        //=======================================CREATE_FAIL 505=================================================
        public Packet ResponseCreateFail(Packet recvPacket)
        {
            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                //forward received packet after removing uid from header
                recvPacket.header.uid = 0;
                clientToSend.So.SendBytes(recvPacket);
            }

            return NoResponsePacket;
        }
        //================================================JOIN 600===============================================
        //================================================JOIN 600===============================================
        //================================================JOIN 600===============================================
        public Packet ResponseJoin(Packet recvPacket)
        {
            //NORESPONSE
            CFRoomJoinRequest cfRoomJoinReq = (CFRoomJoinRequest)Serializer.ByteToStructure(recvPacket.data, typeof(CFRoomJoinRequest));
            int roomId = cfRoomJoinReq.roomNum;

            FBRoomJoinRequest fbRoomJoinReq;
            fbRoomJoinReq.cookie = client.CookieChar;
            fbRoomJoinReq.roomNum = roomId;
            byte[] fbRoomJoinReqBytes = Serializer.StructureToByte(fbRoomJoinReq);

            Header backendReqHeader = new Header(Code.JOIN, (ushort)fbRoomJoinReqBytes.Length, client.UserId);
            Packet backendReqPacket = new Packet(backendReqHeader, fbRoomJoinReqBytes);
            backend.SendBytes(backendReqPacket);

            Room requestedRoom;
            lock (rooms)
            {
                if (rooms.TryGetValue(roomId, out requestedRoom))
                {
                    requestedRoom.AddClient(client);
                    client.Status = ClientHandle.State.Room;
                    client.RoomId = roomId;
                }
            }
            
            return NoResponsePacket;
        }
        //=======================================JOIN_SUCCESS 602===============================================
        //=======================================JOIN_SUCCESS 602===============================================
        //=======================================JOIN_SUCCESS 602===============================================
        public Packet ResponseJoinSuccess(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                //send JOIN_SUCCESS to client, actual room join and state change is JOIN
                returnHeader = new Header(Code.JOIN_SUCCESS, 0);
                response = new Packet(returnHeader, null);
                clientToSend.So.SendBytes(response);

                //send nothing back to Backend
                response = NoResponsePacket;
            }
            else
                response = NoResponsePacket;

            return response;
        }
        //==========================================JOIN_FAIL 605===============================================
        //==========================================JOIN_FAIL 605===============================================
        //==========================================JOIN_FAIL 605===============================================
        public Packet ResponseJoinFail(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                returnHeader = new Header(recvPacket.header.code, 0);
                response = new Packet(returnHeader, null);
                clientToSend.So.SendBytes(response);

                //send nothing back to Backend
                response = NoResponsePacket;
            }
            else
                response = NoResponsePacket;

            return response;
        }
        //========================================JOIN_REDIRECT 605=============================================
        //========================================JOIN_REDIRECT 605=============================================
        //========================================JOIN_REDIRECT 605=============================================
        public Packet ResponseJoinRedirect(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                returnHeader = recvPacket.header;
                returnHeader.uid = 0;
                response = new Packet(returnHeader, recvPacket.data);
                clientToSend.So.SendBytes(response);

                lock (clients)
                    clients.Remove(clientToSend.UserId);
                clientToSend.CloseConnection();

                //send nothing back to Backend
                response = NoResponsePacket;
            }
            else
                response = NoResponsePacket;

            return response;
        }
        //==============================================LEAVE 600===============================================
        //==============================================LEAVE 600===============================================
        //==============================================LEAVE 600===============================================
        public Packet ResponseLeave(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;
            Room requestedRoom;

            if (client.Status != ClientHandle.State.Room)
            {
                //can't leave if client is in the lobby. must be in a room
                returnHeader = new Header(Code.LEAVE_ROOM_FAIL, 0);
                returnData = null;
            }
            else
            {
                lock (rooms)
                {
                    if (!rooms.TryGetValue(client.RoomId, out requestedRoom))
                    {
                        Console.WriteLine("ERROR: Client is in a room that doesn't exist. WTF you fucked up.");
                        returnHeader = new Header(Code.LEAVE_ROOM_FAIL, 0);
                        returnData = null;                        
                    }
                    else
                    {
                        Header backendReqHeader = new Header(Code.LEAVE_ROOM, 0, client.UserId);
                        Packet backendReqPacket = new Packet(backendReqHeader, null);
                        backend.SendBytes(backendReqPacket);

                        requestedRoom.RemoveClient(client);
                        client.RoomId = 0;
                        client.Status = ClientHandle.State.Lobby;

                        returnHeader = NoResponseHeader;
                        returnData = null;
                    }
                }
            }

            response = new Packet(returnHeader, returnData);
            return response;
        }
        //=======================================LEAVE_ROOM_FAIL 705===========================================
        //=======================================LEAVE_ROOM_FAIL 705===========================================
        //=======================================LEAVE_ROOM_FAIL 705===========================================
        public Packet ResponseLeaveRoomFail(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                returnHeader = recvPacket.header;
                returnHeader.uid = 0;
                response = new Packet(returnHeader, recvPacket.data);
                clientToSend.So.SendBytes(response);

                //send nothing back to Backend
                response = NoResponsePacket;
            }
            else
                response = NoResponsePacket;

            return response;
        }
        //=====================================LEAVE_ROOM_SUCCESS 702==========================================
        //=====================================LEAVE_ROOM_SUCCESS 702==========================================
        //=====================================LEAVE_ROOM_SUCCESS 702==========================================
        public Packet ResponseLeaveRoomSuccess(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                returnHeader = recvPacket.header;
                returnHeader.uid = 0;
                response = new Packet(returnHeader, recvPacket.data);
                clientToSend.So.SendBytes(response);

                //send nothing back to Backend
                response = NoResponsePacket;
            }
            else
                response = NoResponsePacket;

            return response;
        }
        //==============================================LIST 400===============================================
        //==============================================LIST 400===============================================
        //==============================================LIST 400===============================================
        public Packet ResponseList(Packet recvPacket)
        {
            Header backendReqHeader = new Header(Code.ROOM_LIST, 0, client.UserId);
            Packet backendReqPacket = new Packet(backendReqHeader, null);
            backend.SendBytes(backendReqPacket);

            return NoResponsePacket;
        }
        //==========================================LIST_SUCCESS 402===========================================
        //==========================================LIST_SUCCESS 402===========================================
        //==========================================LIST_SUCCESS 402===========================================
        public Packet ResponseListSuccess(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                returnHeader = recvPacket.header;
                returnHeader.uid = 0;
                response = new Packet(returnHeader, recvPacket.data);
                clientToSend.So.SendBytes(response);

                //send nothing back to Backend
                response = NoResponsePacket;
            }
            else
                response = NoResponsePacket;

            return response;
        }
        //==========================================LIST_FAIL 405==============================================
        //==========================================LIST_FAIL 405==============================================
        //==========================================LIST_FAIL 405==============================================
        public Packet ResponseListFail(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                returnHeader = recvPacket.header;
                returnHeader.uid = 0;
                response = new Packet(returnHeader, recvPacket.data);
                clientToSend.So.SendBytes(response);

                //send nothing back to Backend
                response = NoResponsePacket;
            }
            else
                response = NoResponsePacket;

            return response;
        }
        //================================================MSG 200===============================================
        //================================================MSG 200===============================================
        //================================================MSG 200===============================================
        public Packet ResponseMsg(Packet recvPacket)
        {
            Room requestedRoom;

            //TODO: update user chat count. make it so that it increments value in redis
            if (!client.IsDummy)
            {
                client.ChatCount++;
                //redis.IncrementUserChatCount(client.UserId);
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
                        peerClient.So.SendBytes(recvPacket);
                }
            }

            return NoResponsePacket;
        }
        //=============================================SWITCH CASE============================================
        //=============================================SWITCH CASE============================================
        //=============================================SWITCH CASE============================================
        public Packet GetResponse()
        {
            if (!HasInitialized())
                return new Packet(new Header(Code.INITIALIZE_FAIL, 0), null);

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

            switch (recvPacket.header.code)
            {
                //------------No action from client----------
                case ushort.MaxValue-1:
                    responsePacket = new Packet(new Header(Code.HEARTBEAT, 0), null);
                    break;

                //--------CONNECTION_PASS-------
                case Code.CONNECTION_PASS:
                    responsePacket = ResponseConnectionPass(recvPacket);
                    break;

                //----------INITIALIZE----------
                case Code.INITIALIZE:
                    responsePacket = ResponseInitialize(recvPacket);
                    break;

                //------------CREATE------------
                case Code.CREATE_ROOM:
                    //CL -> FE side
                    responsePacket = ResponseCreate(recvPacket);
                    break;
                case Code.CREATE_ROOM_SUCCESS:
                    responsePacket = ResponseCreateSuccess(recvPacket);
                    break;
                case Code.CREATE_ROOM_FAIL:
                    responsePacket = ResponseCreateFail(recvPacket);
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
                    responsePacket = ResponseJoin(recvPacket);
                    break;
                case Code.JOIN_FAIL:
                    responsePacket = ResponseJoinFail(recvPacket);
                    break;
                case Code.JOIN_SUCCESS:
                    responsePacket = ResponseJoinSuccess(recvPacket);
                    break;
                case Code.JOIN_REDIRECT:
                    responsePacket = ResponseJoinRedirect(recvPacket);
                    break;

                //------------LEAVE------------
                case Code.LEAVE_ROOM:
                    //CL -> FE side
                    responsePacket = ResponseLeave(recvPacket);
                    break;
                case Code.LEAVE_ROOM_FAIL:
                    responsePacket = ResponseLeaveRoomFail(recvPacket);
                    break;
                case Code.LEAVE_ROOM_SUCCESS:
                    responsePacket = ResponseLeaveRoomSuccess(recvPacket);
                    break;

                //------------LIST------------
                case Code.ROOM_LIST:
                    //CL -> FE side
                    responsePacket = ResponseList(recvPacket);
                    break;
                case Code.ROOM_LIST_SUCCESS:
                    responsePacket = ResponseListSuccess(recvPacket);
                    break;
                case Code.ROOM_LIST_FAIL:
                    responsePacket = ResponseListFail(recvPacket);
                    break;

                //------------MSG------------
                case Code.MSG:
                    //CL <--> FE side
                    responsePacket = ResponseMsg(recvPacket);
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

        private ClientHandle GetClientFromUid(long uid)
        {
            ClientHandle clientToSend;
            if (!clients.TryGetValue(uid, out clientToSend))
            {
                Console.WriteLine("WARNING: GetClientFromUid failed - client no longer exists");
                return null;
            }
            return clientToSend;
        }
        private bool HasInitialized()
        {
            return !(client.UserId == -1 || client.Cookie == null);
        }
    }
}
